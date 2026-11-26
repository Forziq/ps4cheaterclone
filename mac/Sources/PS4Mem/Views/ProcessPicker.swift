import SwiftUI

/// Every process on the PS4. The likely game is pre-highlighted with the reason beside its name,
/// but nothing is chosen until the user presses "Use this process" or double-clicks a row.
struct ProcessPicker: View {
    @Bindable var processes: ProcessModel
    /// Called after the user chose a process (the sections panel then loads its maps).
    var onChoose: (Int) async -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Process").font(.headline)
                Spacer()
                if processes.isLoading { ProgressView().controlSize(.small) }
                Button {
                    Task { await processes.load() }
                } label: {
                    Image(systemName: "arrow.clockwise")
                }
                .help("Fetch the process list again (after starting or closing a game)")
                .disabled(processes.isLoading)
            }

            if !processes.suggestionText.isEmpty {
                Text(processes.suggestionText)
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }

            List(processes.processes, selection: $processes.highlightedPid) { entry in
                row(entry)
                    .tag(entry.pid)
            }
            .listStyle(.bordered(alternatesRowBackgrounds: true))
            .contextMenu(forSelectionType: Int.self) { _ in
            } primaryAction: { pids in
                // Double-click (or Return) chooses the row.
                if let pid = pids.first { Task { await choose(pid) } }
            }

            HStack {
                Button("Use this process") {
                    if let pid = processes.highlightedPid { Task { await choose(pid) } }
                }
                .disabled(!processes.canChooseHighlighted)
                Spacer()
            }

            chosenLabel

            if let error = processes.error {
                Text(error)
                    .font(.caption)
                    .foregroundStyle(.red)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(12)
    }

    private func row(_ entry: ProcessEntry) -> some View {
        HStack(spacing: 6) {
            if entry.pid == processes.chosen?.pid {
                Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
                    .help("The chosen process")
            }
            VStack(alignment: .leading, spacing: 1) {
                Text(entry.name).lineLimit(1)
                if entry.suggested {
                    Text(ProcessModel.suggestionLabel(entry.suggestionReason))
                        .font(.caption)
                        .foregroundStyle(.orange)
                        .lineLimit(2)
                }
            }
            Spacer(minLength: 4)
            Text("\(entry.pid)")
                .font(.caption.monospacedDigit())
                .foregroundStyle(.secondary)
        }
    }

    private var chosenLabel: some View {
        HStack(spacing: 6) {
            Image(systemName: processes.chosen == nil ? "circle.dashed" : "gamecontroller.fill")
                .foregroundStyle(processes.chosen == nil ? Color.secondary : Color.accentColor)
            Text(processes.chosenText)
                .font(.callout)
                .textSelection(.enabled)
                .lineLimit(2)
        }
    }

    private func choose(_ pid: Int) async {
        if await processes.choose(pid: pid) { await onChoose(pid) }
    }
}
