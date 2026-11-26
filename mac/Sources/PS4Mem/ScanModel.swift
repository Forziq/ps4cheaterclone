import Foundation
import Observation

/// The scanner's state: what to look for (type, compare, values, options), the running scan's
/// progress, and the First / Next / New / Undo / Cancel actions.
///
/// Scans run in the backend. A button sends the scan and gets the status back at once; the model then
/// follows the progress stream (`GET /api/scan/progress`) until the "done" event. If the stream breaks,
/// it asks for the status once a second instead, so a long scan is never cut short by the app.
/// Scans only read memory.
@MainActor
@Observable
final class ScanModel {
    let client: APIClient

    // MARK: What to scan for

    /// Default 4 bytes: most game values (money, ammo) are 32-bit integers.
    var kind: ValueKind = .byte4 { didSet { fixCompare() } }
    var signed = false
    var compare: CompareKind = .exact
    var value = ""
    /// The second value of "between".
    var value2 = ""
    /// Typed numbers are hex (and results show in hex).
    var hex = false
    /// Only look at aligned addresses (4 bytes, less for small types). First scan only.
    var aligned = true
    /// Where the first scan compares: on the Mac (default) or on the PS4. Next scans always run on the Mac.
    var method: ScanMethodKind = .local
    /// Stop the game while scanning, so values don't change mid-scan. It is always resumed.
    var pauseGame = false

    // MARK: State

    /// The last status from the backend (the running scan, or the current results).
    private(set) var status: ScanStatus = .idle
    /// True while a button's request is waiting for the backend.
    private(set) var isBusy = false
    /// True while the progress of a scan is being followed.
    private(set) var isFollowing = false
    /// The error from the last action, the backend's text shown as it is.
    private(set) var error: String?

    /// Called after the results changed (a scan finished, undo, new scan), with the new status.
    /// The results table uses it to load its first page.
    var onResultsChanged: ((ScanStatus) async -> Void)?

    /// How long to wait between status checks when the progress stream isn't working.
    private let pollInterval: TimeInterval
    /// How many status checks in a row may fail before the app gives up following a scan.
    private let maxPollFailures = 10

    init(client: APIClient, pollInterval: TimeInterval = 1) {
        self.client = client
        self.pollInterval = pollInterval
    }

    // MARK: What the view shows

    var isRunning: Bool { status.isRunning }
    var hasResults: Bool { status.hasResults }

    /// After the first scan the type is fixed until New scan.
    var typeLocked: Bool { hasResults || isRunning }

    /// The compares to offer now: first-scan ones before any results, next-scan ones after.
    var availableCompares: [CompareKind] { CompareKind.available(for: kind, nextScan: hasResults) }

    /// The type name sent to the backend.
    var typeName: String { kind.typeName(signed: signed) }

    /// Hex input only makes sense for numbers (byte arrays are always hex, strings never).
    var hexApplies: Bool { kind.isNumber }

    /// True when every value field the compare needs is filled in.
    var valuesFilled: Bool {
        let filled = [value, value2].map { !$0.trimmingCharacters(in: .whitespaces).isEmpty }
        return filled.prefix(compare.valueCount).allSatisfy { $0 }
    }

    func canFirstScan(pid: Int?, sectionCount: Int) -> Bool {
        pid != nil && sectionCount > 0 && !hasResults && !isRunning && !isBusy && valuesFilled
    }

    var canNextScan: Bool { hasResults && !isRunning && !isBusy && valuesFilled }
    var canUndo: Bool { status.canUndo && !isRunning && !isBusy }
    var canNewScan: Bool { (hasResults || isRunning || status.state != "idle") && !isBusy }

    /// One line under the buttons: the running scan's progress, or the results and how long the scan took.
    var statusText: String {
        let hits = status.hits.formatted()
        switch status.state {
        case "running":
            var parts = ["\(status.kind == "next" ? "Next" : "First") scan \(Self.percent(status.percent))"]
            if let section = status.section { parts.append(section) }
            parts.append("\(hits) hits")
            parts.append(Self.seconds(status.elapsedSeconds))
            if status.gamePaused { parts.append("game paused") }
            return parts.joined(separator: " · ")
        case "done":
            return "\(status.resultCount.formatted()) results · scan \(status.scanNumber) took \(Self.seconds(status.elapsedSeconds))"
        case "cancelled":
            return "Cancelled after \(Self.seconds(status.elapsedSeconds))" + resultsSuffix
        case "failed":
            return "The scan failed" + resultsSuffix
        default:
            return hasResults ? "\(status.resultCount.formatted()) results (scan \(status.scanNumber))" : "No scan yet"
        }
    }

