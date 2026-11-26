import Foundation

/// A failed API call, with a message that can go straight into the UI.
enum APIError: Error, Equatable, LocalizedError {
    /// The backend answered with an error status and `{"error": "..."}` (or another body).
    case server(status: Int, message: String)
    /// The backend didn't answer at all (not running, wrong port, timed out).
    case unreachable(String)
    /// The backend answered, but not with the JSON we expected (a bug on one side).
    case badResponse(String)

    var errorDescription: String? {
        switch self {
        case .server(_, let message): return message
        case .unreachable(let why): return "The backend didn't answer: \(why)"
        case .badResponse(let why): return "Unexpected answer from the backend: \(why)"
        }
    }

    /// The HTTP status, when the backend sent one.
    var status: Int? {
        if case .server(let status, _) = self { return status }
        return nil
    }
}

/// Talks to the C# backend over HTTP with async/await. One method per endpoint the app uses.
/// Every call throws `APIError`, so the UI only has one kind of error to show.
struct APIClient {
    let baseURL: URL
    let session: URLSession

    /// Normal calls get an answer within seconds. Inject waits up to 20 s for ps4debug, so it gets longer.
    static let normalTimeout: TimeInterval = 15
    static let injectTimeout: TimeInterval = 90

    init(baseURL: URL, session: URLSession = .shared) {
        self.baseURL = baseURL
        self.session = session
    }

    // MARK: Endpoints

    func status() async throws -> ConnectionStatus { try await send("GET", "/api/status") }

    func settings() async throws -> AppSettings { try await send("GET", "/api/settings") }

    func updateSettings(_ update: AppSettingsUpdate) async throws -> AppSettings {
        try await send("PUT", "/api/settings", body: update)
    }

    func connect(ip: String?) async throws -> ConnectionStatus {
        try await send("POST", "/api/connect", body: ConnectRequest(ip: ip))
    }

    func disconnect() async throws -> ConnectionStatus { try await send("POST", "/api/disconnect") }

    func inject(_ request: InjectRequest) async throws -> InjectResponse {
        try await send("POST", "/api/inject", body: request, timeout: Self.injectTimeout)
    }

    func processes() async throws -> ProcessList { try await send("GET", "/api/processes") }

    func processDetails(pid: Int) async throws -> ProcessDetails { try await send("GET", "/api/processes/\(pid)") }

    /// The sections of a process. `filter` nil = the backend's default name filter; an empty list = any name.
    func sections(pid: Int, filter: [String]? = nil, includeReadOnly: Bool = false) async throws -> SectionList {
        var query: [URLQueryItem] = []
        if let filter {
            // The backend splits on commas and drops empty parts, so "," means "no filter (any name)".
            // An empty value isn't sent, as the backend could read it as "no filter given" (= defaults).
            query.append(URLQueryItem(name: "filter", value: filter.isEmpty ? "," : filter.joined(separator: ",")))
        }
        if includeReadOnly { query.append(URLQueryItem(name: "includeReadOnly", value: "true")) }
        return try await send("GET", "/api/processes/\(pid)/maps", query: query)
    }

    // MARK: Plumbing

    /// A call without a body.
    func send<Response: Decodable>(_ method: String, _ path: String, query: [URLQueryItem] = [],
                                   timeout: TimeInterval = normalTimeout) async throws -> Response {
        try await send(method, path, query: query, bodyData: nil, timeout: timeout)
    }

    /// A call with a JSON body.
    func send<Body: Encodable, Response: Decodable>(_ method: String, _ path: String, body: Body,
                                                    timeout: TimeInterval = normalTimeout) async throws -> Response {
        let data: Data
        do { data = try JSONEncoder().encode(body) } catch {
            throw APIError.badResponse("couldn't encode the request: \(error.localizedDescription)")
        }
        return try await send(method, path, query: [], bodyData: data, timeout: timeout)
    }

    private func send<Response: Decodable>(_ method: String, _ path: String, query: [URLQueryItem],
                                           bodyData: Data?, timeout: TimeInterval) async throws -> Response {
        var url = baseURL.appendingPathComponent(path)
        if !query.isEmpty, var parts = URLComponents(url: url, resolvingAgainstBaseURL: false) {
            parts.queryItems = query
            url = parts.url ?? url
        }
        var request = URLRequest(url: url, timeoutInterval: timeout)
        request.httpMethod = method
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        if let bodyData {
            request.httpBody = bodyData
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        }

        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await session.data(for: request)
        } catch {
            throw APIError.unreachable(error.localizedDescription)
        }

        guard let http = response as? HTTPURLResponse else {
            throw APIError.badResponse("not an HTTP response")
        }
        guard (200..<300).contains(http.statusCode) else {
            throw APIError.server(status: http.statusCode, message: Self.errorMessage(data, status: http.statusCode))
        }
        do {
            return try JSONDecoder().decode(Response.self, from: data)
        } catch {
            throw APIError.badResponse("\(method) \(path): \(error)")
        }
    }

    /// The `error` text from `{"error": "..."}`, or the raw body if it isn't that shape.
    static func errorMessage(_ data: Data, status: Int) -> String {
        if let body = try? JSONDecoder().decode(ApiErrorBody.self, from: data) {
            return body.error
        }
        let text = String(data: data, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        return text.isEmpty ? "HTTP \(status)" : "HTTP \(status): \(text)"
    }
}
