using PS4Mem.Core.Protocol;
using PS4Mem.Core.Scanning;
using PS4Mem.Core.Sections;
using PS4Mem.Core.Tests.Fakes;
using static PS4Mem.Core.Tests.Fakes.FakeConsole;

namespace PS4Mem.Core.Tests;

/// <summary>Which queries run on the console, and what goes over the wire (T1.4 m3).</summary>
public class ConsoleMappingTests
{
    private const int Pid = 77;

    private static Section Sec(string name, ulong start, ulong size) =>
        new(new MemoryMap(name, start, start + size, 0, 3), true, "test");

    private static ScanQuery Q(ScanValueType type, ScanCompare compare, string? v1 = null, string? v2 = null) =>
        ScanQuery.Parse(type, compare, v1, v2);

    private static ConsoleSectionScanner Scanner(bool turbo, PS4DebugClient? client = null) =>
        new(client ?? new PS4DebugClient(PS4MemSettings.Default with { ConsoleIp = "192.0.2.10" }), turbo);

    private static PS4DebugClient ConnectTo(FakePs4Server server)
    {
        var client = new PS4DebugClient(PS4MemSettings.Default with { ConsoleIp = "127.0.0.1", CommandPort = server.Port });
        client.Connect();
        return client;
    }

    private static readonly byte[] Sentinel = U64(ulong.MaxValue);

    private static byte[] Batch(params (uint Offset, byte[] Value)[] records) =>
        [.. U64((ulong)records.Sum(r => 4 + r.Value.Length)), .. records.SelectMany(r => U32(r.Offset).Concat(r.Value))];

    public static TheoryData<ScanValueType, ScanCompare, bool> Supported => new()
    {
        { ScanValueType.UInt8, ScanCompare.Exact, true },
        { ScanValueType.Int16, ScanCompare.BiggerThan, true },
        { ScanValueType.UInt32, ScanCompare.SmallerThan, true },
        { ScanValueType.Int64, ScanCompare.Between, true },
        { ScanValueType.Float, ScanCompare.BiggerThan, true },
        { ScanValueType.Double, ScanCompare.Between, true },
        { ScanValueType.Float, ScanCompare.Exact, false },        // NG's float tolerance differs
        { ScanValueType.Double, ScanCompare.Exact, false },
        { ScanValueType.UInt32, ScanCompare.UnknownInitial, false },  // snapshot on the Mac
        { ScanValueType.UInt32, ScanCompare.Changed, false },         // next scans re-read on the Mac
        { ScanValueType.UInt32, ScanCompare.IncreasedBy, false },
    };

    [Theory]
    [MemberData(nameof(Supported))]
    public void Supported_compares_are_the_ones_that_match_local_scan(ScanValueType type, ScanCompare compare, bool expected)
    {
        var values = compare.ValueCount();
        var query = Q(type, compare, values >= 1 ? "1" : null, values >= 2 ? "9" : null);
        Assert.Equal(expected, Scanner(turbo: true).Supports(query));
        Assert.Equal(expected, Scanner(turbo: false).Supports(query));
    }

    [Fact]
    public void One_byte_strings_stay_local_on_turbo_only()
    {
        var one = Q(ScanValueType.String, ScanCompare.Exact, "A");
        var two = Q(ScanValueType.String, ScanCompare.Exact, "AB");
        Assert.False(Scanner(turbo: true).Supports(one));   // Turbo's fast exact path finds no strings
        Assert.True(Scanner(turbo: false).Supports(one));
        Assert.True(Scanner(turbo: true).Supports(two));
    }

    [Fact]
    public void Requests_carry_the_wire_types_both_values_and_the_mask()
    {
        var between = Scanner(true).Map(Q(ScanValueType.Int16, ScanCompare.Between, "-5", "300"))!;
        Assert.Equal(Ps4ScanValueType.Int16, between.Type);
        Assert.Equal(Ps4ScanCompareType.ValueBetween, between.Compare);
        Assert.Equal([0xFB, 0xFF, 0x2C, 0x01], between.Pattern);   // value, then the second value
        Assert.Empty(between.Mask);
        Assert.Equal(2, between.ValueSize);

        var bytes = Scanner(true).Map(Q(ScanValueType.ByteArray, ScanCompare.Exact, "DE ?? BE EF"))!;
        Assert.Equal(Ps4ScanValueType.ArrBytes, bytes.Type);
        Assert.Equal([1, 0, 1, 1], bytes.Mask);                     // 1 = compare, 0 = ??
        Assert.Equal(4, bytes.ValueSize);

        var text = Scanner(false).Map(Q(ScanValueType.String, ScanCompare.Exact, "Gold"))!;
        Assert.Equal(Ps4ScanValueType.String, text.Type);
        Assert.Equal("Gold"u8.ToArray(), text.Pattern);
        Assert.Empty(text.Mask);
    }

