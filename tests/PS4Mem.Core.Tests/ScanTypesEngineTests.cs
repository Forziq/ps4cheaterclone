using System.Buffers.Binary;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Scanning;
using PS4Mem.Core.Sections;
using PS4Mem.Core.Tests.Fakes;

namespace PS4Mem.Core.Tests;

/// <summary>LocalScan with every value type and compare, end to end through <see cref="ScanEngine"/>.</summary>
public class ScanTypesEngineTests
{
    private const int Pid = 77;

    private static Section Sec(string name, ulong start, ulong size) =>
        new(new MemoryMap(name, start, start + size, 0, 3), true, "test");

    // Small chunks so values cross chunk boundaries.
    private static readonly PS4MemSettings Small = PS4MemSettings.Default with
    {
        ScanChunkBytes = 64,
        NextScanMaxGapBytes = 32,
    };

    private static ScanQuery Q(ScanValueType type, ScanCompare compare, string? v1 = null, string? v2 = null) =>
        ScanQuery.Parse(type, compare, v1, v2);

    private static void Set(FakeProcessMemory mem, ScanValueType type, ulong address, string value) =>
        mem.SetBytes(address, ValueParser.Parse(type, value).Bytes);

    [Fact]
    public void U8_bigger_than_checks_every_byte()
    {
        var mem = new FakeProcessMemory();
        Set(mem, ScanValueType.UInt8, 0x1001, "200");
        Set(mem, ScanValueType.UInt8, 0x1003, "99");
        Set(mem, ScanValueType.UInt8, 0x103F, "101");   // last byte of the first 64-byte chunk

        var r = new ScanEngine(mem, Small).FirstScan(Pid, [Sec("a", 0x1000, 0x80)],
            Q(ScanValueType.UInt8, ScanCompare.BiggerThan, "100"));

        Assert.Equal([0x1001UL, 0x103F], r.Addresses);
        Assert.Equal(["200", "101"], r.Texts());
        Assert.Equal(1, r.ValueSize);
        Assert.Equal(ScanValueType.UInt8, r.Type);
    }

    [Fact]
    public void I16_exact_uses_2_byte_steps_unless_alignment_is_off()
    {
        var mem = new FakeProcessMemory();
        Set(mem, ScanValueType.Int16, 0x1002, "-300");
        Set(mem, ScanValueType.Int16, 0x1011, "-300");   // odd address
        Set(mem, ScanValueType.Int16, 0x103F, "-300");   // crosses the chunk edge at 0x1040
        var engine = new ScanEngine(mem, Small);
        var q = Q(ScanValueType.Int16, ScanCompare.Exact, "-300");

        Assert.Equal([0x1002UL], engine.FirstScan(Pid, [Sec("a", 0x1000, 0x80)], q).Addresses);
        Assert.Equal([0x1002UL, 0x1011, 0x103F],
            engine.FirstScan(Pid, [Sec("a", 0x1000, 0x80)], q, new ScanOptions { Alignment = 1 }).Addresses);
    }

    [Fact]
    public void I16_between_negatives()
    {
        var mem = new FakeProcessMemory();
        Set(mem, ScanValueType.Int16, 0x1000, "-50");
        Set(mem, ScanValueType.Int16, 0x1002, "-101");
        Set(mem, ScanValueType.Int16, 0x1004, "-100");
        Set(mem, ScanValueType.Int16, 0x1006, "-1");

        var r = new ScanEngine(mem, Small).FirstScan(Pid, [Sec("a", 0x1000, 0x40)],
            Q(ScanValueType.Int16, ScanCompare.Between, "-1", "-100"));

        Assert.Equal([0x1000UL, 0x1004, 0x1006], r.Addresses);
        Assert.Equal(["-50", "-100", "-1"], r.Texts());
    }

