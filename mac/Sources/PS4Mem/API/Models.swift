import Foundation

// The JSON the backend sends and receives. Property names match the API's camelCase JSON exactly,
// so Swift's Codable needs no key mapping. Fields the API may leave out or send as null are optionals.
// Source of truth: src/PS4Mem.Core/Connection/ConnectionManager.cs (ConnectionStatus),
// src/PS4Mem.Core/Settings/AppSettings.cs and src/PS4Mem.Api/ConnectionEndpoints.cs.

/// `GET /api/status`, and the body of connect, disconnect and inject.
struct ConnectionStatus: Codable, Equatable {
    var connected: Bool
    var ip: String?
    var port: Int?
    /// The ps4debug version, e.g. "1.3.4". Only set while connected.
    var version: String?
    /// When the connection was made (ISO 8601 text; shown as it is).
    var connectedAt: String?
    var debugLogPath: String?
    /// The IP a connect without one would use, and where it comes from. Pre-fills the IP field.
    var defaultIp: String?
    var defaultIpSource: String?
    /// Why the last connect failed or the connection dropped. Cleared by the next connect.
    var lastError: String?

    static let disconnected = ConnectionStatus(connected: false)

    init(connected: Bool, ip: String? = nil, port: Int? = nil, version: String? = nil,
         connectedAt: String? = nil, debugLogPath: String? = nil, defaultIp: String? = nil,
         defaultIpSource: String? = nil, lastError: String? = nil) {
        self.connected = connected
        self.ip = ip
        self.port = port
        self.version = version
        self.connectedAt = connectedAt
        self.debugLogPath = debugLogPath
        self.defaultIp = defaultIp
        self.defaultIpSource = defaultIpSource
        self.lastError = lastError
    }
}

/// `GET /api/settings`: the settings saved in ~/Library/Application Support/PS4Mem/settings.json.
struct AppSettings: Codable, Equatable {
    var consoleIp: String
    /// Newest first.
    var recentIps: [String]
    var commandPort: Int
    /// GoldHEN BinLoader port (9090; 9020 or 9021 on other loaders).
    var loaderPort: Int
    var payloadPath: String
    var debugLogging: Bool
}

/// `PUT /api/settings`: only the fields that aren't nil change. Nil fields are left out of the JSON.
struct AppSettingsUpdate: Encodable, Equatable {
    var consoleIp: String?
    var recentIps: [String]?
    var commandPort: Int?
    var loaderPort: Int?
    var payloadPath: String?
    var debugLogging: Bool?
}

/// Body of `POST /api/connect`. No ip: the API uses PS4_IP, ps4ip.local or the saved IP.
struct ConnectRequest: Encodable, Equatable {
    var ip: String?
}

/// Body of `POST /api/inject`. Nil fields use the saved settings; a path and port that work are saved.
struct InjectRequest: Encodable, Equatable {
    var ip: String?
    var payloadPath: String?
    var loaderPort: Int?
}

/// What `POST /api/inject` did: the file sent, how long ps4debug took to start, and the new status.
struct InjectResponse: Codable, Equatable {
    var payloadPath: String
    var bytesSent: Int64
    var loaderPort: Int
    var waitedSeconds: Double
    var status: ConnectionStatus
}

/// Every error from the API looks like `{"error": "..."}`. The text is written to be shown as it is.
struct ApiErrorBody: Codable, Equatable {
    var error: String
}

// MARK: Processes and sections
// Source of truth: src/PS4Mem.Api/ConnectionEndpoints.cs (ProcessDto, ProcessListDto, SectionDto,
// SectionListDto) and src/PS4Mem.Core/Protocol/Ps4Models.cs (ProcessInfo).

/// One process in `GET /api/processes`. `suggested` marks the likely game; the user still chooses.
struct ProcessEntry: Codable, Equatable, Identifiable {
    var pid: Int
    var name: String
    var suggested: Bool
    var suggestionReason: String?

    var id: Int { pid }
}

/// `GET /api/processes`: every process (by pid), the suggested game's pid and why.
struct ProcessList: Codable, Equatable {
    var processes: [ProcessEntry]
    var suggestedPid: Int?
    var suggestionReason: String
}

/// `GET /api/processes/{pid}` (ProcessInfo in the backend): the chosen process's name, path and title ID (e.g. CUSA11711).
struct ProcessDetails: Codable, Equatable {
    var pid: Int
    var name: String
    var path: String
    var titleId: String
    var contentId: String
}

/// One section (memory map) in `GET /api/processes/{pid}/maps`. Addresses are hex text ("0x...")
/// because a JSON number can't hold every 64-bit address exactly.
struct SectionEntry: Codable, Equatable, Identifiable {
    var name: String
    /// The name, or "(unnamed)" (the game's heap is usually unnamed).
    var displayName: String
    var start: String
    var end: String
    var size: UInt64
    var offset: String
    var prot: Int
    /// e.g. "rw-".
    var protText: String
    /// Selected by the default rules (or by the filter that was sent).
    var selected: Bool
    /// Why it is or isn't selected, e.g. "name contains 'anon'" or "not writable".
    var reason: String

    /// The start address is unique in a process's list, and scans name sections by it.
    var id: String { start }
}

