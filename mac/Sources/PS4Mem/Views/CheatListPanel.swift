import SwiftUI
import AppKit
import UniformTypeIdentifiers

/// The chosen game's cheat list, like PS4Cheater: lock, description, address, section + offset, type,
/// value and the value in memory now, and delete.
///
/// Editing: Return in the description field saves it. Return in the value field saves the value
/// **and writes it to the PS4**; leaving the field (or Esc) without Return puts the old value back.
/// The lock box writes the value now and every ~200 ms until it's unticked.
/// The list is saved on the Mac after every change; Import and Export read and write cheat files.
struct CheatListPanel: View {
    let cheats: CheatListModel

    @State private var selection: Set<String> = []
    /// The cheat waiting for "Delete?" to be confirmed.
    @State private var deleting: Cheat?
    /// A chosen file waiting for "merge or replace?".
    @State private var importing: PendingImport?

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                Text(cheats.headerText).font(.headline)
                if cheats.isLoading { ProgressView().controlSize(.small) }
                Spacer()
                Button("Reload") { Task { await cheats.reload() } }
                    .disabled(cheats.titleId == nil || cheats.isLoading)
                    .help("Read the saved list again")
                Button("Import…") { chooseImportFile() }
                    .disabled(cheats.titleId == nil)
                    .help("Add cheats from a cheat file (.json) to this game's list")
                Button("Export…") { Task { await export() } }
                    .disabled(cheats.titleId == nil || cheats.cheats.isEmpty)
                    .help("Save this game's list as a cheat file (.json)")
            }

            Table(cheats.cheats, selection: $selection) {
                TableColumn("Lock") { cheat in
                    LockBox(cheat: cheat, cheats: cheats)
                }
                .width(44)
                TableColumn("Description") { cheat in
                    EditField(text: cheat.description, placeholder: "Describe it, then Return") { text in
                        Task { await cheats.setDescription(cheat.id, text) }
                    }
                }
                .width(min: 120, ideal: 200)
                TableColumn("Address") { cheat in
                    Text(cheat.address).font(.body.monospaced())
                }
                .width(min: 110, ideal: 140)
                TableColumn("Section") { cheat in
                    Text(sectionText(cheat))
                        .lineLimit(1)
                        .help(cheat.sectionStart.map { "Section start \($0)" } ?? "Section not known")
                }
                .width(min: 100, ideal: 180)
                TableColumn("Type") { cheat in
                    Text(cheat.type).font(.body.monospaced())
                }
                .width(min: 40, ideal: 50)
                TableColumn("Value") { cheat in
                    EditField(text: cheat.value, placeholder: "Value", monospaced: true) { text in
                        Task { await cheats.writeValue(cheat.id, text) }
                    }
                    .help("Type a value and press Return to write it to the PS4 (0x… is hex)")
                }
                .width(min: 80, ideal: 120)
                TableColumn("In memory") { cheat in
                    InMemoryText(now: cheats.current[cheat.id], saved: cheat.value)
                }
                .width(min: 80, ideal: 120)
                TableColumn("") { cheat in
                    Button {
                        deleting = cheat
                    } label: {
                        Image(systemName: "trash")
                    }
                    .buttonStyle(.borderless)
                    .help("Delete from the list")
                }
                .width(28)
            }
            .contextMenu(forSelectionType: String.self) { ids in
                if ids.count == 1, let id = ids.first, let cheat = cheats.cheats.first(where: { $0.id == id }) {
                    Button("Delete \(cheat.address)") { deleting = cheat }
                }
            }

            footer
        }
        .confirmationDialog("Delete this cheat?", isPresented: deletingBinding, presenting: deleting) { cheat in
            Button("Delete \(cheat.address)", role: .destructive) {
                Task { await cheats.delete(cheat.id) }
            }
        } message: { cheat in
            Text(cheat.locked ? "Its lock stops too." : "It goes from the saved list.")
        }
        .confirmationDialog("Import \(importing?.name ?? "the file")?", isPresented: importingBinding,
                            presenting: importing) { file in
            Button("Add to the list") { Task { await cheats.importFile(file.data, mode: .merge) } }
            Button("Replace the whole list", role: .destructive) {
                Task { await cheats.importFile(file.data, mode: .replace) }
            }
        } message: { _ in
            Text("Add keeps the saved cheats (a cheat with the same id is replaced). Replace removes "
                 + "the cheats that aren't in the file. Nothing is written to the PS4.")
        }
        // Another game: the old selection points at other rows.
        .onChange(of: cheats.titleId) { selection = [] }
    }

    private var deletingBinding: Binding<Bool> {
        Binding(get: { deleting != nil }, set: { if !$0 { deleting = nil } })
    }

    private var importingBinding: Binding<Bool> {
        Binding(get: { importing != nil }, set: { if !$0 { importing = nil } })
    }

    /// Asks for a cheat file, reads it, then asks merge or replace.
    private func chooseImportFile() {
        let panel = NSOpenPanel()
        panel.title = "Import a cheat file into \(cheats.titleId ?? "this game")'s list"
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        panel.allowedContentTypes = [.json]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            importing = PendingImport(name: url.lastPathComponent, data: try Data(contentsOf: url))
        } catch {
            cheats.showError("Couldn't read \(url.lastPathComponent): \(error.localizedDescription)")
        }
    }

    /// Fetches the list as a cheat file and saves it where the user chooses (TITLEID.json by default).
    private func export() async {
        guard let titleId = cheats.titleId, let data = await cheats.exportFile() else { return }
        let panel = NSSavePanel()
        panel.title = "Export \(titleId)'s cheat list"
        panel.nameFieldStringValue = "\(titleId).json"
        panel.allowedContentTypes = [.json]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try data.write(to: url, options: .atomic)
            cheats.exported(to: url.path)
        } catch {
            cheats.showError("Couldn't save \(url.lastPathComponent): \(error.localizedDescription)")
        }
    }

    private func sectionText(_ cheat: Cheat) -> String {
        switch (cheat.section, cheat.sectionOffset) {
        case let (name?, offset?): return "\(name) + \(offset)"
        case let (name?, nil): return name
        default: return "–"
        }
    }

    @ViewBuilder
    private var footer: some View {
        HStack(spacing: 8) {
            if let notice = cheats.notice {
                Text(notice).font(.caption).foregroundStyle(.green)
            } else if cheats.titleId != nil && cheats.cheats.isEmpty && !cheats.isLoading {
                Text("No cheats yet. Double-click a scan result to add one.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            if let file = cheats.file {
                Text("Saved automatically to \(file)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .truncationMode(.middle)
                    .textSelection(.enabled)
            }
        }
        if let error = cheats.error {
            Text(error).font(.caption).foregroundStyle(.red).textSelection(.enabled)
        }
        if let error = cheats.valuesError {
            Text(error).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
        }
    }
}

/// A cheat file the user chose to import, read into memory.
private struct PendingImport {
    let name: String
    let data: Data
}

/// The value in memory now: orange when it differs from the cheat's value, a dash until read.
private struct InMemoryText: View {
    let now: String?
    let saved: String

    private var color: Color {
        guard let now else { return .secondary }
        return now == saved ? .primary : .orange
    }

    var body: some View {
        Text(now ?? "–")
            .font(.body.monospaced())
            .foregroundStyle(color)
            .help(now == nil ? "Not read yet" : "The value in memory now (read every second)")
    }
}

/// The lock box. Ticking it writes memory (now and every ~200 ms); a lock that stopped by itself
/// shows a warning with the reason.
private struct LockBox: View {
    let cheat: Cheat
    let cheats: CheatListModel

    var body: some View {
        HStack(spacing: 2) {
            Toggle("", isOn: Binding(
                get: { cheat.locked },
                set: { on in Task { await cheats.setLocked(cheat.id, on) } }))
                .toggleStyle(.checkbox)
                .labelsHidden()
                .help(cheat.locked ? "Locked: written every ~200 ms (\(cheat.lockWrites ?? 0) writes)"
                                   : "Lock: keep writing this value")
            if let lockError = cheat.lockError, !cheat.locked {
                Image(systemName: "exclamationmark.triangle.fill")
                    .foregroundStyle(.orange)
                    .help("The lock stopped: \(lockError)")
            }
        }
    }
}

/// A text field that hands its text over only on Return. Leaving it or pressing Esc puts the saved
/// text back, so nothing half-typed is kept or written.
private struct EditField: View {
    /// The saved text.
    let text: String
    var placeholder: String
    var monospaced = false
    let onSubmit: (String) -> Void

    @State private var draft = ""
    @FocusState private var focused: Bool

    var body: some View {
        TextField(placeholder, text: $draft)
            .textFieldStyle(.plain)
            .font(monospaced ? .body.monospaced() : .body)
            .focused($focused)
            .onAppear { draft = text }
            // The saved text only changes after an edit of ours (the backend may tidy it, e.g. 0x10 → 16).
            .onChange(of: text) { draft = text }
            .onChange(of: focused) { if !focused { draft = text } }
            .onSubmit { onSubmit(draft) }
            .onExitCommand { draft = text }
    }
}
