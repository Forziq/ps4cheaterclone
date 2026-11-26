import SwiftUI

/// The main window: backend state, connection bar, process picker, sections panel and scanner, and
/// (in a later step) the cheat list.
struct ContentView: View {
    let model: AppModel
    @State private var showingInject = false

    var body: some View {
        VStack(spacing: 0) {
            BackendBanner(backend: model.backend)
            ConnectionBar(model: model, onInject: { showingInject = true })
            Divider()
            if model.status.connected {
                HSplitView {
                    ProcessPicker(processes: model.processes, onChoose: { _ in
                        await model.syncSectionsWithChosenProcess()
                    })
                        .frame(minWidth: 240, idealWidth: 300, maxWidth: 420)
                    sectionsArea
                        .frame(minWidth: 420, maxWidth: .infinity, maxHeight: .infinity)
                }
                // A refresh can drop the chosen process (the game was closed): clear or reload the sections.
                .onChange(of: model.processes.chosen?.pid) {
                    Task { await model.syncSectionsWithChosenProcess() }
                }
            } else {
                placeholder
            }
        }
        .task { await model.startUp() }
        .sheet(isPresented: $showingInject) {
            InjectSheet(model: model, inject: model.inject)
        }
    }

    /// Right of the picker: the chosen process's sections, or a hint to choose one.
    @ViewBuilder
    private var sectionsArea: some View {
        if model.processes.chosen == nil {
            Text("Choose the game's process on the left.")
                .foregroundStyle(.secondary)
                .padding()
        } else {
            VSplitView {
                SectionsPanel(sections: model.sections, processText: model.processes.chosenText)
                    .frame(minHeight: 180, idealHeight: 260)
                VStack(alignment: .leading, spacing: 0) {
                    ScannerPanel(scan: model.scan, pid: model.processes.chosen?.pid,
                                 sections: model.sections.selectedStarts)
                    ResultsTable(results: model.results, titleId: model.cheatTitleId,
                                 game: model.processes.chosen?.name)
                        .padding([.horizontal, .bottom], 12)
                }
                .frame(minHeight: 300, maxHeight: .infinity, alignment: .top)
                // The hex box also switches the results to hex (the backend formats the values).
                .onChange(of: model.scan.hex) { Task { await model.results.setHex(model.scan.hex) } }
            }
        }
    }

    /// While not connected: a hint about what to do first.
    private var placeholder: some View {
        VStack(spacing: 8) {
            Image(systemName: "gamecontroller")
                .font(.system(size: 36))
                .foregroundStyle(.secondary)
            Text("Start a game on the PS4, then Connect (or Inject payload first, once per PS4 session).")
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding()
    }
}

/// A thin strip that only shows while the backend isn't ready (or failed).
struct BackendBanner: View {
    let backend: BackendProcess

    var body: some View {
        switch backend.state {
        case .running:
            EmptyView()
        case .idle, .checking:
            banner(Text("Looking for the backend…"), busy: true)
        case .starting:
            banner(Text("Starting the backend (the first build can take a minute)…"), busy: true)
        case .failed(let message):
            banner(Text(message).foregroundStyle(.red), busy: false) {
                Button("Retry") { Task { await backend.start() } }
            }
        }
    }

    private func banner(_ text: Text, busy: Bool,
                        @ViewBuilder trailing: () -> some View = { EmptyView() }) -> some View {
        HStack(spacing: 8) {
            if busy { ProgressView().controlSize(.small) }
            text.textSelection(.enabled)
            Spacer()
            trailing()
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(.bar)
    }
}
