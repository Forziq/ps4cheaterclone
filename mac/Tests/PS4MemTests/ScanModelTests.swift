import XCTest
@testable import PS4Mem

/// A scan status as the API writes it (camelCase, nulls included).
func scanStatusJSON(state: String = "done", kind: String? = "first", percent: Double = 100, hits: Int64 = 0,
                    scanNumber: Int = 1, resultCount: Int64 = 1234, type: String? = "u32",
                    canUndo: Bool = false, message: String? = nil, pid: Int? = 85,
                    elapsed: Double = 3.25) -> String {
    func text(_ s: String?) -> String { s.map { "\"\($0)\"" } ?? "null" }
    return """
    {"state":"\(state)","kind":\(text(kind)),"query":"u32 exact 100","method":"local","percent":\(percent),
     "section":"(unnamed)","done":500,"total":1000,"hits":\(hits),"elapsedSeconds":\(elapsed),
     "message":\(text(message)),"gamePaused":false,"pid":\(pid.map(String.init) ?? "null"),
     "scanNumber":\(scanNumber),"resultCount":\(resultCount),"type":\(text(type)),"valueSize":4,
     "resultMethod":"local","canUndo":\(canUndo),
     "sections":[{"name":"(unnamed)","start":"0x8000000000","size":3103784960,"hits":1234,"milliseconds":3100.5}]}
    """
}

/// One SSE event as the backend writes it (data on one line).
func sseEvent(_ name: String, _ json: String) -> String {
    "event: \(name)\ndata: \(json.replacingOccurrences(of: "\n", with: ""))\n\n"
}

final class ServerSentEventsTests: XCTestCase {
    func testDataLineCompletesTheEventWithOrWithoutBlankLines() {
        var parser = ServerSentEvents()
        XCTAssertNil(parser.feed("event: progress"))
        XCTAssertEqual(parser.feed("data: {\"a\":1}"), .init(name: "progress", data: "{\"a\":1}"))
        XCTAssertNil(parser.feed(""))
        // No blank line between events (a line reader that skips them).
        XCTAssertNil(parser.feed("event: done"))
        XCTAssertEqual(parser.feed("data:{}"), .init(name: "done", data: "{}"))
        XCTAssertEqual(parser.feed("data: x"), .init(name: "message", data: "x"), "the name doesn't carry over")
        XCTAssertNil(parser.feed(": keep-alive comment"))
    }
}

final class ScanOptionsTests: XCTestCase {
    func testTypeNamesMatchTheBackend() {
        XCTAssertEqual(ValueKind.byte4.typeName(signed: false), "u32")
        XCTAssertEqual(ValueKind.byte1.typeName(signed: true), "i8")
        XCTAssertEqual(ValueKind.byte8.typeName(signed: true), "i64")
        XCTAssertEqual(ValueKind.float.typeName(signed: true), "f32", "floats have no unsigned form")
        XCTAssertEqual(ValueKind.bytes.typeName(signed: false), "bytes")
        XCTAssertEqual(ValueKind.from(typeName: "i16")?.kind, .byte2)
        XCTAssertEqual(ValueKind.from(typeName: "i16")?.signed, true)
        XCTAssertEqual(ValueKind.from(typeName: "f64")?.kind, .double)
        XCTAssertNil(ValueKind.from(typeName: "nope"))
    }

    func testComparesForFirstAndNextScans() {
        XCTAssertEqual(CompareKind.available(for: .byte4, nextScan: false),
                       [.exact, .biggerThan, .smallerThan, .between, .unknownInitial])
        let next = CompareKind.available(for: .byte4, nextScan: true)
        XCTAssertFalse(next.contains(.unknownInitial))
        XCTAssertTrue(next.contains(.increasedBy))
        XCTAssertEqual(next.count, 10)
        XCTAssertEqual(CompareKind.available(for: .string, nextScan: false), [.exact])
        XCTAssertEqual(CompareKind.available(for: .bytes, nextScan: true), [.exact, .changed, .unchanged])
    }

    func testValueFieldsFollowTheCompare() {
        XCTAssertEqual(CompareKind.between.valuePrompts.count, 2)
        XCTAssertEqual(CompareKind.exact.valuePrompts, ["Value"])
        XCTAssertEqual(CompareKind.increased.valuePrompts, [])
        XCTAssertEqual(CompareKind.unknownInitial.valueCount, 0)
        XCTAssertEqual(CompareKind.biggerThan.apiName, "bigger than")
    }
}

@MainActor
final class ScanModelTests: XCTestCase {
    override func setUp() async throws { StubURLProtocol.reset() }
    override func tearDown() async throws { StubURLProtocol.reset() }

