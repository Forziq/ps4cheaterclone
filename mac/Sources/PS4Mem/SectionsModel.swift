import Foundation
import Observation

/// The sections panel's state: the chosen process's readable maps, which ones are ticked for scanning,
/// and the name filter.
///
/// The backend picks the default selection (writable, and unnamed or a name containing a filter key,
/// e.g. "anon", "exe"). The filter and that selection are only suggestions: the user can tick or untick
/// any section. Ticks are kept here in the app; the scanner (next task) sends the ticked start addresses.
@MainActor
@Observable
final class SectionsModel {
    let client: APIClient

    /// The process whose sections are listed. Nil until a process is chosen.
    private(set) var pid: Int?
    /// Every readable section, by start address (the backend's order).
    private(set) var sections: [SectionEntry] = []
    /// Start addresses of the ticked sections.
    private(set) var selected: Set<String> = []
    /// What's in the filter field: keys separated by commas. Empty = any name.
    var filterText: String = ""
    /// Also tick read-only sections when the filter is applied.
    var includeReadOnly = false
    /// The backend's default filter keys, for "Reset filter".
    private(set) var defaultFilter: [String] = []
    /// Maps the backend left out (end <= start, too big, duplicates, unreadable).
    private(set) var skippedMaps = 0
    private(set) var isLoading = false
    /// The backend's error text from the last load, shown as it is.
    private(set) var error: String?

    init(client: APIClient) {
        self.client = client
    }

    // MARK: Counts

    var totalCount: Int { sections.count }
    var selectedCount: Int { sections.filter { selected.contains($0.start) }.count }
    var totalBytes: UInt64 { sections.reduce(0) { $0 + $1.size } }
    var selectedBytes: UInt64 { sections.filter { selected.contains($0.start) }.reduce(0) { $0 + $1.size } }

    /// "selected / total", like PS4Cheater, with the sizes: "3 / 120 selected · 2.9 GB of 3.1 GB".
    var countText: String {
        "\(selectedCount) / \(totalCount) selected · \(Self.formatBytes(selectedBytes)) of \(Self.formatBytes(totalBytes))"
    }

    /// The ticked sections' start addresses, in list order (what a scan sends).
    var selectedStarts: [String] { sections.map(\.start).filter { selected.contains($0) } }

    /// True when the field holds the default filter (so "Reset filter" has nothing to do).
    var filterIsDefault: Bool { Self.parseFilter(filterText) == defaultFilter }

    // MARK: Loading

    /// Loads a process's sections with the backend's default filter and selection.
    func load(pid: Int) async {
        self.pid = pid
        filterText = ""
        includeReadOnly = false
        await fetch(filter: nil)
    }

    /// Fetches the list again with the filter field and the read-only toggle. The ticks are replaced
    /// by the backend's selection for that filter (an empty field selects every name).
    func applyFilter() async {
        await fetch(filter: Self.parseFilter(filterText))
    }

    /// Puts the default filter back in the field and applies it.
    func resetFilter() async {
        filterText = defaultFilter.joined(separator: ", ")
        includeReadOnly = false
        await applyFilter()
    }

    private func fetch(filter: [String]?) async {
        guard let pid else { return }
        isLoading = true
        error = nil
        defer { isLoading = false }
        do {
            let list = try await client.sections(pid: pid, filter: filter, includeReadOnly: includeReadOnly)
            // A different process may have been chosen while this one loaded.
            guard self.pid == pid else { return }
            sections = list.sections
            selected = Set(list.sections.filter(\.selected).map(\.start))
            skippedMaps = list.skippedMaps
            if filter == nil {
                defaultFilter = list.nameFilter
                filterText = list.nameFilter.joined(separator: ", ")
            }
        } catch {
            self.error = error.localizedDescription
        }
    }

    // MARK: Ticks

    func isSelected(_ section: SectionEntry) -> Bool { selected.contains(section.start) }

    func setSelected(_ section: SectionEntry, _ on: Bool) {
        if on { selected.insert(section.start) } else { selected.remove(section.start) }
    }

    func selectAll() { selected = Set(sections.map(\.start)) }

    func selectNone() { selected = [] }

    /// Forgets everything (on disconnect, or when no process is chosen).
    func clear() {
        pid = nil
        sections = []
        selected = []
        filterText = ""
        includeReadOnly = false
        defaultFilter = []
        skippedMaps = 0
        error = nil
    }

    // MARK: Helpers

    /// "anon, exe;Game" → ["anon", "exe", "Game"]. Commas or semicolons, spaces trimmed, empty parts dropped.
    static func parseFilter(_ text: String) -> [String] {
        text.split(whereSeparator: { $0 == "," || $0 == ";" })
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
    }

    /// 1024-based sizes ("2.9 GB"), as memory sizes usually are.
    static func formatBytes(_ bytes: UInt64) -> String {
        ByteCountFormatter.string(fromByteCount: Int64(clamping: bytes), countStyle: .memory)
    }
}
