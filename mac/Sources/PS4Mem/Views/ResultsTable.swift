import SwiftUI

/// The scan results: address, current value (read again every second for the rows on screen),
/// previous value (what the scan found) and section. Rows are fetched a page at a time as the table
/// scrolls, up to the first 5,000. Double-click a row (or right-click) to add it to the cheat list.
struct ResultsTable: View {
    let results: ResultsModel
    /// The game the cheat list belongs to, or nil when the results aren't for the chosen process.
    var titleId: String?
    var game: String?

    @State private var selection: Set<Int64> = []

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Table(results.rows, selection: $selection) {
                TableColumn("Address") { row in
                    Text(row.address)
                        .font(.body.monospaced())
                        .onAppear { Task { await results.rowAppeared(row) } }
                        .onDisappear { results.rowDisappeared(row) }
                }
                .width(min: 110, ideal: 140)
                TableColumn("Value") { row in
                    Text(row.latest)
                        .font(.body.monospaced())
                        .foregroundStyle(row.changed ? .red : .primary)
                        .help(row.changed ? "Changed since the scan (was \(row.previous))" : "Value in memory now")
                }
                .width(min: 80, ideal: 140)
                TableColumn("Previous") { row in
                    Text(row.previous)
                        .font(.body.monospaced())
                        .foregroundStyle(.secondary)
                }
                .width(min: 80, ideal: 140)
                TableColumn("Section") { row in
                    Text("\(row.section) + \(row.sectionOffset)")
                        .lineLimit(1)
                        .help("Section start \(row.sectionStart)")
                }
                .width(min: 120, ideal: 220)
            }
            .contextMenu(forSelectionType: Int64.self) { indexes in
                Button(indexes.count > 1 ? "Add \(indexes.count) to cheat list" : "Add to cheat list") {
                    add(indexes)
                }
                .disabled(titleId == nil || indexes.isEmpty)
            } primaryAction: { indexes in
                // Double-click (or Return) on a row.
                add(indexes)
            }

            HStack(spacing: 8) {
                Text(results.countText)
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
                if results.isLoading { ProgressView().controlSize(.small) }
                Spacer()
                if let notice = results.notice {
                    Text(notice).font(.caption).foregroundStyle(.green)
                } else if !results.rows.isEmpty {
                    Text(titleId == nil ? "Choose the process these results are for to add cheats"
                                        : "Double-click a row to add it to the cheat list")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            if let error = results.error {
                Text(error)
                    .font(.caption)
                    .foregroundStyle(.red)
                    .textSelection(.enabled)
            }
        }
        // New results: the old selection points at other rows.
        .onChange(of: results.scanNumber) { selection = [] }
    }

    private func add(_ indexes: Set<Int64>) {
        guard let titleId, !indexes.isEmpty else { return }
        Task { await results.addToCheatList(indexes, titleId: titleId, game: game) }
    }
}
