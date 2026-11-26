import SwiftUI

/// The scan controls, like PS4Cheater: value type (with signed/unsigned), compare type, the value
/// field(s) the compare needs, hex, alignment and pause options, the First / Next / Undo / New buttons,
/// and a progress bar with Cancel while a scan runs.
struct ScannerPanel: View {
    @Bindable var scan: ScanModel
    /// The chosen process, and the start addresses of the ticked sections (what a first scan sends).
    var pid: Int?
    var sections: [String]

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                Text("Scanner").font(.headline)
                if let pid = scan.status.pid, scan.hasResults, pid != self.pid {
                    Text("results are for pid \(pid): New scan to start over")
                        .font(.caption)
                        .foregroundStyle(.orange)
                }
                Spacer()
            }

            queryRow
            optionsRow

            if scan.isRunning {
                HStack(spacing: 8) {
                    ProgressView(value: scan.progressFraction)
                    Button("Cancel") { Task { await scan.cancel() } }
                }
            }

            HStack(spacing: 8) {
                Text(scan.statusText)
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .help(scan.status.query ?? "")
                Spacer()
            }
            if let message = scan.status.message, scan.status.state != "failed" {
                Text(message).font(.caption).foregroundStyle(.secondary).textSelection(.enabled)
            }
            if let error = scan.error {
                Text(error)
                    .font(.caption)
                    .foregroundStyle(.red)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .padding(12)
    }

    /// Type, signed, compare and the value field(s).
    private var queryRow: some View {
        HStack(spacing: 8) {
            Picker("Type", selection: $scan.kind) {
                ForEach(ValueKind.allCases) { Text($0.label).tag($0) }
            }
            .fixedSize()
            .disabled(scan.typeLocked)
            .help(scan.typeLocked ? "The type is fixed until New scan" : "What the value is stored as. Most game values are 4 bytes.")

            if scan.kind.isInteger {
                Toggle("Signed", isOn: $scan.signed)
                    .toggleStyle(.checkbox)
                    .disabled(scan.typeLocked)
                    .help("Signed values can be negative (i32); unsigned can't (u32)")
            }

            Picker("Compare", selection: $scan.compare) {
                ForEach(scan.availableCompares) { Text($0.label).tag($0) }
            }
            .fixedSize()
            .disabled(scan.isRunning)

            let prompts = scan.compare.valuePrompts
            if prompts.count >= 1 {
                TextField(prompts[0], text: $scan.value)
                    .textFieldStyle(.roundedBorder)
                    .font(.body.monospaced())
                    .frame(minWidth: 100, maxWidth: 220)
                    .onSubmit(scanOnReturn)
            }
            if prompts.count >= 2 {
                Text("and").foregroundStyle(.secondary)
                TextField(prompts[1], text: $scan.value2)
                    .textFieldStyle(.roundedBorder)
                    .font(.body.monospaced())
                    .frame(minWidth: 100, maxWidth: 220)
                    .onSubmit(scanOnReturn)
            }
            if prompts.isEmpty {
                Text(scan.compare == .unknownInitial ? "Saves every value now; narrow it down with next scans."
                                                     : "No value needed: compares with the previous scan.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer(minLength: 0)
        }
    }

    /// Hex, alignment, pause, and the buttons.
    private var optionsRow: some View {
        HStack(spacing: 12) {
            Toggle("Hex", isOn: $scan.hex)
                .toggleStyle(.checkbox)
                .disabled(!scan.hexApplies)
                .help(scan.kind == .bytes ? "Byte arrays are always hex, e.g. 01 FF ?? 3C"
                                          : "Type numbers in hex (and show results in hex)")
            Toggle("Aligned", isOn: $scan.aligned)
                .toggleStyle(.checkbox)
                .disabled(scan.typeLocked)
                .help("Only look at addresses that are a multiple of 4 (less for 1- and 2-byte values). Faster, and finds almost every game value.")
            Toggle("Pause game while scanning", isOn: $scan.pauseGame)
                .toggleStyle(.checkbox)
                .disabled(scan.isRunning)
                .help("Stops the game during the scan so values can't change half-way. The game is always resumed.")

            Spacer()

            Button("First scan") { Task { await scan.firstScan(pid: pid ?? 0, sections: sections) } }
                .disabled(!scan.canFirstScan(pid: pid, sectionCount: sections.count))
                .help(sections.isEmpty ? "Tick at least one section first" : "Scan the ticked sections")
            Button("Next scan") { Task { await scan.nextScan() } }
                .disabled(!scan.canNextScan)
                .help("Keep only the results that match")
            Button("Undo") { Task { await scan.undo() } }
                .disabled(!scan.canUndo)
                .help("Back to the results before the last next scan")
            Button("New scan") { Task { await scan.newScan() } }
                .disabled(!scan.canNewScan)
                .help("Drop every result and start over")
        }
    }

    /// Return in a value field runs the next scan when there are results, else the first scan.
    private func scanOnReturn() {
        if scan.canNextScan {
            Task { await scan.nextScan() }
        } else if scan.canFirstScan(pid: pid, sectionCount: sections.count), let pid {
            Task { await scan.firstScan(pid: pid, sections: sections) }
        }
    }
}
