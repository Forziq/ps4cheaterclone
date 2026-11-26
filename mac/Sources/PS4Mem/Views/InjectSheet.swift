import AppKit
import SwiftUI
import UniformTypeIdentifiers

/// The "Inject payload" sheet: pick the ps4debug .bin (remembered), pick the loader port, inject.
/// The backend sends the file, waits up to 20 s for ps4debug on port 744, then connects.
struct InjectSheet: View {
    let model: AppModel
    @Bindable var inject: InjectModel
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Inject the ps4debug payload")
                .font(.headline)
            Text("Sends the payload to GoldHEN's BinLoader on \(ipDescription), then connects. " +
                 "Once per PS4 session: BinLoader takes one payload, then refuses until you toggle it " +
                 "in GoldHEN or restart the PS4.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)

            Grid(alignment: .leading, horizontalSpacing: 10, verticalSpacing: 10) {
                GridRow {
                    Text("Payload")
                    HStack {
                        TextField("ps4debug .bin", text: $inject.payloadPath)
                            .textFieldStyle(.roundedBorder)
                            .truncationMode(.head)
                        Button("Choose…", action: choosePayload)
                    }
                }
                GridRow {
                    Text("Loader port")
                    Picker("Loader port", selection: Binding(
                        get: { inject.loaderPort },
                        set: { port in Task { await savePort(port) } })) {
                        ForEach(inject.loaderPortOptions, id: \.self) { port in
                            Text(port == 9090 ? "9090 (GoldHEN BinLoader)" : String(port)).tag(port)
                        }
                    }
                    .labelsHidden()
                    .fixedSize()
                }
            }
            .disabled(inject.isInjecting)

            if inject.isInjecting {
                HStack(spacing: 8) {
                    ProgressView().controlSize(.small)
                    Text(inject.progressText)
                        .monospacedDigit()
                }
            }

            if let error = inject.error {
                Text(error)
                    .foregroundStyle(.red)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
            }

            HStack {
                Spacer()
                Button("Cancel") { dismiss() }
                    .keyboardShortcut(.cancelAction)
                    .disabled(inject.isInjecting)
                Button("Inject") { Task { await runInject() } }
                    .keyboardShortcut(.defaultAction)
                    .disabled(inject.isInjecting || inject.payloadPath.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .padding(20)
        .frame(width: 520)
        .onAppear { inject.load(from: model.settings) }
    }

    /// The IP the backend will use: the typed one, or its own default.
    private var ipDescription: String {
        let typed = model.ipText.trimmingCharacters(in: .whitespaces)
        return typed.isEmpty ? "the default IP" : typed
    }

    private func choosePayload() {
        let panel = NSOpenPanel()
        panel.title = "Choose the ps4debug payload"
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = false
        if let bin = UTType(filenameExtension: "bin") { panel.allowedContentTypes = [bin, .data] }
        if let current = inject.payloadURL() {
            panel.directoryURL = current.deletingLastPathComponent()
            panel.nameFieldStringValue = current.lastPathComponent
        }
        guard panel.runModal() == .OK, let url = panel.url else { return }
        Task {
            if let saved = await inject.choosePayload(url) { model.settingsChanged(saved) }
        }
    }

    private func savePort(_ port: Int) async {
        if let saved = await inject.chooseLoaderPort(port) { model.settingsChanged(saved) }
    }

    private func runInject() async {
        if await model.injectPayload() { dismiss() }
    }
}