    private func body(of path: String) -> [String: Any]? {
        guard let i = StubURLProtocol.requests.lastIndex(where: { $0.url?.path == path }) else { return nil }
        return try? JSONSerialization.jsonObject(with: Data(StubURLProtocol.bodies[i].utf8)) as? [String: Any]
    }

    private var paths: [String] { StubURLProtocol.requests.compactMap { $0.url?.path } }

    func testFirstScanSendsTheQueryAndFollowsTheProgressStream() async {
        let running = scanStatusJSON(state: "running", percent: 0, scanNumber: 0, resultCount: 0, type: nil)
        StubURLProtocol.handler = { request in
            switch request.url?.path {
            case "/api/scan/first": return .init(status: 202, body: running)
            case "/api/scan/progress":
                return .init(status: 200, body:
                    sseEvent("progress", scanStatusJSON(state: "running", percent: 50, hits: 700, scanNumber: 0,
                                                        resultCount: 0, type: nil))
                    + sseEvent("done", scanStatusJSON()))
            default: return .init(status: 404, body: "{\"error\":\"no\"}")
            }
        }
        let model = ScanModel(client: stubClient(), pollInterval: 0.01)
        var changed: ScanStatus?
        model.onResultsChanged = { changed = $0 }
        model.value = "100"

        XCTAssertTrue(model.canFirstScan(pid: 85, sectionCount: 1))
        XCTAssertFalse(model.canFirstScan(pid: 85, sectionCount: 0), "needs a ticked section")
        await model.firstScan(pid: 85, sections: ["0x8000000000"])

        let sent = body(of: "/api/scan/first")
        XCTAssertEqual(sent?["pid"] as? Int, 85)
        XCTAssertEqual(sent?["type"] as? String, "u32")
        XCTAssertEqual(sent?["compare"] as? String, "exact")
        XCTAssertEqual(sent?["value"] as? String, "100")
        XCTAssertNil(sent?["value2"], "exact sends one value")
        XCTAssertEqual(sent?["sections"] as? [String], ["0x8000000000"])
        XCTAssertEqual(sent?["aligned"] as? Bool, true)
        XCTAssertEqual(sent?["method"] as? String, "local", "scans on the Mac by default")
        XCTAssertEqual(sent?["pauseGame"] as? Bool, false)
        XCTAssertEqual(paths, ["/api/scan/first", "/api/scan/progress"])

        XCTAssertEqual(model.status.state, "done")
        XCTAssertEqual(model.status.resultCount, 1234)
        XCTAssertEqual(changed?.scanNumber, 1)
        XCTAssertTrue(model.hasResults)
        XCTAssertTrue(model.typeLocked)
        XCTAssertFalse(model.isFollowing)
        XCTAssertEqual(model.statusText, "1,234 results · scan 1 took 3.2 s")
        XCTAssertTrue(model.availableCompares.contains(.increased), "next-scan compares after results")
    }

    func testFirstScanSendsTheConsoleMethodWhenChosen() async {
        StubURLProtocol.handler = { request in
            switch request.url?.path {
            case "/api/scan/first": return .init(status: 202, body: scanStatusJSON(state: "running", percent: 0,
                                                                                    scanNumber: 0, resultCount: 0, type: nil))
            case "/api/scan/progress": return .init(status: 200, body: sseEvent("done", scanStatusJSON()))
            default: return .init(status: 404, body: "{\"error\":\"no\"}")
            }
        }
        let model = ScanModel(client: stubClient(), pollInterval: 0.01)
        model.value = "100"
        model.method = .console
        await model.firstScan(pid: 85, sections: ["0x8000000000"])

        XCTAssertEqual(body(of: "/api/scan/first")?["method"] as? String, "console")
    }

    func testFallsBackToStatusChecksWhenTheStreamFails() async {
        var statusCalls = 0
        StubURLProtocol.handler = { request in
            switch request.url?.path {
            case "/api/scan/first":
                return .init(status: 202, body: scanStatusJSON(state: "running", percent: 0, scanNumber: 0, resultCount: 0, type: nil))
            case "/api/scan/progress": return .init(status: 500, body: "boom")
            case "/api/scan/status":
                statusCalls += 1
                return .init(status: 200, body: statusCalls < 3
                    ? scanStatusJSON(state: "running", percent: 40, scanNumber: 0, resultCount: 0, type: nil)
                    : scanStatusJSON(resultCount: 9))
            default: return .init(status: 404, body: "{\"error\":\"no\"}")
            }
        }
        let model = ScanModel(client: stubClient(), pollInterval: 0.01)
        model.compare = .unknownInitial
        XCTAssertTrue(model.valuesFilled, "unknown initial value needs no value")
        await model.firstScan(pid: 85, sections: ["0x8000000000"])

        XCTAssertEqual(body(of: "/api/scan/first")?["compare"] as? String, "unknown initial value")
        XCTAssertNil(body(of: "/api/scan/first")?["value"])
        XCTAssertEqual(statusCalls, 3)
        XCTAssertEqual(model.status.resultCount, 9)
        XCTAssertNil(model.error)
        XCTAssertEqual(model.compare, .exact, "unknown initial value isn't a next-scan compare")
    }

