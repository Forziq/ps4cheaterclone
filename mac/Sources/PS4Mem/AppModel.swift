import Foundation
import Observation

/// Everything the window shows, in one place. Views read these properties and call these methods;
/// SwiftUI redraws by itself when a property changes (that's what `@Observable` does).
/// `@MainActor` means it's only touched on the main thread, so there are no threading surprises.
@MainActor
@Observable
final class AppModel {
    /// Where the backend listens. PS4MEM_API_URL overrides it (the API's default is the same address).
    nonisolated static let apiURL: URL = {
        let text = ProcessInfo.processInfo.environment["PS4MEM_API_URL"] ?? "http://127.0.0.1:5080"
        return URL(string: text) ?? URL(string: "http://127.0.0.1:5080")!
    }()

    let client: APIClient
    let backend: BackendProcess
    /// The inject sheet's state (payload file, loader port, progress).
    let inject: InjectModel
    /// The process picker. Loaded when a connection starts, cleared when it ends.
    let processes: ProcessModel
    /// The sections panel for the chosen process.
    let sections: SectionsModel
    /// The scanner (and, through it, the results). The backend keeps one set of results per connection.
    let scan: ScanModel
    /// The results table (the first 5,000 results, current values of the rows on screen).
    let results: ResultsModel
    /// The chosen game's cheat list (one per title ID, saved by the backend).
    let cheats: CheatListModel

    // MARK: Connection state

    /// The last status from the backend.
    private(set) var status: ConnectionStatus = .disconnected
    /// The saved settings (recent IPs, payload path, loader port). Nil until the backend answers.
    private(set) var settings: AppSettings?
    /// What's typed in the IP field. Empty means "let the backend pick" (PS4_IP, ps4ip.local or saved IP).
    var ipText: String = ""
    /// True while connect or disconnect is waiting for the backend.
    private(set) var isBusy = false
    /// The error from the last button press (connect/disconnect). The backend's text, shown as it is.
    private(set) var actionError: String?

    /// How often the status is fetched again, so a dropped connection shows up on its own.
    private let statusPollInterval: TimeInterval
    private var pollTask: Task<Void, Never>?

    init(client: APIClient = APIClient(baseURL: AppModel.apiURL), backend: BackendProcess? = nil,
         statusPollInterval: TimeInterval = 3) {
        self.client = client
        self.backend = backend ?? BackendProcess(client: client) {
            try .automatic(apiURL: client.baseURL)
        }
        self.inject = InjectModel(client: client)
        self.processes = ProcessModel(client: client)
        self.sections = SectionsModel(client: client)
        self.scan = ScanModel(client: client)
        let results = ResultsModel(client: client)
        self.results = results
        let cheats = CheatListModel(client: client)
        self.cheats = cheats
        // Cheats added from the results show up in the list.
        results.onCheatsAdded = { await cheats.reload() }
        // After a scan, undo or new scan, the table loads the new results' first page.
        scan.onResultsChanged = { status in await results.reload(status: status) }
        self.statusPollInterval = statusPollInterval
    }

    /// Called once when the window appears: start (or find) the backend, then load the status and settings.
    func startUp() async {
        await backend.start()
        guard backend.isRunning else { return }
        await refresh()
        if ipText.isEmpty { ipText = suggestedIp }
        startPolling()
        // Current values of the rows on screen, every second, while connected and no scan runs.
        results.startRefreshing { [weak self] in
            guard let self else { return false }
            return self.status.connected && !self.scan.isRunning && !self.scan.isBusy
        }
        // The cheats' values in memory, every second, under the same conditions.
        cheats.startRefreshing { [weak self] in
            guard let self else { return false }
            return self.status.connected && !self.scan.isRunning && !self.scan.isBusy
        }
    }

    /// Called when the app quits (also on Ctrl+C in the terminal).
    func shutDown() {
        pollTask?.cancel()
        pollTask = nil
        results.stopRefreshing()
        cheats.stopRefreshing()
        backend.stop()
    }

    /// The IP to pre-fill: the backend's default (PS4_IP, ps4ip.local or saved), else the newest recent IP.
    var suggestedIp: String {
        status.defaultIp ?? settings?.consoleIp.nilIfEmpty ?? settings?.recentIps.first ?? ""
    }

    /// Recent IPs for the menu next to the IP field (newest first).
    var recentIps: [String] { settings?.recentIps ?? [] }

    /// One line for the status area: connected (IP, port, ps4debug version) or why not.
    var statusText: String {
        if status.connected {
            let place = [status.ip, status.port.map(String.init)].compactMap { $0 }.joined(separator: ":")
            let version = status.version.map { "ps4debug \($0)" } ?? "ps4debug (version unknown)"
            return "Connected to \(place) · \(version)"
        }
        return "Not connected"
    }

