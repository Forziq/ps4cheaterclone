import Foundation
import Observation

/// The cheat list of the chosen game (one list per title ID, saved by the backend on the Mac after
/// every change: there's no separate Save step).
///
/// Safety: memory is written only by `writeValue` (Return in a value field) and `setLocked(true)`
/// (the lock box). Both are only called from the user's own click or key press. Everything else here
/// saves on the Mac or reads memory.
@MainActor
@Observable
final class CheatListModel {
    let client: APIClient

    /// The game the list belongs to. Nil when no process with a title ID is chosen.
    private(set) var titleId: String?
    /// The game's name (saved with the list), else the process name.
    private(set) var game: String?
    /// The process writes, locks and reads go to.
    private(set) var pid: Int?
    /// Where the backend saves the list.
    private(set) var file: String?
    private(set) var cheats: [Cheat] = []
    /// Each cheat's value in memory now, by cheat id (read every second while connected).
    private(set) var current: [String: String] = [:]
    /// Why the last read of the current values failed, or nil.
    private(set) var valuesError: String?
    private(set) var isLoading = false
    /// Why the last action failed (the backend's text).
    private(set) var error: String?
    /// What the last action did, e.g. "Wrote 999 to 0x2001A3F10 (was 100)".
    private(set) var notice: String?

    let refreshInterval: TimeInterval
    private var refreshTask: Task<Void, Never>?

    init(client: APIClient, refreshInterval: TimeInterval = 1) {
        self.client = client
        self.refreshInterval = refreshInterval
    }

    /// One line above the table: the game and how many cheats.
    var headerText: String {
        guard let titleId else { return "Cheat list" }
        let name = game.map { " · \($0)" } ?? ""
        let count = cheats.count == 1 ? "1 cheat" : "\(cheats.count) cheats"
        return "Cheat list · \(titleId)\(name) · \(count)"
    }

    var lockedCount: Int { cheats.filter(\.locked).count }

    // MARK: Loading

    /// Shows the list of the chosen process's game. The same game and process again does nothing.
    /// A process without a title ID has no list.
    func show(titleId: String, game: String?, pid: Int) async {
        let title = titleId.trimmingCharacters(in: .whitespaces)
        guard !title.isEmpty else {
            clear()
            error = "This process has no title ID, so it has no cheat list."
            return
        }
        if title == self.titleId && pid == self.pid { return }
        let sameGame = title == self.titleId
        self.titleId = title
        self.pid = pid
        self.game = game
        if !sameGame {
            cheats = []
            current = [:]
            file = nil
        }
        error = nil
        notice = nil
        valuesError = nil
        await reload()
    }

    /// Fetches the list again (after cheats were added from the results, or to pick up lock state).
    func reload() async {
        guard let titleId else { return }
        isLoading = true
        defer { isLoading = false }
        do {
            apply(try await client.cheats(titleId: titleId))
        } catch {
            self.error = error.localizedDescription
        }
    }

    private func apply(_ table: CheatTable) {
        guard table.titleId == titleId else { return }
        cheats = table.cheats
        file = table.file
        if let name = table.game, !name.isEmpty { game = name }
        // Values of deleted cheats go.
        let ids = Set(table.cheats.map(\.id))
        current = current.filter { ids.contains($0.key) }
    }

    private func replace(_ cheat: Cheat) {
        if let i = cheats.firstIndex(where: { $0.id == cheat.id }) { cheats[i] = cheat }
    }

    // MARK: Editing (saved on the Mac, no memory writes)

    /// Saves a new description (Return in the description field).
    func setDescription(_ id: String, _ text: String) async {
        guard let titleId, let old = cheats.first(where: { $0.id == id }) else { return }
        let text = text.trimmingCharacters(in: .whitespaces)
        guard text != old.description else { return }
        await act {
            self.replace(try await self.client.updateCheat(titleId: titleId, id: id, CheatEdit(description: text)))
        }
    }

