import SwiftUI

/// The chosen process's sections, like PS4Cheater: a table (tick, name, start, end, size, prot) with the
/// name filter (defaults pre-filled), select all / none, and the "selected / total" count.
/// The default ticks are a suggestion; the user can tick any section.
struct SectionsPanel: View {
    @Bindable var sections: SectionsModel
    /// One line about the chosen process (name, title ID, pid).
    var processText: String

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Sections").font(.headline)
                Text(processText)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .textSelection(.enabled)
                Spacer()
                if sections.isLoading { ProgressView().controlSize(.small) }
            }

            filterBar

            Table(sections.sections) {
                TableColumn("") { section in
                    Toggle("", isOn: Binding(
                        get: { sections.isSelected(section) },
                        set: { sections.setSelected(section, $0) }))
                        .labelsHidden()
                        .toggleStyle(.checkbox)
                        .help(section.reason)
                }
                .width(22)
                TableColumn("Name") { section in
                    Text(section.displayName)
                        .foregroundStyle(section.name.isEmpty ? .secondary : .primary)
                        .help(section.reason)
                }
                .width(min: 120, ideal: 220)
                TableColumn("Start") { section in
                    Text(section.start).font(.body.monospaced()).textSelection(.enabled)
                }
                .width(min: 110, ideal: 130)
                TableColumn("End") { section in
                    Text(section.end).font(.body.monospaced()).textSelection(.enabled)
                }
                .width(min: 110, ideal: 130)
                TableColumn("Size") { section in
                    Text(SectionsModel.formatBytes(section.size))
                        .monospacedDigit()
                        .frame(maxWidth: .infinity, alignment: .trailing)
                        .help("\(section.size) bytes")
                }
                .width(min: 70, ideal: 80)
                TableColumn("Prot") { section in
                    Text(section.protText).font(.body.monospaced())
                        .help(String(format: "0x%X", section.prot))
                }
                .width(min: 40, ideal: 50)
            }

            HStack(spacing: 8) {
                Button("Select all") { sections.selectAll() }
                    .disabled(sections.sections.isEmpty)
                Button("Select none") { sections.selectNone() }
                    .disabled(sections.selected.isEmpty)
                Spacer()
                if sections.skippedMaps > 0 {
                    Text("\(sections.skippedMaps) maps left out")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                        .help("Unreadable, empty (end <= start), too big, or the same range as the map before. See the debug log (category maps).")
                }
                Text(sections.countText)
                    .monospacedDigit()
            }

            if let error = sections.error {
                Text(error)
                    .font(.caption)
                    .foregroundStyle(.red)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(12)
    }

    private var filterBar: some View {
        HStack(spacing: 8) {
            Text("Name filter").foregroundStyle(.secondary)
            TextField("empty = any name", text: $sections.filterText)
                .textFieldStyle(.roundedBorder)
                .onSubmit { Task { await sections.applyFilter() } }
                .help("Ticks writable sections whose name contains any of these (commas between), plus unnamed ones. Applying it replaces your ticks.")
            Toggle("Read-only too", isOn: $sections.includeReadOnly)
                .toggleStyle(.checkbox)
                .help("Also tick read-only sections when the filter is applied")
            Button("Apply") { Task { await sections.applyFilter() } }
                .disabled(sections.isLoading || sections.pid == nil)
            Button("Reset") { Task { await sections.resetFilter() } }
                .disabled(sections.isLoading || sections.pid == nil || (sections.filterIsDefault && !sections.includeReadOnly))
                .help("Back to the default filter")
        }
    }
}
