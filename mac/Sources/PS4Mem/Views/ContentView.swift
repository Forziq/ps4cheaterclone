import SwiftUI

/// The main window: backend state, connection bar, and (in later steps) the process, scan and cheat panels.
struct ContentView: View {
    let model: AppModel
    @State private var showingInject = false

    var body: some View {
        VStack(spacing: 0) {
            BackendBanner(backend: model.backend)
            ConnectionBar(model: model, onInject: { showingInject = true })
            Divider()
            placeholder
        }
        .task { await model.startUp() }
    }

    /// Until the process picker exists (next task): a hint about what to do first.
    private var placeholder: some View {
        VStack(spacing: 8) {
            Image(systemName: model.status.connected ? "checkmark.circle" : "gamecontroller")
                .font(.system(size: 36))
                .foregroundStyle(.secondary)
            Text(model.status.connected
                 ? "Connected. The process picker comes next."
                 : "Start a game on the PS4, then Connect (or Inject payload first, once per PS4 session).")
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
