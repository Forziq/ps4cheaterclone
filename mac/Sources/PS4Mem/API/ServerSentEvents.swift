import Foundation

/// One event from the scan progress stream.
struct ScanEvent: Equatable {
    /// True for the last event ("done"): the scan has finished, been cancelled or failed.
    var isDone: Bool
    var status: ScanStatus
}

/// Reads server-sent events line by line. The backend writes each event as
///
///     event: progress
///     data: {"state":"running",...}
///     (blank line)
///
/// Its data is always one line, so an event is complete as soon as its `data:` line arrives. That way
/// it doesn't matter whether the line reader passes the blank lines on or skips them.
struct ServerSentEvents {
    struct Event: Equatable {
        /// The `event:` name ("progress" or "done"); "message" if the event had none.
        var name: String
        var data: String
    }

    private var name: String?

    /// Takes one line (without the line break). Returns an event when the line completes one.
    mutating func feed(_ line: String) -> Event? {
        if line.isEmpty {
            name = nil
        } else if line.hasPrefix(":") {
            // A comment (some servers send these to keep the connection open).
        } else if let value = Self.value(of: "event", in: line) {
            name = value
        } else if let value = Self.value(of: "data", in: line) {
            let event = Event(name: name ?? "message", data: value)
            name = nil
            return event
        }
        return nil
    }

    /// "data: {...}" → "{...}" (one space after the colon is dropped, as the SSE format says).
    private static func value(of field: String, in line: String) -> String? {
        guard line.hasPrefix(field + ":") else { return nil }
        var value = line.dropFirst(field.count + 1)
        if value.first == " " { value = value.dropFirst() }
        return String(value)
    }
}
