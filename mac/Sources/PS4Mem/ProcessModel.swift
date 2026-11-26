import Foundation
import Observation

/// The process picker's state. Every process is listed; the backend marks the likely game and says why,
/// and the app pre-highlights it, but **the user chooses**: nothing is chosen until they press
/// "Use this process" (or double-click). The suggestion can be wrong: AC3 runs as
/// `ac3_engine_orbis_fn.elf`, and some system processes also end in `.elf`.
@MainActor
@Observable
final class ProcessModel {
    let client: APIClient

    /// Every process, by pid.
    private(set) var processes: [ProcessEntry] = []
    /// The backend's suggestion (nil when it found no likely game) and why.
    private(set) var suggestedPid: Int?
    private(set) var suggestionReason: String = ""
    /// The row highlighted in the list. Starts on the suggestion; choosing still needs a click.
    var highlightedPid: Int?
    /// The process the user chose, with its title ID. Nil until they choose.
    private(set) var chosen: ProcessDetails?
    private(set) var isLoading = false
    /// The backend's error text from the last load or choose, shown as it is.
    private(set) var error: String?

    init(client: APIClient) {
        self.client = client
    }

    /// Text beside the suggested process's name.
    static func suggestionLabel(_ reason: String?) -> String {
        guard let reason, !reason.isEmpty else { return "suggested" }
        return "suggested: \(reason)"
    }

    /// One line above the list: what's suggested, or that nothing is.
    var suggestionText: String {
        guard let pid = suggestedPid, let entry = processes.first(where: { $0.pid == pid }) else {
            return processes.isEmpty ? "" : "No game suggested. Is a game running? Choose its process yourself."
        }
        return "Suggested: \(entry.name) (pid \(entry.pid)), \(suggestionReason). You still choose."
    }

    /// One line for the chosen process: name, title ID and pid.
    var chosenText: String {
        guard let chosen else { return "No process chosen" }
        let title = chosen.titleId.isEmpty ? "no title ID" : chosen.titleId
        return "\(chosen.name) · \(title) · pid \(chosen.pid)"
    }

    /// True when the highlighted row can be chosen (it isn't already the chosen one).
    var canChooseHighlighted: Bool {
        guard let pid = highlightedPid, !isLoading else { return false }
        return pid != chosen?.pid
    }

    /// Fetches the process list. Keeps the highlight if that pid still exists, else highlights the
    /// suggestion. A chosen process that has gone (the game was closed) is dropped.
    func load() async {
        guard !isLoading else { return }
        isLoading = true
        error = nil
        defer { isLoading = false }
        do {
            let list = try await client.processes()
            processes = list.processes
            suggestedPid = list.suggestedPid
            suggestionReason = list.suggestionReason
            let pids = Set(list.processes.map(\.pid))
            if highlightedPid.map({ !pids.contains($0) }) ?? true { highlightedPid = list.suggestedPid }
            if let chosenPid = chosen?.pid, !pids.contains(chosenPid) { chosen = nil }
        } catch {
            self.error = error.localizedDescription
        }
    }

    /// Chooses a process: fetches its name, path and title ID. Returns true when it worked.
    @discardableResult
    func choose(pid: Int) async -> Bool {
        guard !isLoading else { return false }
        isLoading = true
        error = nil
        defer { isLoading = false }
        do {
            chosen = try await client.processDetails(pid: pid)
            highlightedPid = pid
            return true
        } catch {
            self.error = error.localizedDescription
            return false
        }
    }

    /// Forgets everything (on disconnect: the pids belong to that connection's PS4 session).
    func clear() {
        processes = []
        suggestedPid = nil
        suggestionReason = ""
        highlightedPid = nil
        chosen = nil
        error = nil
    }
}
