import Foundation
import Observation

/// One row of the results table.
struct ResultRow: Identifiable, Equatable {
    /// The row's place in the backend's results (0 = first). Also its id in the table.
    var index: Int64
    var address: String
    /// What the last scan found.
    var previous: String
    /// What's in memory now (read again every second for the rows on screen). Nil until read.
    var current: String?
    var section: String
    var sectionStart: String
    var sectionOffset: String

    var id: Int64 { index }

    /// The value now if it has been read, else the scan's value.
    var latest: String { current ?? previous }

    /// True when the value in memory differs from what the scan found (shown in red).
    var changed: Bool { current.map { $0 != previous } ?? false }

    init(_ row: ScanResultRow) {
        index = row.index
        address = row.address
        previous = row.value
        section = row.section
        sectionStart = row.sectionStart
        sectionOffset = row.sectionOffset
    }
}

/// The results table's state. The backend keeps every result (millions after an unknown-value scan);
/// the app shows the first `maxShown` (5,000), fetched a page at a time as the table scrolls.
/// The current value of the rows on screen is read from the PS4 again every `refreshInterval`.
/// That is a read only: nothing here writes to memory.
@MainActor
@Observable
final class ResultsModel {
    let client: APIClient

    /// The rows fetched so far, in result order.
    private(set) var rows: [ResultRow] = []
    /// Every result the backend has.
    private(set) var total: Int64 = 0
    /// Which results these are. A scan or undo changes it, and old pages or values are then ignored.
    private(set) var scanNumber = 0
    /// The results' type name ("u32" ...), for a cheat made from a row.
    private(set) var type: String?
    /// Values are shown in hex.
    private(set) var hex = false
    private(set) var isLoading = false
    /// Why the last page or add-to-cheat-list failed (the backend's text).
    private(set) var error: String?
    /// What the last double-click did, e.g. "Added 0x2001A3F10 to the cheat list of CUSA11711".
    private(set) var notice: String?

    /// At most this many rows are fetched (PROMPT: "show first ~5,000 but keep all").
    let maxShown: Int
    /// Rows per fetch.
    let pageSize: Int
    /// At most this many rows are read again per refresh (more than fit on a screen).
    let maxRefreshRows = 300
    let refreshInterval: TimeInterval

    /// Indexes of the rows on screen (from each row's appear and disappear).
    private var visible: Set<Int64> = []
    private var refreshTask: Task<Void, Never>?

    init(client: APIClient, maxShown: Int = 5_000, pageSize: Int = 1_000, refreshInterval: TimeInterval = 1) {
        self.client = client
        self.maxShown = maxShown
        self.pageSize = pageSize
        self.refreshInterval = refreshInterval
    }

    /// The most rows there will be (5,000, or fewer results).
    var shownLimit: Int { Int(min(total, Int64(maxShown))) }

    var hasMore: Bool { rows.count < shownLimit }

    /// Under the table: "Showing 1,000 of 5,000 shown (1,234,567 results)" and so on.
    var countText: String {
        if total == 0 { return scanNumber == 0 ? "" : "No results" }
        if Int64(rows.count) == total { return "\(total.formatted()) results" }
        return "Showing \(rows.count.formatted()) of \(total.formatted()) results"
            + (total > Int64(maxShown) ? " (the first \(maxShown.formatted()) are listed)" : "")
    }

    // MARK: Loading

    /// Starts over with the results in `status` (called after a scan, undo or new scan).
    func reload(status: ScanStatus) async {
        await reload(scanNumber: status.scanNumber, hex: hex)
    }

    /// Shows the values in hex or not: fetches the rows again, as the backend formats them.
    func setHex(_ on: Bool) async {
        guard on != hex else { return }
        await reload(scanNumber: scanNumber, hex: on)
    }

    private func reload(scanNumber: Int, hex: Bool) async {
        self.scanNumber = scanNumber
        self.hex = hex
        rows = []
        total = 0
        type = nil
        visible = []
        error = nil
        notice = nil
        guard scanNumber > 0 else { return }
        await loadPage()
    }

