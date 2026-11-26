using PS4Mem.Core;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Scanning;
using PS4Mem.Core.Sections;
using PS4Mem.Core.Tests.Fakes;

namespace PS4Mem.Core.Tests;

public class ScanEngineTests
{
    private const int Pid = 77;
    private const uint Money = 123_456;

    private static Section Sec(string name, ulong start, ulong size) =>
        new(new MemoryMap(name, start, start + size, 0, 3), true, "test");

    // Small chunks so the tests cross chunk boundaries.
    private static readonly PS4MemSettings Small = PS4MemSettings.Default with
    {
        ScanChunkBytes = 64,
        NextScanMaxGapBytes = 32,
    };

    [Fact]
    public void First_scan_finds_aligned_hits_only_by_default()
    {
        var mem = new FakeProcessMemory();
        var a = Sec("anon:a", 0x1000, 0x200);
        mem.SetU32(0x1000, Money);   // aligned, first byte
        mem.SetU32(0x1010, Money);   // aligned
        mem.SetU32(0x1021, Money);   // unaligned: skipped with alignment 4
        mem.SetU32(0x11FC, Money);   // aligned, last 4 bytes of the section

        var results = new ScanEngine(mem, Small).FirstScan(Pid, [a], Money);

        Assert.Equal([0x1000UL, 0x1010, 0x11FC], results.Addresses);
        Assert.All(results.U32s(), v => Assert.Equal(Money, v));
        Assert.Equal(1, results.ScanNumber);
        Assert.Equal("local", results.Method);
        Assert.Empty(mem.Writes);
    }

    [Fact]
    public void Alignment_1_finds_unaligned_hits_including_across_chunk_boundaries()
    {
        var mem = new FakeProcessMemory();
        var a = Sec("anon:a", 0x1000, 0x200);
        mem.SetU32(0x1021, Money);
        mem.SetU32(0x103E, Money);   // starts 2 bytes before the 64-byte chunk boundary at 0x1040

        var results = new ScanEngine(mem, Small).FirstScan(Pid, [a], Money, new ScanOptions { Alignment = 1 });

        Assert.Equal([0x1021UL, 0x103E], results.Addresses);
    }

    [Fact]
    public void First_scan_never_reads_past_a_section_end()
    {
        var mem = new FakeProcessMemory();
        var a = Sec("anon:a", 0x1000, 0x100);
        mem.SetU32(0x10FE, Money);   // would need 2 bytes past the end

        var results = new ScanEngine(mem, Small).FirstScan(Pid, [a], Money, new ScanOptions { Alignment = 1 });

        Assert.Empty(results.Addresses);
        Assert.All(mem.Reads, r => Assert.True(r.Address + (ulong)r.Length <= 0x1100));
        Assert.All(mem.Reads, r => Assert.True(r.Length <= 64 + 3));
    }

    [Fact]
    public void Results_are_grouped_per_section_in_address_order()
    {
        var mem = new FakeProcessMemory();
        var low = Sec("executable", 0x1000, 0x100);
        var empty = Sec("anon:empty", 0x3000, 0x100);
        var high = Sec("SmallBlockAllocator", 0x5000, 0x100);
        mem.SetU32(0x1004, Money);
        mem.SetU32(0x5008, Money);
        mem.SetU32(0x500C, Money);

        var seen = new List<SectionScanStats>();
        var results = new ScanEngine(mem, Small)
            .FirstScan(Pid, [high, empty, low], Money, new ScanOptions { SectionDone = seen.Add });

        Assert.Equal(["executable", "anon:empty", "SmallBlockAllocator"], results.Sections.Select(s => s.Section.Name));
        Assert.Equal([1, 0, 2], results.Sections.Select(s => s.Hits));
        Assert.Equal([0, 1, 1], results.Sections.Select(s => s.FirstResult));
        Assert.Equal(results.Sections, seen);
        Assert.Equal("executable", results.SectionOf(0).Name);
        Assert.Equal("SmallBlockAllocator", results.SectionOf(2).Name);
    }

    [Fact]
    public void Next_scan_keeps_only_hits_that_now_equal_the_value()
    {
        var mem = new FakeProcessMemory();
        var a = Sec("anon:a", 0x1000, 0x400);
        var b = Sec("anon:b", 0x8000, 0x100);
        foreach (var addr in new ulong[] { 0x1000, 0x1010, 0x1300, 0x8004 })
            mem.SetU32(addr, Money);
        var engine = new ScanEngine(mem, Small);
        var first = engine.FirstScan(Pid, [a, b], Money);
        Assert.Equal(4, first.Count);

        // Money changed to 999 at two places; another place now holds 999 but was never a hit.
        mem.SetU32(0x1010, 999);
        mem.SetU32(0x8004, 999);
        mem.SetU32(0x1020, 999);

        var next = engine.NextScan(first, 999);

        Assert.Equal([0x1010UL, 0x8004], next.Addresses);
        Assert.Equal([999u, 999u], next.U32s());
        Assert.Equal(2, next.ScanNumber);
        Assert.Equal([1, 1], next.Sections.Select(s => s.Hits));
        Assert.Equal("anon:b", next.SectionOf(1).Name);
        Assert.Empty(mem.Writes);
    }