    /// The bar's value, 0 to 1.
    var progressFraction: Double { min(max(status.percent / 100, 0), 1) }

    private var resultsSuffix: String {
        hasResults ? " · \(status.resultCount.formatted()) results kept (scan \(status.scanNumber))" : ""
    }

    // MARK: Actions

    /// Scans the ticked sections of the chosen process.
    func firstScan(pid: Int, sections: [String]) async {
        let request = FirstScanRequest(
            pid: pid, type: typeName, compare: compare.apiName,
            value: compare.valueCount >= 1 ? value : nil, value2: compare.valueCount >= 2 ? value2 : nil,
            hex: hexApplies && hex, sections: sections, aligned: aligned,
            method: method.apiName, pauseGame: pauseGame)
        await start { try await self.client.firstScan(request) }
    }

    /// Scans the current results again with the compare chosen now.
    func nextScan() async {
        let request = NextScanRequest(
            compare: compare.apiName,
            value: compare.valueCount >= 1 ? value : nil, value2: compare.valueCount >= 2 ? value2 : nil,
            hex: hexApplies && hex, pauseGame: pauseGame)
        await start { try await self.client.nextScan(request) }
    }

    /// Drops every result (and cancels a running scan), so a first scan can start again.
    func newScan() async {
        await act { self.apply(try await self.client.newScan()) }
        await onResultsChanged?(status)
    }

    /// Back to the results before the last next scan.
    func undo() async {
        await act { self.apply(try await self.client.undoScan()) }
        await onResultsChanged?(status)
    }

    /// Asks the running scan to stop. The progress stream then sends the final status.
    func cancel() async {
        do {
            apply(try await client.cancelScan())
        } catch {
            self.error = error.localizedDescription
        }
    }

    /// Fetches the status (e.g. after connecting): shows results the backend already has, and
    /// follows a scan that is already running.
    func refresh() async {
        guard let status = try? await client.scanStatus() else { return }
        apply(status)
        if status.isRunning {
            await follow()
        } else {
            await onResultsChanged?(status)
        }
    }

    /// Forgets everything (on disconnect: the backend drops the results then too).
    func clear() {
        status = .idle
        error = nil
        compare = .exact
    }

    // MARK: Following a scan

    /// Sends a first or next scan, then follows it until it ends.
    private func start(_ send: @escaping () async throws -> ScanStatus) async {
        guard !isBusy, !isRunning else { return }
        var started = false
        await act {
            self.apply(try await send())
            started = true
        }
        if started { await follow() }
    }

    /// Follows the running scan: the progress stream first, then status checks if the stream breaks.
    private func follow() async {
        guard !isFollowing else { return }
        isFollowing = true
        defer { isFollowing = false }

        do {
            for try await event in client.scanProgress() {
                apply(event.status)
                if event.isDone { break }
            }
        } catch {
            // The stream broke: fall back to checking the status below.
        }

        var failures = 0
        while status.isRunning && !Task.isCancelled {
            try? await Task.sleep(nanoseconds: UInt64(pollInterval * 1_000_000_000))
            do {
                apply(try await client.scanStatus())
                failures = 0
            } catch {
                failures += 1
                if failures >= maxPollFailures {
                    self.error = "Lost touch with the backend during the scan: \(error.localizedDescription)"
                    return
                }
            }
        }
        await onResultsChanged?(status)
    }

    /// Takes a new status. Results the backend already has fix the type pickers to their type.
    func apply(_ newStatus: ScanStatus) {
        status = newStatus
        if newStatus.state == "failed", let message = newStatus.message {
            error = message
        }
        if newStatus.hasResults, let type = newStatus.type, let match = ValueKind.from(typeName: type) {
            if kind != match.kind { kind = match.kind }
            if signed != match.signed { signed = match.signed }
        }
        fixCompare()
    }

    /// Runs a button action: marks busy, clears the old error, shows the new one if it fails.
    private func act(_ action: @escaping () async throws -> Void) async {
        guard !isBusy else { return }
        isBusy = true
        error = nil
        defer { isBusy = false }
        do {
            try await action()
        } catch {
            self.error = error.localizedDescription
        }
    }

    /// Keeps the compare picker on a compare that fits the type and scan (e.g. after the first scan,
    /// "unknown initial value" isn't allowed any more).
    private func fixCompare() {
        let available = availableCompares
        if !available.contains(compare) { compare = available.first ?? .exact }
    }

    // MARK: Formatting

    static func percent(_ value: Double) -> String { "\(Int(value.rounded()))%" }

    static func seconds(_ value: Double) -> String {
        value < 10 ? String(format: "%.1f s", value) : "\(Int(value.rounded())) s"
    }
}