    [Theory]
    [InlineData(ScanValueType.UInt64, "12345678901234")]
    [InlineData(ScanValueType.Int64, "-12345678901234")]
    [InlineData(ScanValueType.Double, "1234.5")]
    public void Eight_byte_values_step_4_and_cross_chunk_edges(ScanValueType type, string value)
    {
        var mem = new FakeProcessMemory();
        Set(mem, type, 0x1004, value);   // 4-aligned, not 8-aligned
        Set(mem, type, 0x103C, value);   // starts 4 bytes before the chunk edge
        Set(mem, type, 0x1051, value);   // unaligned: skipped with alignment on
        Set(mem, type, 0x1078, value);   // last 8 bytes of the section

        var r = new ScanEngine(mem, Small).FirstScan(Pid, [Sec("a", 0x1000, 0x80)], Q(type, ScanCompare.Exact, value));

        Assert.Equal([0x1004UL, 0x103C, 0x1078], r.Addresses);
        Assert.All(r.Texts(), t => Assert.Equal(value, t));
        Assert.Equal(8, r.ValueSize);
    }

    [Fact]
    public void Float_exact_matches_within_the_tolerance()
    {
        var mem = new FakeProcessMemory();
        Set(mem, ScanValueType.Float, 0x1000, "100.00005");
        Set(mem, ScanValueType.Float, 0x1008, "100");
        Set(mem, ScanValueType.Float, 0x1010, "100.01");

        var r = new ScanEngine(mem, Small).FirstScan(Pid, [Sec("a", 0x1000, 0x40)],
            Q(ScanValueType.Float, ScanCompare.Exact, "100"));

        Assert.Equal([0x1000UL, 0x1008], r.Addresses);
    }

    [Fact]
    public void Float_smaller_than_ignores_nan()
    {
        var mem = new FakeProcessMemory();
        mem.SetU32(0x1000, 0x7FC00000);                  // NaN
        Set(mem, ScanValueType.Float, 0x1004, "-3.5");
        mem.SetU32(0x1008, 0xFF800000);                  // -infinity

        var r = new ScanEngine(mem, Small).FirstScan(Pid, [Sec("a", 0x1000, 0x10)],
            Q(ScanValueType.Float, ScanCompare.SmallerThan, "-1"));

        Assert.Equal([0x1004UL, 0x1008], r.Addresses);
    }

    [Fact]
    public void String_exact_finds_unaligned_text_across_chunk_edges()
    {
        var mem = new FakeProcessMemory();
        mem.SetBytes(0x1003, "Gold"u8);
        mem.SetBytes(0x103E, "Gold"u8);   // "Go" in chunk 1, "ld" in chunk 2
        mem.SetBytes(0x1050, "gold"u8);   // case differs
        mem.SetBytes(0x107D, "Gol"u8);    // would end past the section

        var r = new ScanEngine(mem, Small).FirstScan(Pid, [Sec("a", 0x1000, 0x80)],
            Q(ScanValueType.String, ScanCompare.Exact, "Gold"));

        Assert.Equal([0x1003UL, 0x103E], r.Addresses);
        Assert.Equal(["Gold", "Gold"], r.Texts());
        Assert.Equal(4, r.ValueSize);
        Assert.All(mem.Reads, read => Assert.True(read.Address + (ulong)read.Length <= 0x1080));
    }

    [Fact]
    public void Byte_array_with_leading_and_middle_wildcards()
    {
        var mem = new FakeProcessMemory();
        mem.SetBytes(0x1000, [0xAA, 0x34, 0x00, 0x78]);   // at the section start
        mem.SetBytes(0x103F, [0xBB, 0x34, 0x11, 0x78]);   // across the chunk edge
        mem.SetBytes(0x1060, [0xCC, 0x34, 0x22, 0x79]);   // last byte differs
        mem.SetBytes(0x107D, [0xDD, 0x34, 0x33]);         // doesn't fit before the end

        var r = new ScanEngine(mem, Small).FirstScan(Pid, [Sec("a", 0x1000, 0x80)],
            Q(ScanValueType.ByteArray, ScanCompare.Exact, "?? 34 ?? 78"));

        Assert.Equal([0x1000UL, 0x103F], r.Addresses);
        Assert.Equal(["AA 34 00 78", "BB 34 11 78"], r.Texts());   // the bytes really found
    }

