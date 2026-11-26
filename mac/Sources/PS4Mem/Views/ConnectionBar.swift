import SwiftUI

/// The strip at the top of the window: IP field with recent IPs, Connect/Disconnect, Inject payload,
/// and the connection status (ps4debug version) or the last error.
struct ConnectionBar: View {
    @Bindable var model: AppModel
    /// Opens the inject sheet (the sheet itself lives in ContentView).
    var onInject: () -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                Text("PS4 IP")
                    .foregroundStyle(.secondary)

                TextField("e.g. 192.0.2.10", text: $model.ipText)
                    .textFieldStyle(.roundedBorder)
                    .frame(width: 160)
                    .disabled(model.status.connected || model.isBusy)
                    .onSubmit { Task { await model.connect() } }

                recentIpsMenu

                if model.status.connected {
                    Button("Disconnect") { Task { await model.disconnect() } }
                        .disabled(model.isBusy)
                } else {
                    Button("Connect") { Task { await model.connect() } }
                        .keyboardShortcut(.defaultAction)
                        .disabled(model.isBusy || !model.backend.isRunning)
                    Button("Inject payload…", action: onInject)
                        .disabled(model.isBusy || !model.backend.isRunning)
                        .help("Send the ps4debug payload to GoldHEN's BinLoader, then connect")
                }

                if model.isBusy { ProgressView().controlSize(.small) }

                Spacer()

                statusLabel
            }

            if let error = model.actionError ?? (model.status.connected ? nil : model.status.lastError) {
                Text(error)
                    .font(.callout)
                    .foregroundStyle(.red)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
    }

    /// A small menu of recently used IPs; picking one fills the field.
    private var recentIpsMenu: some View {
        Menu {
            if model.recentIps.isEmpty {
                Text("No recent IPs yet")
            }
            ForEach(model.recentIps, id: \.self) { ip in
                Button(ip) { model.ipText = ip }
            }
        } label: {
            Image(systemName: "clock.arrow.circlepath")
        }
        .menuStyle(.borderlessButton)
        .fixedSize()
        .help("Recent IPs")
        .disabled(model.status.connected || model.isBusy)
    }

    private var statusLabel: some View {
        HStack(spacing: 6) {
            Circle()
                .fill(model.status.connected ? Color.green : Color.secondary.opacity(0.5))
                .frame(width: 8, height: 8)
            Text(model.statusText)
                .foregroundStyle(model.status.connected ? .primary : .secondary)
                .textSelection(.enabled)
        }
    }
}
