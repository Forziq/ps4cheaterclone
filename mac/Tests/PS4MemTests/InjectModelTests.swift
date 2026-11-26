import XCTest
@testable import PS4Mem

/// The inject sheet's logic, against a stubbed backend (no PS4, nothing is sent anywhere).
@MainActor
final class InjectModelTests: XCTestCase {
    override func setUp() async throws { StubURLProtocol.reset() }
    override func tearDown() async throws { StubURLProtocol.reset() }

    private let injectOK = """
    {"payloadPath": "/Users/me/ps4debug.bin", "bytesSent": 1000, "loaderPort": 9090, "waitedSeconds": 4.0,
     "status": \(statusJSONConnected)}
    """

    private func settings(payloadPath: String = "payloads/ps4debug.bin", loaderPort: Int = 9090) -> AppSettings {
        AppSettings(consoleIp: "192.0.2.10", recentIps: ["192.0.2.10"], commandPort: 744,
                    loaderPort: loaderPort, payloadPath: payloadPath, debugLogging: false)
    }

    private func bodyJSON(_ index: Int) throws -> [String: Any] {
        try XCTUnwrap(JSONSerialization.jsonObject(with: Data(StubURLProtocol.bodies[index].utf8)) as? [String: Any])
    }

    func testLoadUsesTheSavedPathAndPort() {
        let model = InjectModel(client: stubClient())
        model.load(from: settings(payloadPath: "/x/p.bin", loaderPort: 9021))
        XCTAssertEqual(model.payloadPath, "/x/p.bin")
        XCTAssertEqual(model.loaderPort, 9021)
    }

    func testPortOptionsAreTheStandardOnesPlusASavedOtherPort() {
        let model = InjectModel(client: stubClient())
        XCTAssertEqual(model.loaderPortOptions, [9090, 9020, 9021])
        model.loaderPort = 9999
        XCTAssertEqual(model.loaderPortOptions, [9090, 9020, 9021, 9999])
    }

    func testInjectSendsIpPathAndPort() async throws {
        StubURLProtocol.handler = { _ in .init(status: 200, body: self.injectOK) }
        let model = InjectModel(client: stubClient())
        model.payloadPath = "/Users/me/ps4debug.bin"
        model.loaderPort = 9020
        let response = await model.inject(ip: "192.0.2.10")
        XCTAssertEqual(response?.status.connected, true)
        XCTAssertNil(model.error)
        XCTAssertFalse(model.isInjecting)
        XCTAssertEqual(StubURLProtocol.requests.first?.url?.path, "/api/inject")
        let body = try bodyJSON(0)
        XCTAssertEqual(body["ip"] as? String, "192.0.2.10")
        XCTAssertEqual(body["payloadPath"] as? String, "/Users/me/ps4debug.bin")
        XCTAssertEqual(body["loaderPort"] as? Int, 9020)
    }

    func testInjectWithoutIpLeavesItOut() async throws {
        StubURLProtocol.handler = { _ in .init(status: 200, body: self.injectOK) }
        let model = InjectModel(client: stubClient())
        model.payloadPath = "payloads/ps4debug.bin"
        _ = await model.inject(ip: nil)
        XCTAssertNil(try bodyJSON(0)["ip"])
    }

    func testNoPayloadPathIsCaughtBeforeSending() async {
        let model = InjectModel(client: stubClient())
        model.payloadPath = "  "
        let response = await model.inject(ip: nil)
        XCTAssertNil(response)
        XCTAssertEqual(model.error, "Choose the ps4debug payload (.bin) first.")
        XCTAssertTrue(StubURLProtocol.requests.isEmpty)
    }