    [Fact]
    public void Between_on_the_console_sends_both_values_and_reads_2_byte_records()
    {
        FakeRequest? start = null;
        byte[]? pattern = null;
        using var server = new FakePs4Server(c =>
        {
            start = c.Expect(Ps4Command.ProcScanStart, 23);
            c.SendStatus(Ps4Status.Success);
            pattern = c.ReadBytes(4);
            c.SendStatus(Ps4Status.Success);
            c.Send(Batch((0x02, U16(10)), (0x10, U16(20))), Batch((0x7E, U16(15))), Sentinel);
            c.SendStatus(Ps4Status.Success);
        });
        using var client = ConnectTo(server);
        var engine = new ScanEngine(client, consoleScanner: Scanner(turbo: false, client));

        using var r = engine.FirstScan(Pid, [Sec("anon:a", 0x4000, 0x80)], Q(ScanValueType.UInt16, ScanCompare.Between, "10", "20"),
            new ScanOptions { Method = ScanMethod.Console });
        server.WaitForScript();

        Assert.Equal("console", r.Method);
        Assert.Equal((byte)Ps4ScanValueType.UInt16, start!.Body[16]);
        Assert.Equal((byte)Ps4ScanCompareType.ValueBetween, start.Body[17]);
        Assert.Equal(2, start.Body[18]);              // u16 alignment is capped at 2
        Assert.Equal(4U, start.U32(19));              // lenData = both values
        Assert.Equal(0x80U, start.U32(12));
        Assert.Equal([.. U16(10), .. U16(20)], pattern!);
        Assert.Equal([0x4002UL, 0x4010, 0x407E], r.Addresses);
        Assert.Equal(["10", "20", "15"], r.Texts());
    }

    [Fact]
    public void Byte_array_scan_sends_the_mask_after_the_pattern()
    {
        byte[]? pattern = null, mask = null;
        FakeRequest? start = null;
        using var server = new FakePs4Server(c =>
        {
            start = c.Expect(Ps4Command.TurboStart, 27);
            c.SendStatus(Ps4Status.Success);
            pattern = c.ReadBytes(3);
            mask = c.ReadBytes(3);
            c.SendStatus(Ps4Status.Success);
            c.Send(Batch((0x11, [0xAA, 0x42, 0xCC])), Sentinel);
            c.SendStatus(Ps4Status.Success);
        });
        using var client = ConnectTo(server);
        var engine = new ScanEngine(client, consoleScanner: Scanner(turbo: true, client));

        using var r = engine.FirstScan(Pid, [Sec("anon:a", 0x4000, 0x80)], Q(ScanValueType.ByteArray, ScanCompare.Exact, "AA ?? CC"),
            new ScanOptions { Method = ScanMethod.Console });
        server.WaitForScript();

        Assert.Equal((byte)Ps4ScanValueType.ArrBytes, start!.Body[16]);
        Assert.Equal(1, start.Body[18]);              // byte arrays: every byte
        Assert.Equal(3U, start.U32(19));
        Assert.Equal(0x80U, start.U32(12));           // one piece: + (3 - 1) bytes, clamped to the section end
        Assert.Equal([0xAA, 0x00, 0xCC], pattern!);
        Assert.Equal([1, 0, 1], mask!);
        Assert.Equal([0x4011UL], r.Addresses);
    }

    [Fact]
    public void Unsupported_queries_scan_locally_even_when_console_is_asked()
    {
        var mem = new FakeProcessMemory();
        mem.SetBytes(0x1000, BitConverter.GetBytes(2.5f));
        var engine = new ScanEngine(mem, consoleScanner: Scanner(turbo: true));

        using var r = engine.FirstScan(Pid, [Sec("a", 0x1000, 0x10)], Q(ScanValueType.Float, ScanCompare.Exact, "2.5"),
            new ScanOptions { Method = ScanMethod.Console });

        Assert.Equal("local", r.Method);
        Assert.Equal([0x1000UL], r.Addresses);
    }
}
