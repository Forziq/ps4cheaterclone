using System.Net;

namespace PS4Mem.Core.Settings;

/// <summary>
/// The user's saved settings: <c>~/Library/Application Support/PS4Mem/settings.json</c>.
/// Defaults come from <see cref="PS4MemSettings.Default"/>, so they live in one place.
/// The file is private to the user's Mac and is never in the repo.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Last console IP used (empty until the user sets one or connects).</summary>
    public string ConsoleIp { get; init; } = "";

    /// <summary>Recently used IPs, newest first.</summary>
    public IReadOnlyList<string> RecentIps { get; init; } = [];

    /// <summary>ps4debug command port.</summary>
    public int CommandPort { get; init; } = PS4MemSettings.Default.CommandPort;

    /// <summary>GoldHEN BinLoader port the payload is sent to (9090; 9020/9021 on other loaders).</summary>
    public int LoaderPort { get; init; } = PS4MemSettings.Default.PayloadPort;

    /// <summary>The ps4debug payload <c>.bin</c> to inject.</summary>
    public string PayloadPath { get; init; } = PS4MemSettings.Default.PayloadPath;

    /// <summary>Write a debug log for each connection (see <see cref="Logging.DebugLog"/>).</summary>
    public bool DebugLogging { get; init; }

    /// <summary>
    /// Extra connections scans read with (<see cref="PS4MemSettings.ScanReadConnections"/>); null
    /// uses the default. Used from the next connect.
    /// </summary>
    public int? ScanReadConnections { get; init; }

    /// <summary>ps4debug allows 8 clients: the main connection, a debugger and these must fit.</summary>
    public const int MaxScanReadConnections = 6;

    public static AppSettings Default { get; } = new();

    /// <summary>The settings the rest of Core uses, with these values on top of <paramref name="baseSettings"/>.</summary>
    public PS4MemSettings ApplyTo(PS4MemSettings baseSettings) => baseSettings with
    {
        ConsoleIp = ConsoleIp,
        CommandPort = CommandPort,
        PayloadPort = LoaderPort,
        PayloadPath = PayloadPath,
        DebugLogging = DebugLogging,
        ScanReadConnections = ScanReadConnections ?? baseSettings.ScanReadConnections,
    };

    /// <summary>Problems that stop these settings being saved (empty when they're fine).</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (ConsoleIp.Length > 0 && !IPAddress.TryParse(ConsoleIp, out _))
            errors.Add($"consoleIp '{ConsoleIp}' is not a valid IP address.");
        foreach (var ip in RecentIps.Where(ip => !IPAddress.TryParse(ip, out _)))
            errors.Add($"recentIps entry '{ip}' is not a valid IP address.");
        if (CommandPort is < 1 or > 65535)
            errors.Add($"commandPort {CommandPort} must be 1-65535.");
        if (LoaderPort is < 1 or > 65535)
            errors.Add($"loaderPort {LoaderPort} must be 1-65535.");
        if (ScanReadConnections is < 0 or > MaxScanReadConnections)
            errors.Add($"scanReadConnections {ScanReadConnections} must be 0-{MaxScanReadConnections}.");
        if (string.IsNullOrWhiteSpace(PayloadPath))
            errors.Add("payloadPath must not be empty.");
        return errors;
    }

    /// <summary>
    /// <paramref name="ip"/> becomes the console IP and moves to the front of the recent list,
    /// which keeps at most <paramref name="maxRecent"/> entries.
    /// </summary>
    public AppSettings WithRecentIp(string ip, int maxRecent) => this with
    {
        ConsoleIp = ip,
        RecentIps = [ip, .. RecentIps.Where(r => r != ip).Take(Math.Max(0, maxRecent - 1))],
    };
}

/// <summary>
/// A partial update (<c>PUT /api/settings</c>): only the fields that are set change.
/// </summary>
public sealed record AppSettingsUpdate
{
    public string? ConsoleIp { get; init; }
    public IReadOnlyList<string>? RecentIps { get; init; }
    public int? CommandPort { get; init; }
    public int? LoaderPort { get; init; }
    public string? PayloadPath { get; init; }
    public bool? DebugLogging { get; init; }
    public int? ScanReadConnections { get; init; }

    public AppSettings ApplyTo(AppSettings s) => s with
    {
        ConsoleIp = ConsoleIp?.Trim() ?? s.ConsoleIp,
        RecentIps = RecentIps?.Select(ip => ip.Trim()).Distinct().ToList() ?? s.RecentIps,
        CommandPort = CommandPort ?? s.CommandPort,
        LoaderPort = LoaderPort ?? s.LoaderPort,
        PayloadPath = PayloadPath?.Trim() ?? s.PayloadPath,
        DebugLogging = DebugLogging ?? s.DebugLogging,
        ScanReadConnections = ScanReadConnections ?? s.ScanReadConnections,
    };
}