    func testRefusedLoaderShowsTheBackendsAdvice() async {
        let refused = "Connection refused on 192.0.2.10:9090. BinLoader is off or already used this session: " +
            "toggle it in GoldHEN or restart the PS4."
        StubURLProtocol.handler = { _ in .init(status: 502, body: #"{"error": "\#(refused)"}"#) }
        let model = InjectModel(client: stubClient())
        model.payloadPath = "/p.bin"
        let response = await model.inject(ip: "192.0.2.10")
        XCTAssertNil(response)
        XCTAssertEqual(model.error, refused)
        XCTAssertFalse(model.isInjecting)
    }

    func testAlreadyRunningIsShownNotRetried() async {
        let running = "ps4debug already answers on 192.0.2.10:744: use Connect."
        StubURLProtocol.handler = { _ in .init(status: 409, body: #"{"error": "\#(running)"}"#) }
        let model = InjectModel(client: stubClient())
        model.payloadPath = "/p.bin"
        _ = await model.inject(ip: nil)
        XCTAssertEqual(model.error, running)
        XCTAssertEqual(StubURLProtocol.requests.count, 1, "a refused inject is never sent twice")
    }

    func testChoosingAPayloadSavesThePath() async throws {
        StubURLProtocol.handler = { _ in .init(status: 200, body: settingsJSON) }
        let model = InjectModel(client: stubClient())
        let saved = await model.choosePayload(URL(fileURLWithPath: "/Users/me/new.bin"))
        XCTAssertNotNil(saved)
        XCTAssertEqual(model.payloadPath, "/Users/me/new.bin")
        XCTAssertEqual(StubURLProtocol.requests.first?.httpMethod, "PUT")
        let body = try bodyJSON(0)
        XCTAssertEqual(body.count, 1)
        XCTAssertEqual(body["payloadPath"] as? String, "/Users/me/new.bin")
    }

    func testChoosingAPortSavesIt() async throws {
        StubURLProtocol.handler = { _ in .init(status: 200, body: settingsJSON) }
        let model = InjectModel(client: stubClient())
        await model.chooseLoaderPort(9021)
        XCTAssertEqual(model.loaderPort, 9021)
        XCTAssertEqual(try bodyJSON(0)["loaderPort"] as? Int, 9021)
    }

    func testRelativePayloadPathResolvesAgainstTheRepo() {
        let model = InjectModel(client: stubClient())
        model.payloadPath = "payloads/ps4debug.bin"
        XCTAssertEqual(model.payloadURL(environment: ["PS4MEM_REPO_ROOT": "/repo"])?.path, "/repo/payloads/ps4debug.bin")
        model.payloadPath = "/abs/p.bin"
        XCTAssertEqual(model.payloadURL(environment: [:])?.path, "/abs/p.bin")
    }

    func testProgressText() {
        let model = InjectModel(client: stubClient())
        XCTAssertEqual(model.progressText, "Sending the payload to port 9090…")
    }

    // MARK: Through the app model (what the sheet's Inject button does)

    func testAppModelInjectConnectsAndReloadsSettings() async {
        StubURLProtocol.handler = { request in
            switch (request.httpMethod ?? "", request.url?.path ?? "") {
            case ("POST", "/api/inject"): return .init(status: 200, body: self.injectOK)
            case ("GET", "/api/settings"): return .init(status: 200, body: settingsJSON)
            default: return .init(status: 404, body: #"{"error": "no route"}"#)
            }
        }
        let client = stubClient()
        let app = AppModel(client: client, backend: BackendProcess(client: client, launchCommand: {
            throw APIError.unreachable("never launched")
        }), statusPollInterval: 3600)
        app.inject.payloadPath = "/Users/me/ps4debug.bin"
        let connected = await app.injectPayload()
        XCTAssertTrue(connected)
        XCTAssertTrue(app.status.connected)
        XCTAssertEqual(app.statusText, "Connected to 192.0.2.10:744 · ps4debug 1.3.4")
        XCTAssertEqual(app.recentIps, ["192.0.2.10", "192.0.2.11"])
        XCTAssertFalse(app.isBusy)
    }

    func testAppModelInjectFailureKeepsTheSheetOpen() async {
        StubURLProtocol.handler = { request in
            switch (request.httpMethod ?? "", request.url?.path ?? "") {
            case ("POST", "/api/inject"): return .init(status: 502, body: #"{"error": "ps4debug never answered"}"#)
            case ("GET", "/api/status"): return .init(status: 200, body: statusJSONDisconnected)
            default: return .init(status: 404, body: #"{"error": "no route"}"#)
            }
        }
        let client = stubClient()
        let app = AppModel(client: client, backend: BackendProcess(client: client, launchCommand: {
            throw APIError.unreachable("never launched")
        }), statusPollInterval: 3600)
        app.inject.payloadPath = "/p.bin"
        let connected = await app.injectPayload()
        XCTAssertFalse(connected)
        XCTAssertEqual(app.inject.error, "ps4debug never answered")
        XCTAssertFalse(app.status.connected)
    }
}
