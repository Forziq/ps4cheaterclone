import XCTest
@testable import PS4Mem

/// A cheat as the API writes it.
func cheatJSON(id: String = "c1", description: String = "", address: String = "0x2001A3F10",
               value: String = "100", locked: Bool = false, lockError: String? = nil) -> String {
    """
    {"id":"\(id)","description":"\(description)","address":"\(address)","section":"(unnamed)",
     "sectionStart":"0x2001A0000","sectionOffset":"0x3F10","type":"u32","value":"\(value)",
     "locked":\(locked),"lockPid":\(locked ? "77" : "null"),"lockWrites":\(locked ? "5" : "null"),
     "lockError":\(lockError.map { "\"\($0)\"" } ?? "null")}
    """
}

func cheatTableJSON(titleId: String = "CUSA11711", game: String? = "Game", cheats: [String]) -> String {
    """
    {"titleId":"\(titleId)","game":\(game.map { "\"\($0)\"" } ?? "null"),
     "file":"/Users/x/Library/Application Support/PS4Mem/cheats/\(titleId).json","cheats":[\(cheats.joined(separator: ","))]}
    """
}

@MainActor
final class CheatListModelTests: XCTestCase {
    override func setUp() async throws { StubURLProtocol.reset() }
    override func tearDown() async throws { StubURLProtocol.reset() }

    private var calls: [String] {
        StubURLProtocol.requests.map { "\($0.httpMethod ?? "GET") \($0.url?.path ?? "")" }
    }

    /// Serves a list with two cheats; `extra` answers other paths first.
    private func serve(_ extra: @escaping (URLRequest) -> StubURLProtocol.Reply? = { _ in nil }) {
        StubURLProtocol.handler = { request in
            if let reply = extra(request) { return reply }
            switch (request.httpMethod ?? "GET", request.url?.path ?? "") {
            case ("GET", "/api/cheats/CUSA11711"):
                return .init(status: 200, body: cheatTableJSON(cheats: [
                    cheatJSON(id: "c1", description: "Money"), cheatJSON(id: "c2", address: "0x2001A3F20", value: "5"),
                ]))
            case ("POST", "/api/cheats/CUSA11711/values"):
                return .init(status: 200, body: """
                {"titleId":"CUSA11711","pid":77,"values":[{"id":"c1","value":"150"},{"id":"c2","value":"5"}]}
                """)
            default:
                return .init(status: 404, body: "{\"error\":\"not served\"}")
            }
        }
    }

    private func shownModel() async -> CheatListModel {
        let model = CheatListModel(client: stubClient())
        await model.show(titleId: "CUSA11711", game: "eboot.bin", pid: 77)
        return model
    }

    func testShowLoadsTheGamesList() async {
        serve()
        let model = await shownModel()

        XCTAssertEqual(calls, ["GET /api/cheats/CUSA11711"])
        XCTAssertEqual(model.cheats.map(\.id), ["c1", "c2"])
        XCTAssertEqual(model.game, "Game", "the saved game name wins over the process name")
        XCTAssertEqual(model.headerText, "Cheat list · CUSA11711 · Game · 2 cheats")
        XCTAssertTrue(model.file?.hasSuffix("cheats/CUSA11711.json") ?? false)
        XCTAssertNil(model.error)
    }

    func testShowingTheSameGameAndProcessAgainDoesNothing() async {
        serve()
        let model = await shownModel()
        await model.show(titleId: "CUSA11711", game: "eboot.bin", pid: 77)
        XCTAssertEqual(calls.count, 1)
    }

    func testAProcessWithoutTitleIdHasNoList() async {
        serve()
        let model = CheatListModel(client: stubClient())
        await model.show(titleId: "  ", game: "x.elf", pid: 5)
        XCTAssertNil(model.titleId)
        XCTAssertTrue(calls.isEmpty)
        XCTAssertNotNil(model.error)
    }

    func testDescriptionIsSavedWithoutWritingMemory() async throws {
        serve { request in
            request.httpMethod == "PUT"
                ? .init(status: 200, body: cheatJSON(id: "c1", description: "Gold")) : nil
        }
        let model = await shownModel()
        await model.setDescription("c1", " Gold ")

        XCTAssertEqual(calls.last, "PUT /api/cheats/CUSA11711/c1")
        let body = try JSONSerialization.jsonObject(with: Data(StubURLProtocol.bodies.last!.utf8)) as! [String: Any]
        XCTAssertEqual(body as NSDictionary, ["description": "Gold"], "only the description is sent")
        XCTAssertEqual(model.cheats[0].description, "Gold")
        XCTAssertFalse(calls.contains { $0.hasSuffix("/write") || $0.hasSuffix("/lock") })
    }