    /// Called when a row appears: near the end of what's fetched, fetch the next page.
    func rowAppeared(_ row: ResultRow) async {
        visible.insert(row.index)
        if hasMore, !isLoading, row.index >= Int64(rows.count - pageSize / 4) {
            await loadPage()
        }
    }

    func rowDisappeared(_ row: ResultRow) {
        visible.remove(row.index)
    }

    /// Fetches the next page of rows.
    func loadPage() async {
        guard !isLoading, scanNumber > 0 else { return }
        let offset = Int64(rows.count)
        let limit = rows.isEmpty ? pageSize : min(pageSize, shownLimit - rows.count)
        guard limit > 0 else { return }
        let wanted = scanNumber
        isLoading = true
        defer { isLoading = false }
        do {
            let page = try await client.scanResults(offset: offset, limit: limit, hex: hex)
            // A scan or undo while this page loaded: these rows belong to other results.
            guard wanted == scanNumber, page.scanNumber == scanNumber, Int64(rows.count) == page.offset else {
                if page.scanNumber != wanted { error = "The results changed while loading. Scan again or undo." }
                return
            }
            total = page.total
            type = page.type
            rows.append(contentsOf: page.results.map(ResultRow.init))
        } catch {
            self.error = error.localizedDescription
        }
    }

    // MARK: Current values

    /// The rows to read again: the ones on screen (one range, at most `maxRefreshRows`), or the first
    /// rows before the table has reported any.
    var refreshRange: Range<Int>? {
        guard !rows.isEmpty else { return nil }
        let onScreen = visible.compactMap { $0 < Int64(rows.count) ? Int($0) : nil }
        let first = onScreen.min() ?? 0
        let last = onScreen.max() ?? min(rows.count, 50) - 1
        let end = min(last + 1, first + maxRefreshRows, rows.count)
        return first < end ? first..<end : nil
    }

    /// Reads the current values of the rows on screen from the PS4 and puts them in the table.
    func refreshCurrentValues() async {
        guard let range = refreshRange else { return }
        let wanted = scanNumber
        do {
            let values = try await client.resultValues(
                ResultValuesRequest(offset: Int64(range.lowerBound), limit: range.count, hex: hex))
            guard values.scanNumber == wanted, wanted == scanNumber else { return }
            for value in values.values {
                let i = Int(value.index)
                if i < rows.count, rows[i].index == value.index, rows[i].current != value.value {
                    rows[i].current = value.value
                }
            }
        } catch {
            // Not shown: the next refresh tries again, and a lost connection shows in the connection bar.
        }
    }

    /// Starts reading the visible rows every `refreshInterval` while `shouldRefresh` is true
    /// (connected, and no scan running).
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

    // MARK: Cheat list

    /// Adds the rows to the game's cheat list, with the value as shown (current if read, else the
    /// scan's value). This only saves them on the Mac: nothing is written to the PS4.
    func addToCheatList(_ indexes: Set<Int64>, titleId: String, game: String?) async {
        let chosen = rows.filter { indexes.contains($0.index) }
        guard !chosen.isEmpty, let type else { return }
        error = nil
        notice = nil
        var added: [String] = []
        for row in chosen {
            do {
                // The value goes back as shown: hex values carry "0x", which the backend reads as hex.
                _ = try await client.addCheat(titleId: titleId, NewCheatRequest(
                    description: nil, address: row.address, type: type, value: row.latest, hex: false,
                    section: row.section, sectionStart: row.sectionStart, game: game))
                added.append(row.address)
            } catch {
                self.error = "Couldn't add \(row.address): \(error.localizedDescription)"
                break
            }
        }
        if !added.isEmpty {
            let what = added.count == 1 ? added[0] : "\(added.count) addresses"
            notice = "Added \(what) to the cheat list of \(titleId)"
        }
    }

    /// Forgets the rows (on disconnect).
    func clear() {
        scanNumber = 0
        rows = []
        total = 0
        type = nil
        visible = []
        error = nil
        notice = nil
    }
}
