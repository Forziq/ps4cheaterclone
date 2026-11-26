import XCTest
@testable import PS4Mem

/// JSON as `GET /api/processes` writes it: AC3's engine is the suggestion, a system .elf is not.
let processesJSON = """
{
  "processes": [
    {"pid": 41, "name": "SceShellCore", "suggested": false, "suggestionReason": null},
    {"pid": 60, "name": "GnmCompositor.elf", "suggested": false, "suggestionReason": null},
    {"pid": 85, "name": "ac3_engine_orbis_fn.elf", "suggested": true, "suggestionReason": "*.elf, not a system process"}
  ],
  "suggestedPid": 85,
  "suggestionReason": "*.elf, not a system process"
}
"""

let processesJSONNoSuggestion = """
{
  "processes": [{"pid": 41, "name": "SceShellCore", "suggested": false, "suggestionReason": null}],
  "suggestedPid": null,
  "suggestionReason": "no eboot.bin and no non-system *.elf. Is a game running? Use --pid or --name."
}
"""

func processDetailsJSON(pid: Int = 85, name: String = "ac3_engine_orbis_fn.elf", titleId: String = "CUSA11711") -> String {
    """
    {"pid": \(pid), "name": "\(name)", "path": "/app0/\(name)", "titleId": "\(titleId)", "contentId": "EP0001-\(titleId)_00-AC3"}
    """
}

@MainActor
final class ProcessModelTests: XCTestCase {
    override func setUp() async throws { StubURLProtocol.reset() }
    override func tearDown() async throws { StubURLProtocol.reset() }

    private func route(_ routes: [String: StubURLProtocol.Reply]) {
        StubURLProtocol.handler = { request in
            let key = "\(request.httpMethod ?? "GET") \(request.url?.path ?? "")"
            return routes[key] ?? .init(status: 404, body: #"{"error": "no route \#(key)"}"#)
        }
    }

    func testLoadHighlightsTheSuggestionButChoosesNothing() async {
        route(["GET /api/processes": .init(status: 200, body: processesJSON)])
        let model = ProcessModel(client: stubClient())
        await model.load()
        XCTAssertEqual(model.processes.map(\.pid), [41, 60, 85])
        XCTAssertEqual(model.highlightedPid, 85)
        XCTAssertNil(model.chosen, "the user chooses, not the app")
        XCTAssertEqual(model.chosenText, "No process chosen")
        XCTAssertTrue(model.canChooseHighlighted)
        XCTAssertEqual(model.suggestionText,
                       "Suggested: ac3_engine_orbis_fn.elf (pid 85), *.elf, not a system process. You still choose.")
        XCTAssertEqual(ProcessModel.suggestionLabel(model.processes[2].suggestionReason),
                       "suggested: *.elf, not a system process")
    }

    func testNoSuggestionHighlightsNothingAndSaysSo() async {
        route(["GET /api/processes": .init(status: 200, body: processesJSONNoSuggestion)])
        let model = ProcessModel(client: stubClient())
        await model.load()
        XCTAssertNil(model.highlightedPid)
        XCTAssertFalse(model.canChooseHighlighted)
        XCTAssertEqual(model.suggestionText, "No game suggested. Is a game running? Choose its process yourself.")
    }

    func testChoosingAnotherProcessFetchesItsTitleId() async {
        route(["GET /api/processes": .init(status: 200, body: processesJSON),
               "GET /api/processes/60": .init(status: 200, body: processDetailsJSON(pid: 60, name: "GnmCompositor.elf", titleId: ""))])
        let model = ProcessModel(client: stubClient())
        await model.load()
        model.highlightedPid = 60
        let worked = await model.choose(pid: 60)
        XCTAssertTrue(worked)
        XCTAssertEqual(model.chosen?.pid, 60, "the user's choice wins over the suggestion")
        XCTAssertEqual(model.chosenText, "GnmCompositor.elf · no title ID · pid 60")
        XCTAssertFalse(model.canChooseHighlighted, "already chosen")
    }

    func testChosenShowsNameTitleIdAndPid() async {
        route(["GET /api/processes/85": .init(status: 200, body: processDetailsJSON())])
        let model = ProcessModel(client: stubClient())
        await model.choose(pid: 85)
        XCTAssertEqual(model.chosen?.titleId, "CUSA11711")
        XCTAssertEqual(model.chosenText, "ac3_engine_orbis_fn.elf · CUSA11711 · pid 85")
    }

    func testChooseErrorIsShownAndNothingIsChosen() async {
        route(["GET /api/processes/99": .init(status: 502, body: #"{"error": "ps4debug didn't answer."}"#)])
        let model = ProcessModel(client: stubClient())
        let worked = await model.choose(pid: 99)
        XCTAssertFalse(worked)
        XCTAssertNil(model.chosen)
        XCTAssertEqual(model.error, "ps4debug didn't answer.")
    }

    func testReloadKeepsTheHighlightAndDropsAChosenProcessThatHasGone() async {
        route(["GET /api/processes": .init(status: 200, body: processesJSON),
               "GET /api/processes/85": .init(status: 200, body: processDetailsJSON())])
        let model = ProcessModel(client: stubClient())
        await model.load()
        await model.choose(pid: 85)
        model.highlightedPid = 41

        route(["GET /api/processes": .init(status: 200, body: processesJSONNoSuggestion)])
        await model.load()
        XCTAssertEqual(model.highlightedPid, 41, "still listed, so still highlighted")
        XCTAssertNil(model.chosen, "the game was closed")
    }

    func testClearForgetsEverything() async {
        route(["GET /api/processes": .init(status: 200, body: processesJSON),
               "GET /api/processes/85": .init(status: 200, body: processDetailsJSON())])
        let model = ProcessModel(client: stubClient())
        await model.load()
        await model.choose(pid: 85)
        model.clear()
        XCTAssertTrue(model.processes.isEmpty)
        XCTAssertNil(model.highlightedPid)
        XCTAssertNil(model.chosen)
        XCTAssertEqual(model.suggestionText, "")
    }
}