    func testNextScanSendsTheCompareAndValues() async {
        StubURLProtocol.handler = { request in
            switch request.url?.path {
            case "/api/scan/next":
                return .init(status: 202, body: scanStatusJSON(state: "running", kind: "next", percent: 0, type: "i16"))
            case "/api/scan/progress":
                return .init(status: 200, body: sseEvent("done", scanStatusJSON(kind: "next", scanNumber: 2, resultCount: 3,
                                                                               type: "i16", canUndo: true)))
            default: return .init(status: 404, body: "{\"error\":\"no\"}")
            }
        }
        let model = ScanModel(client: stubClient())
        model.apply(try! JSONDecoder().decode(ScanStatus.self, from: Data(scanStatusJSON(type: "i16").utf8)))
        XCTAssertEqual(model.kind, .byte2, "pickers follow the results' type")
        XCTAssertTrue(model.signed)

        model.compare = .between
        model.value = "10"
        XCTAssertFalse(model.canNextScan, "between needs both values")
        model.value2 = "20"
        model.hex = true
        await model.nextScan()

        let sent = body(of: "/api/scan/next")
        XCTAssertEqual(sent?["compare"] as? String, "between")
        XCTAssertEqual(sent?["value"] as? String, "10")
        XCTAssertEqual(sent?["value2"] as? String, "20")
        XCTAssertEqual(sent?["hex"] as? Bool, true)
        XCTAssertNil(sent?["type"], "the type can't change, so it isn't sent")
        XCTAssertEqual(model.status.scanNumber, 2)
        XCTAssertTrue(model.canUndo)
    }

    func testErrorsAreShownAsTheBackendWroteThem() async {
        StubURLProtocol.handler = { _ in .init(status: 409, body: "{\"error\":\"Not connected to the PS4.\"}") }
        let model = ScanModel(client: stubClient())
        model.value = "1"
        await model.firstScan(pid: 85, sections: ["0x1"])
        XCTAssertEqual(model.error, "Not connected to the PS4.")
        XCTAssertEqual(paths, ["/api/scan/first"], "no progress stream when the scan didn't start")
        XCTAssertFalse(model.isBusy)
    }

    func testNewScanAndUndo() async {
        StubURLProtocol.handler = { request in
            switch request.url?.path {
            case "/api/scan/undo": return .init(status: 200, body: scanStatusJSON(state: "idle", scanNumber: 1))
            case "/api/scan/new":
                return .init(status: 200, body: scanStatusJSON(state: "idle", kind: nil, scanNumber: 0, resultCount: 0, type: nil))
            default: return .init(status: 404, body: "{\"error\":\"no\"}")
            }
        }
        let model = ScanModel(client: stubClient())
        var reloads: [Int] = []
        model.onResultsChanged = { reloads.append($0.scanNumber) }

        await model.undo()
        XCTAssertEqual(model.statusText, "1,234 results (scan 1)")
        await model.newScan()
        XCTAssertFalse(model.hasResults)
        XCTAssertFalse(model.typeLocked)
        XCTAssertEqual(model.statusText, "No scan yet")
        XCTAssertEqual(reloads, [1, 0])
    }

    func testFailedScanShowsItsMessage() {
        let model = ScanModel(client: stubClient())
        model.apply(try! JSONDecoder().decode(ScanStatus.self, from: Data(
            scanStatusJSON(state: "failed", scanNumber: 0, resultCount: 0, message: "Read failed at 0x1000.").utf8)))
        XCTAssertEqual(model.error, "Read failed at 0x1000.")
        XCTAssertEqual(model.statusText, "The scan failed")
    }

    func testRunningStatusText() {
        let model = ScanModel(client: stubClient())
        model.apply(try! JSONDecoder().decode(ScanStatus.self, from: Data(
            scanStatusJSON(state: "running", percent: 42.4, hits: 12345, scanNumber: 0, resultCount: 0, type: nil, elapsed: 12.6).utf8)))
        XCTAssertEqual(model.statusText, "First scan 42% · (unnamed) · 12,345 hits · 13 s")
        XCTAssertEqual(model.progressFraction, 0.424, accuracy: 0.0001)
        XCTAssertTrue(model.typeLocked)
    }
}
