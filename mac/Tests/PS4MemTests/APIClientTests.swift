import XCTest
@testable import PS4Mem

final class APIClientTests: XCTestCase {
    override func setUp() { StubURLProtocol.reset() }
    override func tearDown() { StubURLProtocol.reset() }

    func testStatusDecodesConnected() async throws {
        StubURLProtocol.handler = { _ in .init(status: 200, body: statusJSONConnected) }
        let status = try await stubClient().status()
        XCTAssertTrue(status.connected)
        XCTAssertEqual(status.ip, "192.0.2.10")
        XCTAssertEqual(status.port, 744)
        XCTAssertEqual(status.version, "1.3.4")
        XCTAssertEqual(status.defaultIpSource, "ps4ip.local")
        XCTAssertNil(status.lastError)
        XCTAssertEqual(StubURLProtocol.requests.first?.httpMethod, "GET")
        XCTAssertEqual(StubURLProtocol.requests.first?.url?.path, "/api/status")
    }

    func testStatusDecodesDisconnectedWithNulls() async throws {
        StubURLProtocol.handler = { _ in .init(status: 200, body: statusJSONDisconnected) }
        let status = try await stubClient().status()
        XCTAssertFalse(status.connected)
        XCTAssertNil(status.version)
        XCTAssertEqual(status.lastError, "Connection refused on 192.0.2.10:744.")
    }

    func testSettingsDecode() async throws {
        StubURLProtocol.handler = { _ in .init(status: 200, body: settingsJSON) }
        let settings = try await stubClient().settings()
        XCTAssertEqual(settings.recentIps, ["192.0.2.10", "192.0.2.11"])
        XCTAssertEqual(settings.loaderPort, 9090)
        XCTAssertEqual(settings.payloadPath, "payloads/ps4debug.bin")
    }

    func testUpdateSettingsSendsOnlySetFields() async throws {
        StubURLProtocol.handler = { _ in .init(status: 200, body: settingsJSON) }
        _ = try await stubClient().updateSettings(AppSettingsUpdate(loaderPort: 9021))
        XCTAssertEqual(StubURLProtocol.requests.first?.httpMethod, "PUT")
        let body = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(StubURLProtocol.bodies[0].utf8)) as? [String: Any])
        XCTAssertEqual(body.count, 1, "nil fields must be left out, or the API would clear them")
        XCTAssertEqual(body["loaderPort"] as? Int, 9021)
    }

    func testConnectSendsIp() async throws {
        StubURLProtocol.handler = { _ in .init(status: 200, body: statusJSONConnected) }
        _ = try await stubClient().connect(ip: "192.0.2.10")
        XCTAssertEqual(StubURLProtocol.requests.first?.httpMethod, "POST")
        XCTAssertEqual(StubURLProtocol.requests.first?.url?.path, "/api/connect")
        XCTAssertEqual(StubURLProtocol.bodies.first, #"{"ip":"192.0.2.10"}"#)
    }

    func testConnectWithoutIpSendsEmptyObject() async throws {
        StubURLProtocol.handler = { _ in .init(status: 200, body: statusJSONConnected) }
        _ = try await stubClient().connect(ip: nil)
        XCTAssertEqual(StubURLProtocol.bodies.first, "{}")
    }

    func testInjectDecodesResponseAndUsesLongTimeout() async throws {
        let body = """
        {"payloadPath": "/tmp/p.bin", "bytesSent": 123456, "loaderPort": 9090, "waitedSeconds": 3.2,
         "status": \(statusJSONConnected)}
        """
        StubURLProtocol.handler = { _ in .init(status: 200, body: body) }
        let result = try await stubClient().inject(InjectRequest(ip: nil, payloadPath: "/tmp/p.bin", loaderPort: 9090))
        XCTAssertEqual(result.bytesSent, 123456)
        XCTAssertEqual(result.waitedSeconds, 3.2)
        XCTAssertTrue(result.status.connected)
        XCTAssertEqual(StubURLProtocol.requests.first?.url?.path, "/api/inject")
        XCTAssertGreaterThan(StubURLProtocol.requests.first?.timeoutInterval ?? 0, 20,
                             "inject waits up to 20 s for ps4debug, so the request must wait longer")
    }

    func testErrorBodyBecomesTheMessage() async {
        let refused = "Connection refused on 192.0.2.10:9090. BinLoader is off or already used this session: " +
            "toggle it in GoldHEN or restart the PS4."
        StubURLProtocol.handler = { _ in .init(status: 502, body: #"{"error": "\#(refused)"}"#) }
        do {
            _ = try await stubClient().inject(InjectRequest())
            XCTFail("expected an error")
        } catch let error as APIError {
            XCTAssertEqual(error, .server(status: 502, message: refused))
            XCTAssertEqual(error.localizedDescription, refused)
            XCTAssertEqual(error.status, 502)
        } catch {
            XCTFail("wrong error type: \(error)")
        }
    }

    func testNonJsonErrorKeepsTheText() {
        XCTAssertEqual(APIClient.errorMessage(Data("oops".utf8), status: 500), "HTTP 500: oops")
        XCTAssertEqual(APIClient.errorMessage(Data(), status: 404), "HTTP 404")
    }

    func testNoBackendIsUnreachable() async {
        StubURLProtocol.handler = nil // the stub fails like a closed port
        do {
            _ = try await stubClient().status()
            XCTFail("expected an error")
        } catch let error as APIError {
            guard case .unreachable = error else { return XCTFail("expected unreachable, got \(error)") }
        } catch {
            XCTFail("wrong error type: \(error)")
        }
    }

    func testWrongJsonIsBadResponse() async {
        StubURLProtocol.handler = { _ in .init(status: 200, body: #"{"nope": 1}"#) }
        do {
            _ = try await stubClient().status()
            XCTFail("expected an error")
        } catch let error as APIError {
            guard case .badResponse = error else { return XCTFail("expected badResponse, got \(error)") }
        } catch {
            XCTFail("wrong error type: \(error)")
        }
    }
}