    [Fact]
    public void Byte_array_without_wildcards_ignores_the_alignment_option()
    {
        var mem = new FakeProcessMemory();
        mem.SetBytes(0x1005, [0xDE, 0xAD, 0xBE, 0xEF]);

        var r = new ScanEngine(mem, Small).FirstScan(Pid, [Sec("a", 0x1000, 0x40)],
            Q(ScanValueType.ByteArray, ScanCompare.Exact, "DEADBEEF"), new ScanOptions { Alignment = 4 });

        Assert.Equal([0x1005UL], r.Addresses);
    }

    [Fact]
    public void Section_start_that_isnt_aligned_starts_at_the_next_aligned_address()
    {
        var mem = new FakeProcessMemory();
        Set(mem, ScanValueType.UInt32, 0x1002, "5");   // in the section but unaligned
        Set(mem, ScanValueType.UInt32, 0x1004, "5");

        var r = new ScanEngine(mem, Small).FirstScan(Pid, [Sec("a", 0x1002, 0x40)],
            Q(ScanValueType.UInt32, ScanCompare.BiggerThan, "4"));

        Assert.Equal(0x1004UL, r.AddressAt(0));
    }

    [Fact]
    public void Unknown_initial_then_next_scans_narrow_it_down()
    {
        var mem = new FakeProcessMemory();
        var sec = Sec("a", 0x1000, 0x40);
        Set(mem, ScanValueType.Int32, 0x1000, "100");   // will increase
        Set(mem, ScanValueType.Int32, 0x1010, "100");   // will decrease
        Set(mem, ScanValueType.Int32, 0x1020, "7");     // stays
        Set(mem, ScanValueType.Int32, 0x103C, "-1");    // stays
        var engine = new ScanEngine(mem, Small);

        var first = engine.FirstScan(Pid, [sec], new ScanQuery(ScanValueType.Int32, ScanCompare.UnknownInitial));
        Assert.Equal([0x1000UL, 0x1010, 0x1020, 0x103C], first.Addresses);   // zeros skipped by default
        Assert.Equal(["100", "100", "7", "-1"], first.Texts());

        Set(mem, ScanValueType.Int32, 0x1000, "150");
        Set(mem, ScanValueType.Int32, 0x1010, "90");

        var changed = engine.NextScan(first, new ScanQuery(ScanValueType.Int32, ScanCompare.Changed));
        Assert.Equal([0x1000UL, 0x1010], changed.Addresses);
        Assert.Equal(["150", "90"], changed.Texts());     // results keep the new values

        var unchanged = engine.NextScan(first, new ScanQuery(ScanValueType.Int32, ScanCompare.Unchanged));
        Assert.Equal([0x1020UL, 0x103C], unchanged.Addresses);

        Assert.Equal([0x1000UL], engine.NextScan(first, new ScanQuery(ScanValueType.Int32, ScanCompare.Increased)).Addresses);
        Assert.Equal([0x1010UL], engine.NextScan(first, new ScanQuery(ScanValueType.Int32, ScanCompare.Decreased)).Addresses);
        Assert.Equal([0x103CUL], engine.NextScan(unchanged, Q(ScanValueType.Int32, ScanCompare.SmallerThan, "0")).Addresses);
        Assert.Equal([0x1000UL], engine.NextScan(first, Q(ScanValueType.Int32, ScanCompare.IncreasedBy, "50")).Addresses);
        Assert.Equal([0x1010UL], engine.NextScan(first, Q(ScanValueType.Int32, ScanCompare.DecreasedBy, "10")).Addresses);
        Assert.Equal([0x1020UL], engine.NextScan(first, Q(ScanValueType.Int32, ScanCompare.Exact, "7")).Addresses);
        Assert.Equal([0x1000UL], engine.NextScan(first, Q(ScanValueType.Int32, ScanCompare.BiggerThan, "100")).Addresses);
        Assert.Equal([0x1010UL, 0x1020], engine.NextScan(first, Q(ScanValueType.Int32, ScanCompare.Between, "0", "99")).Addresses);
        Assert.Empty(mem.Writes);
    }