    // MARK: Actions

    /// Connects to ps4debug on the typed IP (or the backend's default when the field is empty).
    func connect() async {
        await run {
            let ip = self.ipText.trimmingCharacters(in: .whitespaces)
            await self.apply(try await self.client.connect(ip: ip.isEmpty ? nil : ip))
            // The backend saves a working IP as the newest recent IP; reload so the menu shows it.
            self.settings = try? await self.client.settings()
            if let ip = self.status.ip { self.ipText = ip }
        }
    }

    func disconnect() async {
        await run { await self.apply(try await self.client.disconnect()) }
    }

    /// Injects the payload (from the inject sheet) to the typed IP, then takes the new status.
    /// Returns true when it worked and the app is connected (the sheet then closes).
    func injectPayload() async -> Bool {
        guard !isBusy else { return false }
        isBusy = true
        actionError = nil
        defer { isBusy = false }

        let ip = ipText.trimmingCharacters(in: .whitespaces)
        guard let response = await inject.inject(ip: ip.isEmpty ? nil : ip) else {
            if let status = try? await client.status() { await apply(status) }
            return false
        }
        await apply(response.status)
        if let ip = response.status.ip { ipText = ip }
        // A payload path, loader port and IP that worked are saved by the backend: reload them.
        if let settings = try? await client.settings() { self.settings = settings }
        return response.status.connected
    }

    /// The inject sheet saved a setting (payload path or loader port).
    func settingsChanged(_ settings: AppSettings) {
        self.settings = settings
    }

    /// Fetches the status and settings again. Errors here aren't shown as action errors: if the backend
    /// is gone, the status just stays as it was and the next poll tries again.
    func refresh() async {
        if let status = try? await client.status() { await apply(status) }
        if let settings = try? await client.settings() { self.settings = settings }
    }

    /// Takes a new status. On connect the process list loads; on disconnect (or a dropped connection)
    /// the process picker and sections are cleared, since pids belong to that connection.
    func apply(_ newStatus: ConnectionStatus) async {
        let wasConnected = status.connected
        status = newStatus
        if newStatus.connected && !wasConnected {
            await processes.load()
            // Results the backend still has (or a scan still running) show up again. Not awaited:
            // following a running scan lasts as long as the scan.
            let scan = self.scan
            Task { await scan.refresh() }
        } else if !newStatus.connected && wasConnected {
            processes.clear()
            sections.clear()
            // The backend drops the results and cancels a scan on disconnect.
            scan.clear()
            results.clear()
            // The backend stops every lock on disconnect.
            cheats.clear()
        }
    }

    /// Keeps the sections panel on the chosen process: loads the new one's sections, or clears the
    /// panel when no process is chosen (e.g. the game was closed and the list refreshed).
    func syncSectionsWithChosenProcess() async {
        guard let pid = processes.chosen?.pid else {
            sections.clear()
            return
        }
        if sections.pid != pid { await sections.load(pid: pid) }
    }

    /// Keeps the cheat list on the chosen process's game: shows its list, or clears it when no
    /// process is chosen.
    func syncCheatsWithChosenProcess() async {
        guard let chosen = processes.chosen else {
            cheats.clear()
            return
        }
        await cheats.show(titleId: chosen.titleId, game: chosen.name, pid: chosen.pid)
    }

    /// The chosen game's title ID, when the results belong to the chosen process (cheats from the
    /// results go in that game's list). Nil otherwise.
    var cheatTitleId: String? {
        guard let chosen = processes.chosen, scan.status.pid == chosen.pid, !chosen.titleId.isEmpty else { return nil }
        return chosen.titleId
    }

    /// Runs a button action: marks busy, clears the old error, shows the new one if it fails.
    private func run(_ action: @escaping () async throws -> Void) async {
        guard !isBusy else { return }
        isBusy = true
        actionError = nil
        defer { isBusy = false }
        do {
            try await action()
        } catch {
            actionError = error.localizedDescription
            // A failed connect sets lastError in the backend; pick up the new status too.
            if let status = try? await client.status() { await apply(status) }
        }
    }

    private func startPolling() {
        pollTask?.cancel()
        let interval = statusPollInterval
        pollTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: UInt64(interval * 1_000_000_000))
                guard let self, !Task.isCancelled else { return }
                if !self.isBusy, let status = try? await self.client.status() { await self.apply(status) }
            }
        }
    }
}

extension String {
    /// nil for an empty string, so `a.nilIfEmpty ?? b` falls through to b.
    var nilIfEmpty: String? { isEmpty ? nil : self }
}
