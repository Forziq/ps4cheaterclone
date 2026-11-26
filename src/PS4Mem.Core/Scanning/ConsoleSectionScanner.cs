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
/// extended by (value size - 1) bytes so values on a piece edge are found once. A piece can't be stopped while
/// the console streams it, so cancel takes effect between pieces.
/// </summary>
public sealed class ConsoleSectionScanner : ISectionScanner
{
    private const string Cat = "scan";

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
    /// The wire request for <paramref name="query"/>, or null when the console can't run it with
    /// exactly the same results as local scan (then <see cref="ScanEngine"/> scans locally).
    /// From NG's compare function (docs/protocol.md §11.4):
    /// <list type="bullet">
    /// <item>Integers: exact, bigger than, smaller than, between (inclusive, either order; signed
    /// types compare signed): the same as local.</item>
    /// <item>Float/double: bigger, smaller and between are plain IEEE compares, the same as local
    /// (Turbo's float policy does nothing with flags 0). Exact is not mapped: NG uses a relative
    /// tolerance, PS4Mem PS4Cheater's 0.0001.</item>
    /// <item>Byte arrays: exact, with a mask (1 = compare the byte, 0 = <c>??</c>).</item>
    /// <item>Strings: exact. Not on Turbo for 1-byte strings: Turbo's fast exact path returns no
    /// hits for strings, and it's used when the step equals the value size.</item>
    /// <item>Unknown initial value and next-scan compares: never (snapshot / re-read on the Mac).</item>
    /// </list>
    /// </summary>
    public ConsoleScanRequest? Map(ScanQuery query)
    {
        var compare = query.Compare switch
        {
            ScanCompare.Exact => Ps4ScanCompareType.ExactValue,
            ScanCompare.BiggerThan => Ps4ScanCompareType.BiggerThan,
            ScanCompare.SmallerThan => Ps4ScanCompareType.SmallerThan,
            ScanCompare.Between => Ps4ScanCompareType.ValueBetween,
            _ => (Ps4ScanCompareType?)null,
        };
        if (compare is null || query.Value is null || query.ValueSize is not { } size) return null;

        var type = query.Type;
        if (type.IsFloat() && compare == Ps4ScanCompareType.ExactValue) return null;
        if (type == ScanValueType.String && Turbo && size == 1) return null;

        var pattern = query.Value2 is { } v2 ? [.. query.Value.Bytes, .. v2.Bytes] : query.Value.Bytes;
        var mask = type == ScanValueType.ByteArray
            ? query.Value.Bytes.Select((_, i) => query.Value.Wildcards?[i] == true ? (byte)0 : (byte)1).ToArray()
            : [];
        return new ConsoleScanRequest(WireType(type), compare.Value, pattern, mask, size);
    }

    public bool Supports(ScanQuery query) => Map(query) is not null;

    private static Ps4ScanValueType WireType(ScanValueType type) => type switch
    {
        ScanValueType.UInt8 => Ps4ScanValueType.UInt8,
        ScanValueType.Int8 => Ps4ScanValueType.Int8,
        ScanValueType.UInt16 => Ps4ScanValueType.UInt16,
        ScanValueType.Int16 => Ps4ScanValueType.Int16,
        ScanValueType.UInt32 => Ps4ScanValueType.UInt32,
        ScanValueType.Int32 => Ps4ScanValueType.Int32,
        ScanValueType.UInt64 => Ps4ScanValueType.UInt64,
        ScanValueType.Int64 => Ps4ScanValueType.Int64,
        ScanValueType.Float => Ps4ScanValueType.Float,
        ScanValueType.Double => Ps4ScanValueType.Double,
        ScanValueType.ByteArray => Ps4ScanValueType.ArrBytes,
        ScanValueType.String => Ps4ScanValueType.String,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown value type."),
    };

    public void ScanSection(int pid, Section section, ScanQuery query, int alignment, ScanHitBuffer hits,
        Action<long> bytesDone, CancellationToken cancel)
    {
        var request = Map(query) ?? throw new NotSupportedException($"Console scan can't run '{query}'; use local scan.");
        if (alignment is < 1 or > 255)
            throw new ArgumentOutOfRangeException(nameof(alignment), "Console scan alignment is a u8 (1-255).");
        var command = Turbo ? Ps4Command.TurboStart : Ps4Command.ProcScanStart;
        var size = request.ValueSize;
        var check = ValueMatcher.Create(query, size); // the console's answer is checked against local semantics
        var first = hits.Count;
        var last = 0UL;
        var inOrder = true;
        long mismatches = 0;

        for (var pos = section.Start; pos < section.End;)
        {
            cancel.ThrowIfCancellationRequested();
            var left = section.End - pos;
            var own = (uint)Math.Min(PieceBytes, left);
            var length = (uint)Math.Min((ulong)own + (ulong)size - 1, left);
            var piece = pos;

            _client.ScanRange(command, pid, piece, length, request.Type, request.Compare, (byte)alignment,
                request.Pattern, request.Mask, size, (offset, bytes) =>
                {
                    if (offset >= own) return; // starts in the next piece; that piece reports it
                    var address = piece + offset;
                    if (!check.IsMatch(bytes, default) && mismatches++ < 10) // would be a server bug
                        _log.Warn(Cat, $"console returned 0x{address:X} = {Convert.ToHexString(bytes)}, which doesn't match '{query}'; kept");
                    inOrder &= hits.Count == first || address > last;
                    last = address;
                    hits.Add(address, bytes);
                });

            pos += own;
            bytesDone(own);
        }
        if (mismatches > 0)
            _log.Warn(Cat, $"section '{section.Name}': {mismatches} console results don't match '{query}'");
        if (inOrder) return;
        if (hits.SpilledCount > first)
            throw new Ps4ProtocolException(
                $"The console returned {hits.Count - first} hits for '{section.Name}' out of address order, too many to sort " +
                "in memory; use local scan.");
        _log.Info(Cat, $"console hits for '{section.Name}' came out of order; sorted");
        hits.SortFrom(first);
    }
}

/// <summary>What <see cref="ConsoleSectionScanner"/> sends for one query (protocol.md §11).</summary>
public sealed record ConsoleScanRequest(Ps4ScanValueType Type, Ps4ScanCompareType Compare, byte[] Pattern, byte[] Mask, int ValueSize);
