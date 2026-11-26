import Foundation
import Observation

/// The inject sheet's state: which payload file, which loader port, and the injection in progress.
///
/// Injecting sends the ps4debug `.bin` to GoldHEN's BinLoader, then the backend polls port 744 every
/// second for up to 20 s and connects when ps4debug answers. The backend refuses (409) if ps4debug
/// already answers, because a second copy can freeze the console, so the app doesn't check that itself.
@MainActor
@Observable
final class InjectModel {
    /// GoldHEN's BinLoader uses 9090; other loaders use 9020 or 9021.
    static let standardLoaderPorts = [9090, 9020, 9021]

    let client: APIClient

    /// The payload file. A relative path (the default) is relative to the repo folder the backend runs in.
    var payloadPath: String = ""
    var loaderPort: Int = 9090
    private(set) var isInjecting = false
    /// Seconds since Inject was pressed, for the progress line.
    private(set) var elapsed: TimeInterval = 0
    /// The backend's error text, shown as it is (it already explains what to do).
    private(set) var error: String?
    /// The last successful injection.
    private(set) var result: InjectResponse?

    init(client: APIClient) {
        self.client = client
    }

    /// The ports in the picker: the standard ones, plus a saved one that isn't standard.
    var loaderPortOptions: [Int] {
        Self.standardLoaderPorts.contains(loaderPort) ? Self.standardLoaderPorts : Self.standardLoaderPorts + [loaderPort]
    }

    /// Fills the fields from the saved settings (when the sheet opens). Clears an old error.
    func load(from settings: AppSettings?) {
        if let settings {
            payloadPath = settings.payloadPath
            loaderPort = settings.loaderPort
        }
        error = nil
        result = nil
    }

    /// The payload path as a file URL: a relative path is resolved against the repo folder (PS4MEM_REPO_ROOT,
    /// set by run.sh), else the current folder. Used to start the file picker in the right place.
    func payloadURL(environment: [String: String] = ProcessInfo.processInfo.environment) -> URL? {
        guard !payloadPath.isEmpty else { return nil }
        if payloadPath.hasPrefix("/") { return URL(fileURLWithPath: payloadPath) }
        let base = environment["PS4MEM_REPO_ROOT"] ?? FileManager.default.currentDirectoryPath
        return URL(fileURLWithPath: base).appendingPathComponent(payloadPath).standardizedFileURL
    }

    /// The user picked a file: use it and save it in the settings, so it's remembered next time.
    /// Returns the saved settings (nil if saving failed; the error is shown).
    @discardableResult
    func choosePayload(_ url: URL) async -> AppSettings? {
        payloadPath = url.path
        error = nil
        do {
            return try await client.updateSettings(AppSettingsUpdate(payloadPath: url.path))
        } catch {
            self.error = "Couldn't save the payload path: \(error.localizedDescription)"
            return nil
        }
    }

    /// The loader port changed: remember it too.
    @discardableResult
    func chooseLoaderPort(_ port: Int) async -> AppSettings? {
        loaderPort = port
        return try? await client.updateSettings(AppSettingsUpdate(loaderPort: port))
    }

    /// Sends the payload and waits for ps4debug. Returns the backend's answer, or nil with `error` set.
    func inject(ip: String?) async -> InjectResponse? {
        guard !isInjecting else { return nil }
        let path = payloadPath.trimmingCharacters(in: .whitespaces)
        guard !path.isEmpty else {
            error = "Choose the ps4debug payload (.bin) first."
            return nil
        }

        isInjecting = true
        error = nil
        result = nil
        elapsed = 0
        let started = Date()
        // Counts the seconds while we wait, so the sheet shows that something is happening.
        let ticker = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 200_000_000)
                self?.elapsed = Date().timeIntervalSince(started)
            }
        }
        defer {
            ticker.cancel()
            isInjecting = false
        }

        do {
            let response = try await client.inject(InjectRequest(ip: ip, payloadPath: path, loaderPort: loaderPort))
            result = response
            return response
        } catch {
            self.error = error.localizedDescription
            return nil
        }
    }

    /// The line under the progress spinner.
    var progressText: String {
        let seconds = Int(elapsed)
        return seconds < 1
            ? "Sending the payload to port \(loaderPort)…"
            : "Waiting for ps4debug to start (up to 20 s)… \(seconds) s"
    }
}