    [Fact]
    public void Next_scan_batches_nearby_addresses_and_respects_gap_and_chunk_limits()
    {
        var mem = new FakeProcessMemory();
        var a = Sec("anon:a", 0x1000, 0x1000);
        // Close together (one read), then a gap over 32 bytes, then many hits spanning over 64 bytes.
        ulong[] hits = [0x1000, 0x1008, 0x1010, 0x1100, 0x1110, 0x1120, 0x1130, 0x1140, 0x1150];
        foreach (var h in hits) mem.SetU32(h, Money);
        var engine = new ScanEngine(mem, Small);
        var first = engine.FirstScan(Pid, [a], Money);
        mem.Reads.Clear();

        var next = engine.NextScan(first, Money);

        Assert.Equal(hits, next.Addresses);
        Assert.Equal((0x1000UL, 0x14), mem.Reads[0]);               // 0x1000..0x1014
        Assert.All(mem.Reads, r => Assert.True(r.Length <= 64));     // chunk limit
        Assert.True(mem.Reads.Count < hits.Length);                 // batched
        Assert.Equal(3, mem.Reads.Count);                           // [0x1000-0x1014) [0x1100-0x1134) [0x1140-0x1154)
    }

    [Fact]
    public void Next_scan_handles_overlapping_unaligned_hits()
    {
        var mem = new FakeProcessMemory();
        var a = Sec("anon:a", 0x1000, 0x100);
        mem.SetBytes(0x1001, [0, 0, 0, 0, 0, 0, 0]);       // zeros everywhere anyway
        var engine = new ScanEngine(mem, Small);
        var first = engine.FirstScan(Pid, [a], 0, new ScanOptions { Alignment = 1 });
        Assert.Equal(0x100 - 3, first.Count);              // every start that fits

        var next = engine.NextScan(first, 0);

        Assert.Equal(first.Addresses, next.Addresses);
    }

    [Fact]
    public void Progress_reaches_the_total_and_counts_hits()
    {
        var mem = new FakeProcessMemory();
        mem.SetU32(0x1004, Money);
        var progress = new List<ScanProgress>();

        new ScanEngine(mem, Small).FirstScan(Pid, [Sec("anon:a", 0x1000, 0x100), Sec("anon:b", 0x2000, 0x80)],
            Money, new ScanOptions { Progress = progress.Add });

        Assert.Equal(0x180, progress[^1].Done);
        Assert.Equal(0x180, progress[^1].Total);
        Assert.Equal(1, progress[^1].Hits);
        Assert.Equal(1.0, progress[^1].Fraction);
        Assert.True(progress.Select(p => p.Done).SequenceEqual(progress.Select(p => p.Done).Order()));
    }

    [Fact]
    public void Cancel_stops_the_scan()
    {
        var mem = new FakeProcessMemory();
        using var cts = new CancellationTokenSource();
        mem.OnRead = addr => { if (addr >= 0x1080) cts.Cancel(); };

        Assert.ThrowsAny<OperationCanceledException>(() =>
            new ScanEngine(mem, Small).FirstScan(Pid, [Sec("anon:a", 0x1000, 0x1000)], Money, cancel: cts.Token));
        Assert.True(mem.Reads.Count < 0x1000 / 64);
    }

    [Fact]
    public void Each_section_is_logged_with_name_start_size_hits_and_time()
    {
        var mem = new FakeProcessMemory();
        mem.SetU32(0x1004, Money);
        var writer = new StringWriter();
        using var log = new DebugLog(writer);

        new ScanEngine(mem, Small, log).FirstScan(Pid, [Sec("anon:a", 0x1000, 0x100)], Money);

        var text = writer.ToString();
        Assert.Contains("[scan]", text);
        Assert.Contains("section 'anon:a' start 0x1000 size 0x100: 1 hits", text);
        Assert.Contains("Scan #1 (local) done: 1 results", text);
    }

    [Fact]
    public void Console_method_without_a_console_scanner_is_an_error()
    {
        var engine = new ScanEngine(new FakeProcessMemory(), Small);
        Assert.False(engine.HasConsoleScanner);
        Assert.Throws<InvalidOperationException>(() =>
            engine.FirstScan(Pid, [Sec("anon:a", 0x1000, 0x100)], Money, new ScanOptions { Method = ScanMethod.Console }));
    }

    [Fact]
    public void Scan_through_the_real_client_reads_from_the_fake_server()
    {
        // LocalScan over PS4DebugClient: CMD_PROC_READ against FakePs4Server.
        var memory = new byte[0x100];
        BitConverter.GetBytes(Money).CopyTo(memory, 0x40);
        var requests = new List<FakeRequest>();
        using var server = new FakePs4Server(c =>
        {
            for (var i = 0; i < 4; i++) // 0x100 bytes in 64-byte chunks
            {
                var r = c.Expect(Ps4Command.ProcRead, 16);
                requests.Add(r);
                var offset = (int)(r.U64(4) - 0x2000);
                c.SendStatus(Ps4Status.Success);
                c.Send(memory.AsSpan(offset, (int)r.U32(12)).ToArray());
            }
        });
        using var client = new PS4DebugClient();
        client.Connect("127.0.0.1", server.Port);

        var results = new ScanEngine(client, Small).FirstScan(Pid, [Sec("anon:a", 0x2000, 0x100)], Money);
        server.WaitForScript();

        Assert.Equal([0x2040UL], results.Addresses);
        Assert.All(requests, r => Assert.Equal(Pid, r.I32(0)));
        Assert.Equal([67U, 67U, 67U, 64U], requests.Select(r => r.U32(12)));
    }
}
