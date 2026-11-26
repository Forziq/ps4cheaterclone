namespace PS4Mem.Core;

/// <summary>
/// Every default the app uses, in one place. All of them can be overridden
/// (CLI flags now, the settings UI later).
/// </summary>
public sealed record PS4MemSettings
{
    /// <summary>
    /// PS4 address on the LAN. Empty by default: each user sets their own
    /// (see <see cref="ConsoleIpResolver"/>).
    /// </summary>
    public string ConsoleIp { get; init; } = "";

    /// <summary>ps4debug command port (client connects here for everything).</summary>
    public int CommandPort { get; init; } = 744;

    /// <summary>ps4debug debugger port (the PS4 connects back to the Mac here).</summary>
    public int DebuggerPort { get; init; } = 755;

    /// <summary>GoldHEN BinLoader port (one payload per session).</summary>
    public int PayloadPort { get; init; } = 9090;

    /// <summary>Debugger payload sent to the BinLoader.</summary>
    public string PayloadPath { get; init; } = "payloads/ps4debug-ng_v1.3.4_release_2026-11-05.bin";

    /// <summary>
    /// How long to wait for the TCP connect. This is the only timeout: reads have none,
    /// because scans can run for minutes (PROMPT §3.5, protocol.md §2).
    /// </summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Largest single CMD_PROC_READ request. Bigger reads are split into chunks of this size.
    /// 32 MB is PS4Cheater's PEEK_BUFFER_LENGTH (protocol.md §8).
    /// </summary>
    public int ReadChunkBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>
    /// Sections whose name contains any of these are selected by default
    /// (PS4Cheater's approach; see PROMPT.md §3.3).
    /// </summary>
    public IReadOnlyList<string> SectionNameFilter { get; init; } =
        ["anon", "exe", "Game", "dlmalloc", "SmallBlockAllocator"];

    /// <summary>
    /// Write a debug log for testing sessions (also on with <c>--debug</c> or <c>PS4MEM_DEBUG=1</c>).
    /// See <see cref="Logging.DebugLog"/>.
    /// </summary>
    public bool DebugLogging { get; init; }

    /// <summary>Folder for debug logs (relative paths are relative to the working directory).</summary>
    public string LogDirectory { get; init; } = "logs";

    /// <summary>How many bytes of each protocol message the debug log hex-dumps.</summary>
    public int DebugWireDumpBytes { get; init; } = 256;

    public static PS4MemSettings Default { get; } = new();
}