    func testAnUnchangedDescriptionIsNotSent() async {
        serve()
        let model = await shownModel()
        await model.setDescription("c1", "Money")
        XCTAssertEqual(calls.count, 1)
    }

    func testReturnInTheValueFieldWritesIt() async throws {
        serve { request in
            request.url?.path == "/api/cheats/CUSA11711/c1/write"
                ? .init(status: 200, body: "{\"cheat\":\(cheatJSON(id: "c1", value: "999")),\"oldValue\":\"100\"}") : nil
        }
        let model = await shownModel()
        await model.writeValue("c1", " 999 ")

        XCTAssertEqual(calls.last, "POST /api/cheats/CUSA11711/c1/write")
        let body = try JSONSerialization.jsonObject(with: Data(StubURLProtocol.bodies.last!.utf8)) as! [String: Any]
        XCTAssertEqual(body as NSDictionary, ["pid": 77, "value": "999"])
        XCTAssertEqual(model.cheats[0].value, "999")
        XCTAssertEqual(model.current["c1"], "999")
        XCTAssertEqual(model.notice, "Wrote 999 to 0x2001A3F10 (was 100)")
    }

    func testAnEmptyValueIsNotWritten() async {
        serve()
        let model = await shownModel()
        await model.writeValue("c1", "  ")
        XCTAssertEqual(calls.count, 1)
        XCTAssertNotNil(model.error)
    }

    func testAFailedWriteShowsTheBackendsMessage() async {
        serve { request in
            request.url?.path.hasSuffix("/write") == true
                ? .init(status: 502, body: "{\"error\":\"The PS4 refused the write.\"}") : nil
        }
        let model = await shownModel()
        await model.writeValue("c1", "999")
        XCTAssertEqual(model.error, "The PS4 refused the write.")
        XCTAssertEqual(model.cheats[0].value, "100")
    }

    func testLockAndUnlock() async throws {
        serve { request in
            switch request.url?.path {
            case "/api/cheats/CUSA11711/c1/lock": return .init(status: 200, body: cheatJSON(id: "c1", locked: true))
            case "/api/cheats/CUSA11711/c1/unlock": return .init(status: 200, body: cheatJSON(id: "c1"))
            default: return nil
            }
        }
        let model = await shownModel()

        await model.setLocked("c1", true)
        XCTAssertEqual(calls.last, "POST /api/cheats/CUSA11711/c1/lock")
        let body = try JSONSerialization.jsonObject(with: Data(StubURLProtocol.bodies.last!.utf8)) as! [String: Any]
        XCTAssertEqual(body as NSDictionary, ["pid": 77])
        XCTAssertTrue(model.cheats[0].locked)
        XCTAssertEqual(model.lockedCount, 1)

        await model.setLocked("c1", false)
        XCTAssertEqual(calls.last, "POST /api/cheats/CUSA11711/c1/unlock")
        XCTAssertFalse(model.cheats[0].locked)
    }

    func testDeleteRemovesTheRow() async {
        serve { request in
            request.httpMethod == "DELETE" ? .init(status: 204, body: "") : nil
        }
        let model = await shownModel()
        await model.delete("c2")
        XCTAssertEqual(calls.last, "DELETE /api/cheats/CUSA11711/c2")
        XCTAssertEqual(model.cheats.map(\.id), ["c1"])
        XCTAssertNil(model.error)
    }

    func testRefreshReadsTheCurrentValues() async {
        serve()
        let model = await shownModel()
        await model.refreshCurrentValues()

        XCTAssertEqual(calls.last, "POST /api/cheats/CUSA11711/values")
        XCTAssertEqual(model.current, ["c1": "150", "c2": "5"])
        XCTAssertNil(model.valuesError)
        XCTAssertFalse(calls.contains { $0.hasSuffix("/write") || $0.hasSuffix("/lock") }, "refresh only reads")
    }