    [Fact]
    public void Unknown_initial_keeps_zeros_when_the_setting_is_off()
    {
        var mem = new FakeProcessMemory();
        Set(mem, ScanValueType.UInt16, 0x1004, "9");
        var settings = Small with { UnknownInitialSkipZeros = false };

        var r = new ScanEngine(mem, settings).FirstScan(Pid, [Sec("a", 0x1000, 0x40)],
            new ScanQuery(ScanValueType.UInt16, ScanCompare.UnknownInitial));

        Assert.Equal(0x40 / 2, r.Count);   // every 2-byte step
        Assert.Equal("9", r.FormatValue(2));
    }

    [Fact]
    public void Chained_next_scans_compare_with_the_last_scan_not_the_first()
    {
        var mem = new FakeProcessMemory();
        Set(mem, ScanValueType.Double, 0x1000, "10");
        var engine = new ScanEngine(mem, Small);
        var sec = Sec("a", 0x1000, 0x10);

        var first = engine.FirstScan(Pid, [sec], Q(ScanValueType.Double, ScanCompare.Exact, "10"));
        Set(mem, ScanValueType.Double, 0x1000, "10.5");
        var second = engine.NextScan(first, Q(ScanValueType.Double, ScanCompare.IncreasedBy, "0.5"));
        Set(mem, ScanValueType.Double, 0x1000, "11");
        var third = engine.NextScan(second, Q(ScanValueType.Double, ScanCompare.IncreasedBy, "0.5"));

        Assert.Equal([0x1000UL], third.Addresses);
        Assert.Equal(3, third.ScanNumber);
        Assert.Equal("11", third.FormatValue(0));
    }

    [Fact]
    public void Unsigned_increased_by_wraps_in_next_scans()
    {
        var mem = new FakeProcessMemory();
        Set(mem, ScanValueType.UInt8, 0x1000, "255");
        var engine = new ScanEngine(mem, Small);
        var first = engine.FirstScan(Pid, [Sec("a", 0x1000, 0x1)], Q(ScanValueType.UInt8, ScanCompare.Exact, "255"));

        Set(mem, ScanValueType.UInt8, 0x1000, "4");
        var next = engine.NextScan(first, Q(ScanValueType.UInt8, ScanCompare.IncreasedBy, "5"));

        Assert.Equal([0x1000UL], next.Addresses);   // 255 + 5 wraps to 4
    }

    [Fact]
    public void String_next_scans_can_change_the_text_or_check_for_change()
    {
        var mem = new FakeProcessMemory();
        mem.SetBytes(0x1000, "Hero"u8);
        mem.SetBytes(0x1010, "Hero"u8);
        var engine = new ScanEngine(mem, Small);
        var first = engine.FirstScan(Pid, [Sec("a", 0x1000, 0x20)], Q(ScanValueType.String, ScanCompare.Exact, "Hero"));
        Assert.Equal(2, first.Count);

        mem.SetBytes(0x1000, "Heroine"u8);   // first 4 bytes still "Hero"
        mem.SetBytes(0x1010, "Hera"u8);
        var changed = engine.NextScan(first, new ScanQuery(ScanValueType.String, ScanCompare.Changed));
        Assert.Equal([0x1010UL], changed.Addresses);   // compares the 4 bytes of the previous scan
        Assert.Equal(4, changed.ValueSize);

        var longer = engine.NextScan(first, Q(ScanValueType.String, ScanCompare.Exact, "Heroine"));
        Assert.Equal([0x1000UL], longer.Addresses);    // a longer text reads 7 bytes per result
        Assert.Equal("Heroine", longer.FormatValue(0));
    }