    /// Deletes a cheat from the list (a locked one stops first).
    func delete(_ id: String) async {
        guard let titleId, let cheat = cheats.first(where: { $0.id == id }) else { return }
        await act {
            try await self.client.deleteCheat(titleId: titleId, id: id)
            self.cheats.removeAll { $0.id == id }
            self.current[id] = nil
            self.notice = "Deleted \(cheat.address)"
        }
    }

    // MARK: Memory (only from a click)

    /// **Writes memory.** Saves `text` as the cheat's value and writes it into the process.
    /// Called only on Return in the value field. Numbers starting with 0x are read as hex.
    func writeValue(_ id: String, _ text: String) async {
        guard let titleId, let cheat = cheats.first(where: { $0.id == id }) else { return }
        let value = text.trimmingCharacters(in: .whitespaces)
        guard !value.isEmpty else {
            error = "Type a value, then press Return to write it."
            return
        }
        guard let pid else {
            error = "Choose the game's process first."
            return
        }
        await act {
            let result = try await self.client.writeCheat(titleId: titleId, id: id,
                                                          CheatWriteRequest(pid: pid, value: value, hex: nil))
            self.replace(result.cheat)
            self.current[id] = result.cheat.value
            self.notice = "Wrote \(result.cheat.value) to \(cheat.address) (was \(result.oldValue))"
        }
    }

    /// Lock box: on **writes memory** now and every ~200 ms until it's turned off; off stops that.
    /// Called only when the user ticks or unticks the box.
    func setLocked(_ id: String, _ on: Bool) async {
        guard let titleId, let cheat = cheats.first(where: { $0.id == id }) else { return }
        if on {
            guard let pid else {
                error = "Choose the game's process first."
                return
            }
            await act {
                self.replace(try await self.client.lockCheat(titleId: titleId, id: id, pid: pid))
                self.notice = "Locked \(cheat.address) at \(cheat.value)"
            }
        } else {
            await act {
                self.replace(try await self.client.unlockCheat(titleId: titleId, id: id))
                self.notice = "Unlocked \(cheat.address)"
            }
        }
    }

    /// Runs an action: clears the old error and notice, shows the new error if it fails.
    private func act(_ action: @escaping () async throws -> Void) async {
        error = nil
        notice = nil
        do {
            try await action()
        } catch {
            self.error = error.localizedDescription
            // A failed lock or write can change the list (e.g. a lock stopped): show it as it is.
            await reload()
        }
    }

    // MARK: Current values (reads only)

    /// Reads every cheat's value from the process. While a cheat is locked, the list is fetched too,
    /// so a lock that stopped by itself (a write failed) shows up.
    func refreshCurrentValues() async {
        guard let titleId, let pid, !cheats.isEmpty else { return }
        if lockedCount > 0, let table = try? await client.cheats(titleId: titleId) { apply(table) }
        do {
            let values = try await client.cheatValues(titleId: titleId, pid: pid)
            guard values.titleId == self.titleId, values.pid == self.pid else { return }
            for value in values.values where current[value.id] != value.value {
                current[value.id] = value.value
            }
            valuesError = nil
        } catch {
            valuesError = "Couldn't read the current values: \(error.localizedDescription)"
        }
    }

    /// Reads the values every `refreshInterval` while `shouldRefresh` is true (connected, no scan running).
    func startRefreshing(while shouldRefresh: @escaping @MainActor () -> Bool) {
        refreshTask?.cancel()
        let interval = refreshInterval
        refreshTask = Task { [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: UInt64(interval * 1_000_000_000))
                guard let self, !Task.isCancelled else { return }
                if shouldRefresh() { await self.refreshCurrentValues() }
            }
        }
    }

    func stopRefreshing() {
        refreshTask?.cancel()
        refreshTask = nil
    }

    /// Forgets the list (no process chosen, or disconnected: the backend stops every lock then).
    func clear() {
        titleId = nil
        game = nil
        pid = nil
        file = nil
        cheats = []
        current = [:]
        valuesError = nil
        error = nil
        notice = nil
    }
}
