import SwiftUI

/// The main window: the backend state for now; the connection bar and the panels follow.
struct ContentView: View {
    let model: AppModel

    var body: some View {
        VStack(spacing: 0) {
            BackendBanner(backend: model.backend)
            Spacer()
        }
        .task { await model.startUp() }
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
