using System.Buffers.Binary;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Sections;

namespace PS4Mem.Core.Scanning;

/// <summary>
/// ConsoleScan: the PS4 compares, one section at a time (protocol.md §11). Uses Turbo Scan START
/// (0xBDAACC11, flags 0) when CAPS answers, else the trio's CMD_PROC_SCAN_START (0xBDAACC01).
/// Both need <see cref="PS4DebugClient.Authenticate"/>. The classic 0xBDAA0009 is never used:
/// it compares the wrong bytes (protocol.md §10).
///
/// Sections are sent in pieces of at most <see cref="PieceBytes"/> (the length field is u32), each
/// extended by 3 bytes so values on a piece edge are found once. A piece can't be stopped while
/// the console streams it, so cancel takes effect between pieces.
/// </summary>
public sealed class ConsoleSectionScanner : ISectionScanner
{
    private const string Cat = "scan";
    private const int ValueSize = 4;

    /// <summary>1 GB: a multiple of every alignment, well under the u32 length limit.</summary>
    public const uint PieceBytes = 0x4000_0000;

    private readonly PS4DebugClient _client;
    private readonly DebugLog _log;

    public ConsoleSectionScanner(PS4DebugClient client, bool turbo, DebugLog? log = null)
    {
        _client = client;
        Turbo = turbo;
        _log = log ?? DebugLog.Disabled;
    }

    /// <summary>True for Turbo Scan, false for the iterative trio.</summary>
    public bool Turbo { get; }

    public string Name => "console";

    /// <summary>
    /// Authenticates and checks for Turbo Scan. Returns null and the reason when console scan
    /// isn't possible (stock ps4debug, or auth refused); the client is reconnected in that case,
    /// because the server closes the connection on an unknown command.
    /// </summary>
    public static (ConsoleSectionScanner? Scanner, string Reason) TryCreate(
        PS4DebugClient client, PS4MemSettings settings, DebugLog? log = null)
    {
        log ??= DebugLog.Disabled;
        try
        {
            client.Authenticate(Ps4Auth.ScanBit);
        }
        catch (Ps4ProtocolException e)
        {
            var reason = $"auth failed ({e.Message}); console scan needs ps4debug-NG";
            log.Warn(Cat, $"Console scan unavailable: {reason}");
            Reconnect(client, log);
            return (null, reason);
        }

        try
        {
            var caps = client.GetTurboCaps();
            var reason = $"Turbo Scan (version {caps.Version}, engines 0x{caps.Engines:X})";
            log.Info(Cat, $"Console scan: {reason}");
            return (new ConsoleSectionScanner(client, turbo: true, log), reason);
        }
        catch (Ps4ProtocolException e)
        {
            log.Info(Cat, $"Turbo Scan CAPS failed ({e.Message}); using the SCAN_START trio");
            Reconnect(client, log);
            return (new ConsoleSectionScanner(client, turbo: false, log), "SCAN_START (no Turbo Scan)");
        }
    }

    private static void Reconnect(PS4DebugClient client, DebugLog log)
    {
        if (client.IsConnected) return;
        log.Info(Cat, "Reconnecting after the console closed the connection");
        client.Connect();
    }

    /// <summary>
    /// Exact 4-byte integers only for now (the Phase 0 scan); <see cref="ScanEngine"/> runs other
    /// queries locally. Mapping the other types and compares to the console comes in T1.4.
    /// </summary>
    public bool Supports(ScanQuery query) =>
        query.Compare == ScanCompare.Exact && query.Type is ScanValueType.UInt32 or ScanValueType.Int32;

    public void ScanSection(int pid, Section section, ScanQuery query, int alignment, ScanHitBuffer hits,
        Action<long> bytesDone, CancellationToken cancel)
    {
        if (!Supports(query))
            throw new NotSupportedException($"Console scan can't run '{query}'; use local scan.");
        if (alignment is < 1 or > 255)
            throw new ArgumentOutOfRangeException(nameof(alignment), "Console scan alignment is a u8 (1-255).");
        var command = Turbo ? Ps4Command.TurboStart : Ps4Command.ProcScanStart;
        var needle = query.Value!.Bytes;
        var value = BinaryPrimitives.ReadUInt32LittleEndian(needle);
        var first = hits.Count;

        for (var pos = section.Start; pos < section.End;)
        {
            cancel.ThrowIfCancellationRequested();
            var left = section.End - pos;
            var own = (uint)Math.Min(PieceBytes, left);
            var length = (uint)Math.Min((ulong)own + ValueSize - 1, left);
            var piece = pos;

            _client.ScanRange(command, pid, piece, length, Ps4ScanValueType.UInt32, Ps4ScanCompareType.ExactValue,
                (byte)alignment, needle, (offset, bytes) =>
                {
                    if (offset >= own) return; // starts in the next piece; that piece reports it
                    var found = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                    if (found != value) // exact compare on the console; this would be a server bug
                        _log.Warn(Cat, $"console returned 0x{piece + offset:X} = {found}, not {value}; kept");
                    hits.Add(piece + offset, bytes[..ValueSize]);
                });

            pos += own;
            bytesDone(own);
        }
        hits.SortFrom(first);
    }
}