/// `GET /api/processes/{pid}/maps`: the sections list with the backend's selection and counts.
struct SectionList: Codable, Equatable {
    var pid: Int
    /// The name filter used (the defaults when none was sent). Empty = any name.
    var nameFilter: [String]
    var includeReadOnly: Bool
    var sections: [SectionEntry]
    var selectedCount: Int
    var totalCount: Int
    var selectedBytes: UInt64
    var totalBytes: UInt64
    /// Maps left out of the list (end <= start, too big, duplicates, unreadable).
    var skippedMaps: Int
}

// MARK: Scans
// Source of truth: src/PS4Mem.Api/ScanEndpoints.cs (FirstScanRequest, NextScanRequest, ScanStatusDto,
// SectionHitsDto, ScanResultDto, ScanResultsDto, ResultValuesRequest, ResultValuesDto).

/// Body of `POST /api/scan/first`. Nil fields are left out and use the backend's defaults.
struct FirstScanRequest: Encodable, Equatable {
    var pid: Int
    /// u8, i8, u16, i16, u32, i32, u64, i64, f32, f64, string, bytes.
    var type: String
    /// exact, bigger than, smaller than, between, unknown initial value.
    var compare: String
    var value: String?
    /// The second value of "between".
    var value2: String?
    /// Read the typed numbers as hex.
    var hex: Bool
    /// Start addresses ("0x...") of the ticked sections.
    var sections: [String]
    /// false scans every byte; true uses the alignment setting (4 bytes, less for small types).
    var aligned: Bool
    /// Stop the game while scanning (it is always resumed afterwards).
    var pauseGame: Bool
}

/// Body of `POST /api/scan/next`. The type can't change after the first scan, so it isn't sent.
struct NextScanRequest: Encodable, Equatable {
    /// Also increased, increased by, decreased, decreased by, changed, unchanged.
    var compare: String
    var value: String?
    var value2: String?
    var hex: Bool
    var pauseGame: Bool
}

/// Hits of one section in the current results.
struct SectionHits: Codable, Equatable {
    var name: String
    var start: String
    var size: UInt64
    var hits: Int64
    var milliseconds: Double
}

/// The running or last scan and the current results. Sent by every scan endpoint and by the
/// progress stream (`GET /api/scan/progress`).
struct ScanStatus: Codable, Equatable {
    /// idle, running, done, cancelled or failed.
    var state: String
    /// "first" or "next" while/after a scan.
    var kind: String?
    /// The query as text, e.g. "u32 exact 100".
    var query: String?
    var method: String?
    var percent: Double
    /// The section being scanned now.
    var section: String?
    /// Bytes scanned so far, of `total`.
    var done: Int64
    var total: Int64
    /// Hits found so far by the running scan.
    var hits: Int64
    var elapsedSeconds: Double
    /// Why a scan failed or was cancelled, or a note (e.g. the console method fell back to local).
    var message: String?
    var gamePaused: Bool
    /// The process the results belong to.
    var pid: Int?
    /// 0 = no results. Goes up with every scan and changes on undo.
    var scanNumber: Int
    var resultCount: Int64
    /// The results' value type ("u32" ...). Fixed until a new scan.
    var type: String?
    var valueSize: Int
    var resultMethod: String?
    var canUndo: Bool
    /// Hits per section of the current results.
    var sections: [SectionHits]?

    var isRunning: Bool { state == "running" }
    var hasResults: Bool { scanNumber > 0 }

    static let idle = ScanStatus(state: "idle", kind: nil, query: nil, method: nil, percent: 0, section: nil,
                                 done: 0, total: 0, hits: 0, elapsedSeconds: 0, message: nil, gamePaused: false,
                                 pid: nil, scanNumber: 0, resultCount: 0, type: nil, valueSize: 0,
                                 resultMethod: nil, canUndo: false, sections: nil)
}

/// One results row. `value` is what the last scan found (the "previous value" column).
struct ScanResultRow: Codable, Equatable {
    var index: Int64
    var address: String
    var value: String
    var section: String
    var sectionStart: String
    var sectionOffset: String
}

/// `GET /api/scan/results`: a page of rows. `total` is every result.
struct ScanResultsPage: Codable, Equatable {
    var scanNumber: Int
    var type: String?
    var valueSize: Int
    var total: Int64
    var offset: Int64
    var count: Int
    var results: [ScanResultRow]
}

/// Body of `POST /api/scan/results/values`: which rows to read again from the PS4.
struct ResultValuesRequest: Encodable, Equatable {
    var offset: Int64
    var limit: Int
    var hex: Bool
}

struct ResultValue: Codable, Equatable {
    var index: Int64
    var address: String
    var value: String
}

/// The current values of the rows asked for. `scanNumber` says which results they belong to.
struct ResultValues: Codable, Equatable {
    var scanNumber: Int
    var offset: Int64
    var values: [ResultValue]
}

// MARK: Cheats (only what the scanner needs; the cheat list is T2.4)
// Source of truth: src/PS4Mem.Api/CheatEndpoints.cs (CheatRequest, CheatDto).

/// Body of `POST /api/cheats/{titleId}`. Adding a cheat only saves it: it doesn't write memory.
struct NewCheatRequest: Encodable, Equatable {
    var description: String?
    var address: String
    var type: String
    var value: String
    var hex: Bool
    var section: String?
    var sectionStart: String?
    var game: String?
}

/// One cheat as the backend sends it.
struct Cheat: Codable, Equatable, Identifiable {
    var id: String
    var description: String
    var address: String
    var section: String?
    var sectionStart: String?
    var sectionOffset: String?
    var type: String
    var value: String
    var locked: Bool
    var lockPid: Int?
    var lockWrites: Int64?
    var lockError: String?
}
