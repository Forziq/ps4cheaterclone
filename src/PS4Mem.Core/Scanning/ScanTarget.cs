using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;

namespace PS4Mem.Core.Scanning;

/// <summary>
/// What a scan needs from the current connection: memory access, the connection's settings and
/// log, and the console scanner (created on first use, since that authenticates with the PS4).
/// <see cref="Connection.ConnectionManager.GetScanTarget"/> makes one; tests use fake memory.
/// </summary>
public sealed record ScanTarget(
    IProcessMemory Memory,
    PS4MemSettings Settings,
    DebugLog Log,
    Func<(ISectionScanner? Scanner, string Reason)>? ConsoleScanner = null);
