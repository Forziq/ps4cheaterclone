import XCTest
@testable import PS4Mem

/// The connection bar's logic, against a stubbed backend (no window, no PS4).
@MainActor
final class AppModelTests: XCTestCase {
    override func setUp() async throws { StubURLProtocol.reset() }
    override func tearDown() async throws { StubURLProtocol.reset() }

    /// A model whose backend "already runs" (the stub answers), so nothing is launched.
    private func makeModel() -> AppModel {
        let client = stubClient()
        let backend = BackendProcess(client: client, launchCommand: {
            throw APIError.unreachable("tests never launch a backend")
        })
        return AppModel(client: client, backend: backend, statusPollInterval: 3600)
    }

    /// Answers by path; anything else is a 404 with an error body.
    private func route(_ routes: [String: StubURLProtocol.Reply]) {
        StubURLProtocol.handler = { request in
            let key = "\(request.httpMethod ?? "GET") \(request.url?.path ?? "")"
            return routes[key] ?? .init(status: 404, body: #"{"error": "no route \#(key)"}"#)
        }
    }

    private func paths() -> [String] {
        StubURLProtocol.requests.map { "\($0.httpMethod ?? "GET") \($0.url?.path ?? "")" }
    }

    func testStartUpLoadsStatusAndSettingsAndPrefillsTheDefaultIp() async {
        route(["GET /api/status": .init(status: 200, body: statusJSONDisconnected.replacingOccurrences(
            of: #""defaultIp": null"#, with: #""defaultIp": "192.0.2.12""#)),
               "GET /api/settings": .init(status: 200, body: settingsJSON)])
        let model = makeModel()
        await model.startUp()
        XCTAssertTrue(model.backend.isRunning)
        XCTAssertEqual(model.ipText, "192.0.2.12", "the backend's default IP wins")
        XCTAssertEqual(model.recentIps, ["192.0.2.10", "192.0.2.11"])
        XCTAssertEqual(model.statusText, "Not connected")
        model.shutDown()
    }

    func testWithoutDefaultIpTheSavedIpIsUsed() async {
        route(["GET /api/status": .init(status: 200, body: statusJSONDisconnected),
               "GET /api/settings": .init(status: 200, body: settingsJSON)])
        let model = makeModel()
        await model.startUp()
        XCTAssertEqual(model.ipText, "192.0.2.10")
        model.shutDown()
    }

    func testConnectSendsTheTypedIpAndShowsTheVersion() async {
        route(["POST /api/connect": .init(status: 200, body: statusJSONConnected),
               "GET /api/settings": .init(status: 200, body: settingsJSON)])
        let model = makeModel()
        model.ipText = " 192.0.2.10 "
        await model.connect()
        XCTAssertEqual(StubURLProtocol.bodies.first, #"{"ip":"192.0.2.10"}"#, "the IP is trimmed")
        XCTAssertTrue(model.status.connected)
        XCTAssertEqual(model.statusText, "Connected to 192.0.2.10:744 · ps4debug 1.3.4")
        XCTAssertNil(model.actionError)
        XCTAssertFalse(model.isBusy)
        XCTAssertTrue(paths().contains("GET /api/settings"), "recent IPs are reloaded after a connect")
    }

    func testConnectWithAnEmptyFieldLetsTheBackendPick() async {
        route(["POST /api/connect": .init(status: 200, body: statusJSONConnected),
               "GET /api/settings": .init(status: 200, body: settingsJSON)])
        let model = makeModel()
        model.ipText = "   "
        await model.connect()
        XCTAssertEqual(StubURLProtocol.bodies.first, "{}")
        XCTAssertEqual(model.ipText, "192.0.2.10", "the field shows the IP that was used")
    }

    func testFailedConnectShowsTheBackendsMessage() async {
        let refused = "Connection refused on 192.0.2.10:744."
        route(["POST /api/connect": .init(status: 502, body: #"{"error": "\#(refused)"}"#),
               "GET /api/status": .init(status: 200, body: statusJSONDisconnected)])
        let model = makeModel()
        model.ipText = "192.0.2.10"
        await model.connect()
        XCTAssertEqual(model.actionError, refused)
        XCTAssertFalse(model.status.connected)
        XCTAssertEqual(model.status.lastError, refused, "the status is refreshed after a failure")
        XCTAssertFalse(model.isBusy)
    }

    func testDisconnect() async {
        route(["POST /api/connect": .init(status: 200, body: statusJSONConnected),
               "GET /api/settings": .init(status: 200, body: settingsJSON),
               "POST /api/disconnect": .init(status: 200, body: statusJSONDisconnected)])
        let model = makeModel()
        await model.connect()
        await model.disconnect()
        XCTAssertFalse(model.status.connected)
        XCTAssertEqual(paths().last, "POST /api/disconnect")
    }

    func testANewActionClearsTheOldError() async {
        route(["POST /api/connect": .init(status: 502, body: #"{"error": "nope"}"#),
               "GET /api/status": .init(status: 200, body: statusJSONDisconnected)])
        let model = makeModel()
        await model.connect()
        XCTAssertEqual(model.actionError, "nope")
        route(["POST /api/connect": .init(status: 200, body: statusJSONConnected),
               "GET /api/settings": .init(status: 200, body: settingsJSON)])
        await model.connect()
        XCTAssertNil(model.actionError)
    }

    func testPollingPicksUpADroppedConnection() async throws {
        route(["GET /api/status": .init(status: 200, body: statusJSONConnected),
               "GET /api/settings": .init(status: 200, body: settingsJSON)])
        let client = stubClient()
        let backend = BackendProcess(client: client, launchCommand: { throw APIError.unreachable("no") })
        let model = AppModel(client: client, backend: backend, statusPollInterval: 0.05)
        await model.startUp()
        XCTAssertTrue(model.status.connected)

        // The PS4 goes away: the backend now reports disconnected with a reason.
        route(["GET /api/status": .init(status: 200, body: statusJSONDisconnected)])
        let deadline = Date().addingTimeInterval(3)
        while model.status.connected, Date() < deadline {
            try await Task.sleep(nanoseconds: 20_000_000)
        }
        XCTAssertFalse(model.status.connected)
        XCTAssertEqual(model.status.lastError, "Connection refused on 192.0.2.10:744.")
        model.shutDown()
    }

    func testStatusTextWithoutVersion() {
        let model = makeModel()
        XCTAssertEqual(model.statusText, "Not connected")
    }
}
