import XCTest
@testable import PS4Mem

/// A results page as `GET /api/scan/results` writes it: rows offset ..< offset+limit of `total`.
func resultsPageJSON(scanNumber: Int = 1, total: Int64, offset: Int64, limit: Int, value: String = "100") -> String {
    let count = max(0, min(Int64(limit), total - offset))
    let rows = (0..<count).map { i -> String in
        let index = offset + i
        let address = String(format: "0x%llX", 0x8000000000 + UInt64(index) * 4)
        return """
        {"index":\(index),"address":"\(address)","value":"\(value)","section":"(unnamed)",
         "sectionStart":"0x8000000000","sectionOffset":"\(String(format: "0x%llX", UInt64(index) * 4))"}
        """
    }
    return """
    {"scanNumber":\(scanNumber),"type":"u32","valueSize":4,"total":\(total),"offset":\(offset),"count":\(count),
     "results":[\(rows.joined(separator: ","))]}
    """
}

@MainActor
final class ResultsModelTests: XCTestCase {
    override func setUp() async throws { StubURLProtocol.reset() }
    override func tearDown() async throws { StubURLProtocol.reset() }

    private func query(_ request: URLRequest) -> [String: String] {
        let items = request.url.flatMap { URLComponents(url: $0, resolvingAgainstBaseURL: false)?.queryItems } ?? []
        return Dictionary(uniqueKeysWithValues: items.map { ($0.name, $0.value ?? "") })
    }

    /// Serves pages of `total` results (scan 1), and current values "150" for any range asked for.
    private func serve(total: Int64, scanNumber: Int = 1) {
        StubURLProtocol.handler = { request in
            switch request.url?.path {
            case "/api/scan/results":
                let q = self.queryStatic(request)
                return .init(status: 200, body: resultsPageJSON(scanNumber: scanNumber, total: total,
                                                                offset: Int64(q["offset"]!)!, limit: Int(q["limit"]!)!,
                                                                value: q["hex"] == "true" ? "0x00000064" : "100"))
            case "/api/scan/results/values":
                let body = StubURLProtocol.bodies.last ?? ""
                let req = try JSONDecoder().decode(ValuesBody.self, from: Data(body.utf8))
                let values = (0..<Int64(req.limit)).map { i in
                    "{\"index\":\(req.offset + i),\"address\":\"0x0\",\"value\":\"150\"}"
                }
                return .init(status: 200, body: "{\"scanNumber\":\(scanNumber),\"offset\":\(req.offset),\"values\":[\(values.joined(separator: ","))]}")
            case "/api/cheats/CUSA11711":
                return .init(status: 201, body: """
                {"id":"c1","description":"","address":"0x8000000000","section":"(unnamed)","sectionStart":"0x8000000000",
                 "sectionOffset":"0x0","type":"u32","value":"150","locked":false,"lockPid":null,"lockWrites":null,"lockError":null}
                """)
            default: return .init(status: 404, body: "{\"error\":\"no\"}")
            }
        }
    }

    private struct ValuesBody: Decodable { var offset: Int64; var limit: Int; var hex: Bool }

    nonisolated private func queryStatic(_ request: URLRequest) -> [String: String] {
        let items = request.url.flatMap { URLComponents(url: $0, resolvingAgainstBaseURL: false)?.queryItems } ?? []
        return Dictionary(uniqueKeysWithValues: items.map { ($0.name, $0.value ?? "") })
    }

    private func status(scanNumber: Int) -> ScanStatus {
        try! JSONDecoder().decode(ScanStatus.self, from: Data(scanStatusJSON(scanNumber: scanNumber).utf8))
    }

    func testReloadFetchesTheFirstPage() async {
        serve(total: 1_234_567)
        let model = ResultsModel(client: stubClient(), maxShown: 5_000, pageSize: 1_000)
        await model.reload(status: status(scanNumber: 1))

        XCTAssertEqual(model.rows.count, 1_000)
        XCTAssertEqual(model.total, 1_234_567)
        XCTAssertEqual(model.type, "u32")
        XCTAssertEqual(model.rows[1].address, "0x8000000004")
        XCTAssertEqual(model.rows[1].previous, "100")
        XCTAssertNil(model.rows[1].current)
        XCTAssertEqual(query(StubURLProtocol.requests[0]), ["offset": "0", "limit": "1000", "hex": "false"])
        XCTAssertTrue(model.hasMore)
        XCTAssertTrue(model.countText.hasPrefix("Showing 1,000 of 1,234,567 results"), model.countText)
    }

