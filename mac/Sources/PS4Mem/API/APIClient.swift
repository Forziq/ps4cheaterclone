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

    // MARK: Scans

    /// Starts a first scan. It runs in the backend; follow it with `scanProgress()`.
    func firstScan(_ request: FirstScanRequest) async throws -> ScanStatus {
        try await send("POST", "/api/scan/first", body: request)
    }

    /// Starts a next scan on the current results.
    func nextScan(_ request: NextScanRequest) async throws -> ScanStatus {
        try await send("POST", "/api/scan/next", body: request)
    }

    /// Cancels a running scan and drops every result.
    func newScan() async throws -> ScanStatus { try await send("POST", "/api/scan/new") }

    /// Back to the results before the last next scan.
    func undoScan() async throws -> ScanStatus { try await send("POST", "/api/scan/undo") }

    /// Asks the running scan to stop; the previous results stay.
    func cancelScan() async throws -> ScanStatus { try await send("POST", "/api/scan/cancel") }

    func scanStatus() async throws -> ScanStatus { try await send("GET", "/api/scan/status") }

    /// A page of results: rows `offset ..< offset + limit`. `hex` shows the values in hex.
    func scanResults(offset: Int64, limit: Int, hex: Bool) async throws -> ScanResultsPage {
        try await send("GET", "/api/scan/results", query: [
            URLQueryItem(name: "offset", value: String(offset)),
            URLQueryItem(name: "limit", value: String(limit)),
            URLQueryItem(name: "hex", value: hex ? "true" : "false"),
        ])
    }

    /// Reads the current values of rows `offset ..< offset + limit` from the PS4 (a read, never a write).
    func resultValues(_ request: ResultValuesRequest) async throws -> ResultValues {
        try await send("POST", "/api/scan/results/values", body: request)
    }

    /// The scan's progress as it happens, from the server-sent events at `GET /api/scan/progress`:
    /// one status per "progress" event, then the final status ("done" event), then the stream ends.
    /// If no scan is running, it gives the "done" status at once.
    func scanProgress() -> AsyncThrowingStream<ScanEvent, Error> {
        var request = URLRequest(url: baseURL.appendingPathComponent("/api/scan/progress"),
                                 timeoutInterval: Self.progressTimeout)
        request.setValue("text/event-stream", forHTTPHeaderField: "Accept")
        let session = self.session

        return AsyncThrowingStream { continuation in
            let task = Task {
                do {
                    let (bytes, response) = try await session.bytes(for: request)
                    guard let http = response as? HTTPURLResponse else {
                        throw APIError.badResponse("not an HTTP response")
                    }
                    guard (200..<300).contains(http.statusCode) else {
                        throw APIError.server(status: http.statusCode, message: "HTTP \(http.statusCode) from the progress stream")
                    }
                    var parser = ServerSentEvents()
                    for try await line in bytes.lines {
                        guard let event = parser.feed(line) else { continue }
                        guard let status = try? JSONDecoder().decode(ScanStatus.self, from: Data(event.data.utf8)) else {
                            throw APIError.badResponse("progress event: \(event.data)")
                        }
                        let scanEvent = ScanEvent(isDone: event.name == "done", status: status)
                        continuation.yield(scanEvent)
                        if scanEvent.isDone { break }
                    }
                    continuation.finish()
                } catch let error as APIError {
                    continuation.finish(throwing: error)
                } catch is CancellationError {
                    continuation.finish()
                } catch {
                    continuation.finish(throwing: APIError.unreachable(error.localizedDescription))
                }
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    /// The progress stream sends an event every 250 ms while a scan runs, so a minute without one
    /// means the backend is stuck or gone. (This is the gap allowed between events, not a scan time limit.)
    static let progressTimeout: TimeInterval = 60

    // MARK: Cheats

    /// Adds a cheat to a game's list (saved on the Mac; nothing is written to the PS4).
    func addCheat(titleId: String, _ request: NewCheatRequest) async throws -> Cheat {
        try await send("POST", "/api/cheats/\(titleId)", body: request)
    }

    /// A game's cheat list (empty if none is saved yet).
    func cheats(titleId: String) async throws -> CheatTable { try await send("GET", "/api/cheats/\(titleId)") }

    /// Saves changed fields of a cheat. Doesn't write memory.
    func updateCheat(titleId: String, id: String, _ edit: CheatEdit) async throws -> Cheat {
        try await send("PUT", "/api/cheats/\(titleId)/\(id)", body: edit)
    }

    /// Deletes a cheat (a locked one is unlocked first). The backend answers 204 with no body.
    func deleteCheat(titleId: String, id: String) async throws {
        _ = try await sendForData("DELETE", "/api/cheats/\(titleId)/\(id)", query: [], bodyData: nil,
                                  timeout: Self.normalTimeout)
    }

    /// **Writes memory.** Only call this from a click (Return in the value field).
    func writeCheat(titleId: String, id: String, _ request: CheatWriteRequest) async throws -> CheatWriteResult {
        try await send("POST", "/api/cheats/\(titleId)/\(id)/write", body: request)
    }

    /// **Writes memory** now and every ~200 ms until unlocked. Only call this from a click.
    func lockCheat(titleId: String, id: String, pid: Int) async throws -> Cheat {
        try await send("POST", "/api/cheats/\(titleId)/\(id)/lock", body: CheatPidRequest(pid: pid))
    }

    /// Stops a lock (fine if it wasn't locked).
    func unlockCheat(titleId: String, id: String) async throws -> Cheat {
        try await send("POST", "/api/cheats/\(titleId)/\(id)/unlock")
    }

    /// Reads every cheat's value from the process (a read, never a write).
    func cheatValues(titleId: String, pid: Int) async throws -> CheatValues {
        try await send("POST", "/api/cheats/\(titleId)/values", body: CheatPidRequest(pid: pid))
    }

    /// The saved list as a cheat file (the same JSON as the file on disk).
    func exportCheats(titleId: String) async throws -> Data {
        try await sendForData("GET", "/api/cheats/\(titleId)/export", query: [], bodyData: nil,
                              timeout: Self.normalTimeout)
    }

    /// Imports a cheat file (as export makes). Writes no memory.
    func importCheats(titleId: String, file: Data, mode: CheatImportMode) async throws -> CheatImportResult {
        let data = try await sendForData("POST", "/api/cheats/\(titleId)/import",
                                         query: [URLQueryItem(name: "mode", value: mode.rawValue)],
                                         bodyData: file, timeout: Self.normalTimeout)
        return try decode(data, "POST /api/cheats/\(titleId)/import")
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
        let data = try await sendForData(method, path, query: query, bodyData: bodyData, timeout: timeout)
        return try decode(data, "\(method) \(path)")
    }

    private func decode<Response: Decodable>(_ data: Data, _ what: String) throws -> Response {
        do {
            return try JSONDecoder().decode(Response.self, from: data)
        } catch {
            throw APIError.badResponse("\(what): \(error)")
        }
    }

    /// Sends a request and returns the body of a 2xx answer as it is (the other calls decode it).
    private func sendForData(_ method: String, _ path: String, query: [URLQueryItem],
                             bodyData: Data?, timeout: TimeInterval) async throws -> Data {
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
        return data
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