    func testRefreshWithALockFetchesTheListToo() async {
        var locked = true
        serve { request in
            switch (request.httpMethod ?? "", request.url?.path ?? "") {
            case ("POST", "/api/cheats/CUSA11711/c1/lock"):
                return .init(status: 200, body: cheatJSON(id: "c1", locked: true))
            case ("GET", "/api/cheats/CUSA11711") where !locked:
                // The lock stopped by itself.
                return .init(status: 200, body: cheatTableJSON(cheats: [cheatJSON(id: "c1", lockError: "write failed")]))
            default: return nil
            }
        }
        let model = await shownModel()
        await model.setLocked("c1", true)
        locked = false
        await model.refreshCurrentValues()

        XCTAssertFalse(model.cheats[0].locked)
        XCTAssertEqual(model.cheats[0].lockError, "write failed")
    }

    func testAFailedReadIsShownApart() async {
        serve { request in
            request.url?.path.hasSuffix("/values") == true
                ? .init(status: 409, body: "{\"error\":\"Not connected.\"}") : nil
        }
        let model = await shownModel()
        await model.refreshCurrentValues()
        XCTAssertEqual(model.valuesError, "Couldn't read the current values: Not connected.")
        XCTAssertNil(model.error)
    }

    func testClear() async {
        serve()
        let model = await shownModel()
        model.clear()
        XCTAssertNil(model.titleId)
        XCTAssertTrue(model.cheats.isEmpty)
        XCTAssertNil(model.pid)
    }

    func testAddingFromTheResultsReloadsTheList() async {
        serve()
        let app = AppModel(client: stubClient(), backend: nil)
        await app.cheats.show(titleId: "CUSA11711", game: nil, pid: 77)
        let before = calls.count
        await app.results.onCheatsAdded?()
        XCTAssertEqual(calls.count, before + 1)
        XCTAssertEqual(calls.last, "GET /api/cheats/CUSA11711")
    }

    // MARK: Import and export

    func testExportReturnsTheFileAsTheBackendWritesIt() async {
        let file = "{\"titleId\":\"CUSA11711\",\"cheats\":[]}"
        serve { request in
            request.url?.path == "/api/cheats/CUSA11711/export" ? .init(status: 200, body: file) : nil
        }
        let model = await shownModel()
        let data = await model.exportFile()
        XCTAssertEqual(data.map { String(decoding: $0, as: UTF8.self) }, file)
        XCTAssertEqual(calls.last, "GET /api/cheats/CUSA11711/export")
        model.exported(to: "/tmp/CUSA11711.json")
        XCTAssertEqual(model.notice, "Exported 2 cheats to /tmp/CUSA11711.json")
    }

    private func importReply(mode: String) -> StubURLProtocol.Reply {
        .init(status: 200, body: """
        {"mode":"\(mode)","added":1,"replaced":1,"removed":\(mode == "replace" ? 1 : 0),
         "table":\(cheatTableJSON(cheats: [cheatJSON(id: "c1", value: "7"), cheatJSON(id: "c3", address: "0x2001A3F30")]))}
        """)
    }

    func testImportSendsTheFileAsItIsWithTheMode() async {
        serve { request in
            request.url?.path == "/api/cheats/CUSA11711/import"
                ? self.importReply(mode: URLComponents(url: request.url!, resolvingAgainstBaseURL: false)?
                    .queryItems?.first?.value ?? "") : nil
        }
        let model = await shownModel()
        let file = "{\"titleId\":\"CUSA11711\",\"cheats\":[1]}"
        await model.importFile(Data(file.utf8), mode: .merge)

        let request = StubURLProtocol.requests.last!
        XCTAssertEqual(request.url?.query, "mode=merge")
        XCTAssertEqual(StubURLProtocol.bodies.last, file, "the file goes as it is")
        XCTAssertEqual(model.cheats.map(\.id), ["c1", "c3"])
        XCTAssertEqual(model.notice, "Imported: 1 added, 1 replaced")

        await model.importFile(Data(file.utf8), mode: .replace)
        XCTAssertEqual(StubURLProtocol.requests.last?.url?.query, "mode=replace")
        XCTAssertEqual(model.notice, "Imported: 1 added, 1 replaced, 1 removed")
        XCTAssertFalse(calls.contains { $0.hasSuffix("/write") || $0.hasSuffix("/lock") }, "import writes no memory")
    }

    func testABadFileChangesNothing() async {
        serve { request in
            request.url?.path.hasSuffix("/import") == true
                ? .init(status: 400, body: "{\"error\":\"Not a cheat file.\"}") : nil
        }
        let model = await shownModel()
        await model.importFile(Data("nope".utf8), mode: .replace)
        XCTAssertEqual(model.error, "Couldn't import: Not a cheat file.")
        XCTAssertEqual(model.cheats.map(\.id), ["c1", "c2"])
    }
}