    [Fact]
    public void Next_scan_compares_are_refused_on_a_first_scan_and_the_reverse()
    {
        var engine = new ScanEngine(new FakeProcessMemory(), Small);
        var sec = Sec("a", 0x1000, 0x10);

        Assert.Contains("needs a previous scan", Assert.Throws<ArgumentException>(() =>
            engine.FirstScan(Pid, [sec], new ScanQuery(ScanValueType.UInt32, ScanCompare.Increased))).Message);

        var first = engine.FirstScan(Pid, [sec], new ScanQuery(ScanValueType.UInt32, ScanCompare.UnknownInitial));
        Assert.Contains("first scan only", Assert.Throws<ArgumentException>(() =>
            engine.NextScan(first, new ScanQuery(ScanValueType.UInt32, ScanCompare.UnknownInitial))).Message);
        Assert.Contains("start a new scan", Assert.Throws<ArgumentException>(() =>
            engine.NextScan(first, Q(ScanValueType.Float, ScanCompare.Exact, "1"))).Message);
    }

    private sealed class ExactU32OnlyConsole : ISectionScanner
    {
        public int Calls;
        public string Name => "console";
        public bool Supports(ScanQuery query) => query.Type == ScanValueType.UInt32 && query.Compare == ScanCompare.Exact;

        public void ScanSection(int pid, Section section, ScanQuery query, int alignment, ScanHitBuffer hits,
            Action<long> bytesDone, CancellationToken cancel) => Calls++;
    }

    [Fact]
    public void Console_method_falls_back_to_local_for_queries_it_cant_run()
    {
        var mem = new FakeProcessMemory();
        Set(mem, ScanValueType.Float, 0x1000, "2.5");
        var console = new ExactU32OnlyConsole();
        var engine = new ScanEngine(mem, Small, consoleScanner: console);
        var options = new ScanOptions { Method = ScanMethod.Console };

        var r = engine.FirstScan(Pid, [Sec("a", 0x1000, 0x10)], Q(ScanValueType.Float, ScanCompare.Exact, "2.5"), options);
        Assert.Equal("local", r.Method);
        Assert.Equal([0x1000UL], r.Addresses);
        Assert.Equal(0, console.Calls);

        Assert.Equal("console", engine.FirstScan(Pid, [Sec("a", 0x1000, 0x10)], 5u, options).Method);
        Assert.Equal(1, console.Calls);
    }

    [Theory]
    [InlineData(ScanValueType.UInt32, ScanCompare.Exact, true)]
    [InlineData(ScanValueType.Int32, ScanCompare.Exact, true)]
    [InlineData(ScanValueType.UInt32, ScanCompare.BiggerThan, false)]
    [InlineData(ScanValueType.Float, ScanCompare.Exact, false)]
    [InlineData(ScanValueType.UInt16, ScanCompare.Exact, false)]
    public void Console_scanner_supports_exact_4_byte_integers_for_now(ScanValueType type, ScanCompare compare, bool expected)
    {
        var scanner = new ConsoleSectionScanner(new PS4DebugClient(PS4MemSettings.Default with { ConsoleIp = "192.0.2.10" }), turbo: true);
        Assert.Equal(expected, scanner.Supports(Q(type, compare, "1")));
    }

    [Fact]
    public void Hit_buffer_sorting_keeps_values_with_their_addresses()
    {
        var hits = new ScanHitBuffer(2);
        hits.Add(0x30, [3, 3]);
        hits.Add(0x10, [1, 1]);
        hits.Add(0x20, [2, 2]);
        hits.SortFrom(0);

        Assert.Equal([0x10UL, 0x20, 0x30], Enumerable.Range(0, 3).Select(i => hits.AddressAt(i)));
        Assert.Equal([1, 1, 2, 2, 3, 3], Enumerable.Range(0, 3).SelectMany(i => hits.ValueAt(i).ToArray()));
        Assert.Throws<ArgumentException>(() => hits.Add(0x40, [1, 2, 3]));
    }

    [Fact]
    public void First_scan_logs_the_query()
    {
        var writer = new StringWriter();
        using var log = new PS4Mem.Core.Logging.DebugLog(writer);
        new ScanEngine(new FakeProcessMemory(), Small, log).FirstScan(Pid, [Sec("a", 0x1000, 0x10)],
            Q(ScanValueType.Int16, ScanCompare.Between, "-5", "5"));
        Assert.Contains("between i16 -5 and 5, alignment 2", writer.ToString());
    }
}
