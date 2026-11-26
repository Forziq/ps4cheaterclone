using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;

namespace PS4Mem.Core.Scanning;

/// <summary>
/// What a scan needs from the current connection: memory access, the connection's settings and
/// log, the console scanner (created on first use, since that authenticates with the PS4) and
/// process stop/resume for "pause the game during scans", and the extra connections scans read
/// with in parallel (opened on first use; empty or null reads with <see cref="Memory"/>).
/// <see cref="Connection.ConnectionManager.GetScanTarget"/> makes one; tests use fake memory.
/// </summary>
public sealed record ScanTarget(
    IProcessMemory Memory,
    PS4MemSettings Settings,
    DebugLog Log,
    Func<(ISectionScanner? Scanner, string Reason)>? ConsoleScanner = null,
    IProcessControl? Control = null,
    Func<CancellationToken, IReadOnlyList<IProcessMemory>>? Readers = null);
