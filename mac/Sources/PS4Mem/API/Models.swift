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