    func testScrollingFetchesPagesUpToTheLimit() async {
        serve(total: 1_234_567)
        let model = ResultsModel(client: stubClient(), maxShown: 2_500, pageSize: 1_000)
        await model.reload(status: status(scanNumber: 1))

        await model.rowAppeared(model.rows[10])
        XCTAssertEqual(model.rows.count, 1_000, "a row near the top doesn't fetch more")
        await model.rowAppeared(model.rows[900])
        XCTAssertEqual(model.rows.count, 2_000)
        await model.rowAppeared(model.rows[1_999])
        XCTAssertEqual(model.rows.count, 2_500, "the last page stops at the limit")
        XCTAssertEqual(query(StubURLProtocol.requests.last!)["limit"], "500")
        XCTAssertFalse(model.hasMore)
        await model.rowAppeared(model.rows[2_499])
        XCTAssertEqual(StubURLProtocol.requests.count, 3, "nothing more to fetch")
        XCTAssertEqual(model.rows.map(\.index), Array(0..<2_500), "rows in order, none twice")
    }

    func testFewResultsAreAllShown() async {
        serve(total: 3)
        let model = ResultsModel(client: stubClient())
        await model.reload(status: status(scanNumber: 1))
        XCTAssertEqual(model.rows.count, 3)
        XCTAssertFalse(model.hasMore)
        XCTAssertEqual(model.countText, "3 results")
    }

    func testNoResultsFetchesNothing() async {
        serve(total: 0)
        let model = ResultsModel(client: stubClient())
        await model.reload(status: .idle)
        XCTAssertTrue(StubURLProtocol.requests.isEmpty)
        XCTAssertEqual(model.countText, "")
    }

    func testCurrentValuesOfTheVisibleRowsAreRead() async {
        serve(total: 2_000)
        let model = ResultsModel(client: stubClient())
        await model.reload(status: status(scanNumber: 1))

        XCTAssertEqual(model.refreshRange, 0..<50, "before the table reports rows: the first 50")
        for i in 20..<40 { await model.rowAppeared(model.rows[i]) }
        model.rowDisappeared(model.rows[20])
        XCTAssertEqual(model.refreshRange, 21..<40)

        await model.refreshCurrentValues()
        let sent = try? JSONSerialization.jsonObject(with: Data(StubURLProtocol.bodies.last!.utf8)) as? [String: Any]
        XCTAssertEqual(StubURLProtocol.requests.last?.url?.path, "/api/scan/results/values")
        XCTAssertEqual(sent?["offset"] as? Int, 21)
        XCTAssertEqual(sent?["limit"] as? Int, 19)
        XCTAssertEqual(model.rows[21].current, "150")
        XCTAssertTrue(model.rows[21].changed)
        XCTAssertEqual(model.rows[21].latest, "150")
        XCTAssertNil(model.rows[20].current, "rows off screen aren't read")
    }

    func testValuesForOtherResultsAreIgnored() async {
        serve(total: 10, scanNumber: 1)
        let model = ResultsModel(client: stubClient())
        await model.reload(status: status(scanNumber: 1))
        serve(total: 10, scanNumber: 2) // a scan ran in between
        await model.refreshCurrentValues()
        XCTAssertNil(model.rows[0].current)
    }

    func testHexFetchesTheRowsAgain() async {
        serve(total: 10)
        let model = ResultsModel(client: stubClient())
        await model.reload(status: status(scanNumber: 1))
        await model.setHex(true)
        XCTAssertEqual(query(StubURLProtocol.requests.last!)["hex"], "true")
        XCTAssertEqual(model.rows[0].previous, "0x00000064")
        XCTAssertEqual(model.rows.count, 10)
    }

    func testAddToCheatListSendsTheRowAsShown() async {
        serve(total: 10)
        let model = ResultsModel(client: stubClient())
        await model.reload(status: status(scanNumber: 1))
        await model.refreshCurrentValues()

        await model.addToCheatList([0], titleId: "CUSA11711", game: "ac3_engine_orbis_fn.elf")
        let request = StubURLProtocol.requests.last!
        XCTAssertEqual(request.httpMethod, "POST")
        XCTAssertEqual(request.url?.path, "/api/cheats/CUSA11711")
        let sent = try? JSONSerialization.jsonObject(with: Data(StubURLProtocol.bodies.last!.utf8)) as? [String: Any]
        XCTAssertEqual(sent?["address"] as? String, "0x8000000000")
        XCTAssertEqual(sent?["type"] as? String, "u32")
        XCTAssertEqual(sent?["value"] as? String, "150", "the current value when it has been read")
        XCTAssertEqual(sent?["section"] as? String, "(unnamed)")
        XCTAssertEqual(sent?["sectionStart"] as? String, "0x8000000000")
        XCTAssertEqual(sent?["game"] as? String, "ac3_engine_orbis_fn.elf")
        XCTAssertNil(sent?["description"])
        XCTAssertEqual(model.notice, "Added 0x8000000000 to the cheat list of CUSA11711")
        XCTAssertFalse(StubURLProtocol.requests.contains { $0.url?.path.contains("write") == true },
                       "adding a cheat never writes memory")
    }

    func testClearForgetsTheRows() async {
        serve(total: 10)
        let model = ResultsModel(client: stubClient())
        await model.reload(status: status(scanNumber: 1))
        model.clear()
        XCTAssertTrue(model.rows.isEmpty)
        XCTAssertEqual(model.total, 0)
        XCTAssertNil(model.refreshRange)
    }
}
