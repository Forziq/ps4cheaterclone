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

    /// <summary>Debugger payload sent to the BinLoader (a relative path is found as <see cref="ProjectPaths.ResolveFile"/> says).</summary>
    public string PayloadPath { get; init; } = "payloads/ps4debug-ng_v1.3.4_release_2026-11-05.bin";

    /// <summary>Loader ports the app offers: GoldHEN BinLoader first, then other loaders' usual ports.</summary>
    public IReadOnlyList<int> LoaderPortOptions { get; init; } = [9090, 9020, 9021];

    /// <summary>
    /// After sending the payload, how long to keep trying the command port before giving up.
    /// ps4debug takes a few seconds to start (about 10 s on a real console).
    /// </summary>
    public TimeSpan PayloadStartTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How often to try the command port while waiting for the payload to start.</summary>
    public TimeSpan PayloadPollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Connect timeout for one quick "does this port answer?" check (before injecting, and each
    /// poll while waiting). On a LAN a port answers or refuses well within this.
    /// </summary>
    public TimeSpan PortProbeTimeout { get; init; } = TimeSpan.FromSeconds(1);

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
    /// LocalScan reads each section in pieces of this size and compares them on the Mac.
    /// Small enough to show progress and cancel quickly, big enough that round trips don't dominate.
    /// </summary>
    public int ScanChunkBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>
    /// Scan step in bytes: 4 = 4-byte alignment (the default, like PS4Cheater), 1 = every byte.
    /// Each type caps it at its own step (u8 and strings 1, u16 2), like PS4Cheater.
    /// </summary>
    public int ScanAlignment { get; init; } = 4;

    /// <summary>
    /// Unknown initial value keeps only non-zero values, like PS4Cheater. Most of memory is zero,
    /// so this cuts the results a lot; turn it off for a value that starts at 0.
    /// </summary>
    public bool UnknownInitialSkipZeros { get; init; } = true;

    /// <summary>
    /// Next scan: previous hits closer than this are read together in one CMD_PROC_READ
    /// (reading a gap is cheaper than another round trip). Batches never exceed <see cref="ScanChunkBytes"/>.
    /// </summary>
    public int NextScanMaxGapBytes { get; init; } = 64 * 1024;

    /// <summary>
    /// Scan results above this many rows are written to disk instead of kept in memory
    /// (unknown initial value scans can find millions). 2M rows of a 4-byte scan is 24 MB.
    /// </summary>
    public long ScanSpillRows { get; init; } = 2_000_000;

    /// <summary>
    /// Folder for spilled results and unknown-initial snapshots. Empty means
    /// <c>~/Library/Application Support/PS4Mem/scans</c> (<see cref="Scanning.ScanStorage.DefaultRoot"/>).
    /// Each run uses its own subfolder and deletes it on exit; leftovers of runs that crashed are
    /// deleted at the next start.
    /// </summary>
    public string ScanDataDirectory { get; init; } = "";

    /// <summary>How many scans "undo" can go back. Each kept scan may hold disk space.</summary>
    public int ScanUndoLevels { get; init; } = 1;

    /// <summary>How often <c>GET /api/scan/progress</c> sends an update while a scan runs.</summary>
    public TimeSpan ScanProgressInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Results per page when <c>GET /api/scan/results</c> has no limit (PROMPT §5: show the first ~5,000).</summary>
    public int ScanResultsPageRows { get; init; } = 5_000;

    /// <summary>The most results one page (or one value refresh) may ask for.</summary>
    public int ScanResultsMaxPageRows { get; init; } = 100_000;

    /// <summary>
    /// Sections whose name contains any of these are selected by default
    /// (PS4Cheater's approach; see PROMPT.md §3.3).
    /// </summary>
    public IReadOnlyList<string> SectionNameFilter { get; init; } =
        ["anon", "exe", "Game", "dlmalloc", "SmallBlockAllocator"];

    /// <summary>
    /// Also select writable maps that have no name. Games keep most of their data there (a real
    /// game had 2.9 GB of unnamed heap and 9 MB of named sections), and no name filter can match them.
    /// </summary>
    public bool SectionIncludeUnnamed { get; init; } = true;

    /// <summary>Selected-by-default sections must also be writable (PROMPT.md §3.3).</summary>
    public bool SectionRequireWritable { get; init; } = true;

    /// <summary>
    /// Maps bigger than this are treated as broken and skipped (64 GB, the same limit as
    /// PS4CheaterNeo, SectionTool.cs:308; protocol.md §14).
    /// </summary>
    public ulong MaxSectionBytes { get; init; } = 0x10_0000_0000UL;

    /// <summary>
    /// <c>*.elf</c> processes whose name starts with one of these (ignoring case) are system
    /// processes, never the game. The last four are .elf names seen on a real console.
    /// </summary>
    public IReadOnlyList<string> SystemProcessPrefixes { get; init; } =
        ["Sce", "sys", "mini-syscore", "orbis_", "GnmCompositor", "fs_cleaner"];

    /// <summary>
    /// Folder for the cheat tables, one <c>&lt;TITLEID&gt;.json</c> per game. Empty means
    /// <c>~/Library/Application Support/PS4Mem/cheats</c> (<see cref="Cheats.CheatStore.DefaultRoot"/>).
    /// </summary>
    public string CheatDirectory { get; init; } = "";

    /// <summary>How often a locked cheat is written again (PROMPT §5: about every 200 ms).</summary>
    public TimeSpan CheatLockInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>The usual game process name (checked first by the process picker).</summary>
    public string GameProcessName { get; init; } = "eboot.bin";

    /// <summary>
    /// Write a debug log for testing sessions (also on with <c>--debug</c> or <c>PS4MEM_DEBUG=1</c>).
    /// See <see cref="Logging.DebugLog"/>.
    /// </summary>
    public bool DebugLogging { get; init; }

    /// <summary>Folder for debug logs (a relative path goes in the project folder; see <see cref="ProjectPaths"/>).</summary>
    public string LogDirectory { get; init; } = "logs";

    /// <summary>How many bytes of each protocol message the debug log hex-dumps.</summary>
    public int DebugWireDumpBytes { get; init; } = 256;

    /// <summary>
    /// Where the backend API listens. Loopback only: the API can write to PS4 memory, so
    /// nothing else on the network may reach it. Override with <c>--urls</c> (still loopback only).
    /// </summary>
    public string ApiUrl { get; init; } = "http://127.0.0.1:5080";

    /// <summary>How many recently used console IPs the app settings remember.</summary>
    public int MaxRecentIps { get; init; } = 8;

    public static PS4MemSettings Default { get; } = new();
}
