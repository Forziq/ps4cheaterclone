import Foundation
@testable import PS4Mem

/// Answers URLSession requests from a closure instead of the network, so the client can be tested
/// without a backend. Each test sets `handler`; requests are recorded in `requests`.
final class StubURLProtocol: URLProtocol {
    struct Reply {
        var status: Int
        var body: String
    }

    nonisolated(unsafe) static var handler: ((URLRequest) throws -> Reply)?
    nonisolated(unsafe) static var requests: [URLRequest] = []
    /// Bodies of the recorded requests (URLSession moves httpBody into a stream, so it's read here).
    nonisolated(unsafe) static var bodies: [String] = []

    static func reset() {
        handler = nil
        requests = []
        bodies = []
    }

    static func session() -> URLSession {
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [StubURLProtocol.self]
        return URLSession(configuration: config)
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        Self.requests.append(request)
        Self.bodies.append(Self.readBody(request))
        guard let handler = Self.handler else {
            client?.urlProtocol(self, didFailWithError: URLError(.cannotConnectToHost))
            return
        }
        do {
            let reply = try handler(request)
            let response = HTTPURLResponse(url: request.url!, statusCode: reply.status, httpVersion: "HTTP/1.1",
                                           headerFields: ["Content-Type": "application/json"])!
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: Data(reply.body.utf8))
            client?.urlProtocolDidFinishLoading(self)
        } catch {
            client?.urlProtocol(self, didFailWithError: error)
        }
    }

    override func stopLoading() {}

    private static func readBody(_ request: URLRequest) -> String {
        if let data = request.httpBody { return String(decoding: data, as: UTF8.self) }
        guard let stream = request.httpBodyStream else { return "" }
        stream.open()
        defer { stream.close() }
        var data = Data()
        var buffer = [UInt8](repeating: 0, count: 4096)
        while stream.hasBytesAvailable {
            let n = stream.read(&buffer, maxLength: buffer.count)
            if n <= 0 { break }
            data.append(buffer, count: n)
        }
        return String(decoding: data, as: UTF8.self)
    }
}

/// A client whose requests go to the stub.
func stubClient() -> APIClient {
    APIClient(baseURL: URL(string: "http://127.0.0.1:5080")!, session: StubURLProtocol.session())
}

/// A status JSON as the API writes it (camelCase, nulls included).
let statusJSONConnected = """
{
  "connected": true,
  "ip": "192.0.2.10",
  "port": 744,
  "version": "1.3.4",
  "connectedAt": "2026-11-26T10:00:00+00:00",
  "debugLogPath": null,
  "defaultIp": "192.0.2.10",
  "defaultIpSource": "ps4ip.local",
  "lastError": null
}
"""

let statusJSONDisconnected = """
{
  "connected": false, "ip": null, "port": null, "version": null, "connectedAt": null,
  "debugLogPath": null, "defaultIp": null, "defaultIpSource": null,
  "lastError": "Connection refused on 192.0.2.10:744."
}
"""

let settingsJSON = """
{
  "consoleIp": "192.0.2.10",
  "recentIps": ["192.0.2.10", "192.0.2.11"],
  "commandPort": 744,
  "loaderPort": 9090,
  "payloadPath": "payloads/ps4debug.bin",
  "debugLogging": false
}
"""
