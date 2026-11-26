using System.Buffers.Binary;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Scanning;
using PS4Mem.Core.Sections;
using PS4Mem.Core.Tests.Fakes;

namespace PS4Mem.Core.Tests;

/// <summary>Unknown initial value snapshot and undo (T1.4 m2).</summary>
public sealed class SnapshotAndUndoTests : IDisposable
{
    private const int Pid = 77;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ps4mem-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static Section Sec(string name, ulong start, ulong size) =>
        new(new MemoryMap(name, start, start + size, 0, 3), true, "test");

    private static ScanQuery Unknown(ScanValueType type = ScanValueType.UInt32) => new(type, ScanCompare.UnknownInitial);

    private static ScanQuery Next(ScanCompare compare, ScanValueType type = ScanValueType.UInt32) => new(type, compare);

    /// <summary>Sparse random bytes: about a third of the values are zero.</summary>
    private static DenseProcessMemory RandomMemory(ulong start, int size, int seed = 1)
    {
        var mem = new DenseProcessMemory(start, size);
        var rng = new Random(seed);
        rng.NextBytes(mem.Data);
        for (var i = 0; i < size; i += 4)
            if (rng.Next(3) == 0) mem.Data.AsSpan(i, 4).Clear();
        return mem;
    }

    /// <summary>What unknown initial value should find, worked out the slow way.</summary>
    private static List<(ulong Address, byte[] Value)> Expected(DenseProcessMemory mem, IEnumerable<Section> sections,
        int size, int alignment, bool skipZeros)
    {
        var list = new List<(ulong, byte[])>();
        foreach (var s in sections)
            for (var a = s.Start; a + (ulong)size <= s.End; a++)
            {
                if (a % (ulong)alignment != 0) continue;
                var value = mem.Data.AsSpan((int)(a - mem.Base), size).ToArray();
                if (!skipZeros || value.Any(b => b != 0)) list.Add((a, value));
            }
        return list;
    }

    private static string[] FilesIn(ScanStorage storage) => Directory.GetFiles(storage.SessionDirectory);

    [Theory]
    [InlineData(4, true)]
    [InlineData(1, true)]
    [InlineData(4, false)]
    public void Snapshot_results_match_every_aligned_value(int alignment, bool skipZeros)
    {
        // Two sections over 64 KB blocks, one starting unaligned; chunks that don't line up with blocks.
        var mem = RandomMemory(0x10_0000, 0x6_0000);
        var sections = new[] { Sec("anon:a", 0x10_0002, 0x2_1003), Sec("anon:b", 0x13_0000, 0x2_8000) };
        var settings = PS4MemSettings.Default with { ScanChunkBytes = 3000, UnknownInitialSkipZeros = skipZeros };
        using var storage = new ScanStorage(_root);
        var engine = new ScanEngine(mem, settings, storage: storage);

        using var r = engine.FirstScan(Pid, sections, Unknown(), new ScanOptions { Alignment = alignment });
        var expected = Expected(mem, sections, 4, alignment, skipZeros);

        Assert.True(r.IsSnapshot);
        Assert.True(r.OnDisk);
        Assert.Equal(expected.Count, r.Count);
        Assert.Equal(expected.Select(e => e.Address), r.Addresses);
        Assert.Equal(expected.Count(e => e.Address < 0x13_0000), r.Sections[0].Hits);
        Assert.Equal(expected.Count(e => e.Address < 0x13_0000), r.Sections[1].FirstResult);

        // Random access (not in order) gives the same rows and values.
        var rng = new Random(5);
        for (var n = 0; n < 500; n++)
        {
            var i = rng.NextInt64(r.Count);
            Assert.Equal(expected[(int)i].Address, r.AddressAt(i));
            Assert.Equal(expected[(int)i].Value, r.ValueAt(i).ToArray());
            Assert.Equal(expected[(int)i].Address < 0x13_0000 ? "anon:a" : "anon:b", r.SectionOf(i).Name);
        }
        Assert.Equal(0, mem.WriteCount);
    }

    [Fact]
    public void Snapshot_is_one_file_as_big_as_the_sections_and_dispose_deletes_it()
    {
        var mem = RandomMemory(0x10_0000, 0x4_0000);
        using var storage = new ScanStorage(_root);
        var engine = new ScanEngine(mem, PS4MemSettings.Default with { ScanChunkBytes = 8192 }, storage: storage);

        var r = engine.FirstScan(Pid, [Sec("a", 0x10_0000, 0x1_0000), Sec("b", 0x12_0000, 0x8000)], Unknown());

        var file = Assert.Single(FilesIn(storage));
        Assert.Equal(0x1_8000, new FileInfo(file).Length);
        r.Dispose();
        Assert.Empty(FilesIn(storage));
    }

    [Fact]
    public void Without_storage_the_snapshot_is_kept_in_memory()
    {
        var mem = RandomMemory(0x10_0000, 0x1_0000);
        using var r = new ScanEngine(mem).FirstScan(Pid, [Sec("a", 0x10_0000, 0x1_0000)], Unknown());

        Assert.True(r.IsSnapshot);
        Assert.False(r.OnDisk);
        Assert.Equal(Expected(mem, [Sec("a", 0x10_0000, 0x1_0000)], 4, 4, true).Count, r.Count);
    }

    [Fact]
    public void Next_scan_compares_each_value_with_its_snapshot_bytes()
    {
        var mem = new DenseProcessMemory(0x1000, 0x2_0000);
        void Set(ulong address, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(mem.Data.AsSpan((int)(address - mem.Base)), v);
        for (var a = 0x1000UL; a < 0x21000; a += 4) Set(a, 7);        // every value starts at 7
        Set(0x1000, 0);                                               // zero: not a result (skip zeros)
        var section = Sec("anon:a", 0x1000, 0x2_0000);
        var engine = new ScanEngine(mem, PS4MemSettings.Default with { ScanChunkBytes = 4096 });
        using var first = engine.FirstScan(Pid, [section], Unknown());
        Assert.Equal(0x8000 - 1, first.Count);

        Set(0x1000, 50);    // was zero: still not a result
        Set(0x1004, 9);     // increased
        Set(0x1FFC, 3);     // decreased, last 4 bytes of a 4 KB chunk
        Set(0x2_0FFC, 8);   // increased, last value of the section

        using var changed = engine.NextScan(first, Next(ScanCompare.Changed));
        Assert.Equal([0x1004UL, 0x1FFC, 0x2_0FFC], changed.Addresses);
        Assert.False(changed.IsSnapshot);
        Assert.Equal([9u, 3, 8], changed.U32s());

        using var increased = engine.NextScan(first, Next(ScanCompare.Increased));
        Assert.Equal([0x1004UL, 0x2_0FFC], increased.Addresses);

        using var unchanged = engine.NextScan(first, Next(ScanCompare.Unchanged));
        Assert.Equal(0x8000 - 4, unchanged.Count);
        Assert.Equal(2, unchanged.ScanNumber);

        // And the scan after that works from the stored results as usual.
        using var third = engine.NextScan(changed, new ScanQuery(ScanValueType.UInt32, ScanCompare.Exact,
            new ScanPattern(BitConverter.GetBytes(9u))));
        Assert.Equal([0x1004UL], third.Addresses);
    }

    [Fact]
    public void Unknown_initial_millions_of_results_on_disk_then_narrowed()
    {
        // 32 MB of non-zero memory: 8,388,608 candidates.
        const int size = 32 * 1024 * 1024;
        var mem = new DenseProcessMemory(0x2_0000_0000, size);
        for (var i = 0; i < size; i += 4) BinaryPrimitives.WriteUInt32LittleEndian(mem.Data.AsSpan(i), (uint)i | 1);
        using var storage = new ScanStorage(_root);
        var engine = new ScanEngine(mem, PS4MemSettings.Default, storage: storage);

        using var first = engine.FirstScan(Pid, [Sec("anon:heap", mem.Base, size)], Unknown(), new ScanOptions { Method = ScanMethod.Console });

        Assert.Equal(size / 4, first.Count);
        Assert.True(first.OnDisk);
        Assert.Equal(mem.Base + size - 4, first.AddressAt(first.Count - 1));
        Assert.Equal(mem.Base + 0x40_0000, first.AddressAt(0x10_0000));
        Assert.Equal((uint)(size - 4) | 1, BinaryPrimitives.ReadUInt32LittleEndian(first.ValueAt(first.Count - 1)));

        foreach (var offset in new[] { 4, 0x123_4568, size - 8 }) mem.Data[offset] ^= 0x80;
        using var next = engine.NextScan(first, Next(ScanCompare.Changed));
        Assert.Equal(new ulong[] { 4, 0x123_4568, (ulong)size - 8 }.Select(o => mem.Base + o), next.Addresses);
        Assert.Equal(0, mem.WriteCount);
    }

    [Fact]
    public void A_cancelled_snapshot_leaves_no_file()
    {
        var mem = RandomMemory(0x10_0000, 0x4_0000);
        using var storage = new ScanStorage(_root);
        var engine = new ScanEngine(mem, PS4MemSettings.Default with { ScanChunkBytes = 4096 }, storage: storage);
        using var cts = new CancellationTokenSource();
        mem.OnRead = address => { if (address >= 0x12_0000) cts.Cancel(); };

        Assert.ThrowsAny<OperationCanceledException>(() =>
            engine.FirstScan(Pid, [Sec("a", 0x10_0000, 0x4_0000)], Unknown(), cancel: cts.Token));
        Assert.Empty(FilesIn(storage));
    }

    [Fact]
    public void Undo_goes_back_one_scan_and_frees_the_newer_results()
    {
        var mem = new FakeProcessMemory();
        mem.SetU32(0x1000, 5);
        mem.SetU32(0x1004, 5);
        var engine = new ScanEngine(mem);
        var section = Sec("a", 0x1000, 0x10);
        using var history = new ScanHistory(undoLevels: 1);

        var first = engine.FirstScan(Pid, [section], 5);
        history.Push(first);
        Assert.False(history.CanUndo);

        mem.SetU32(0x1004, 6);
        var second = engine.NextScan(first, 5);
        history.Push(second);
        Assert.Same(second, history.Current);
        Assert.True(history.CanUndo);

        Assert.True(history.Undo());
        Assert.Same(first, history.Current);
        Assert.True(second.IsDisposed);
        Assert.False(first.IsDisposed);
        Assert.Equal([0x1000UL, 0x1004], first.Addresses);   // the previous results are intact
        Assert.False(history.Undo());

        // A different next scan from the restored results.
        var other = engine.NextScan(first, 6);
        history.Push(other);
        Assert.Equal([0x1004UL], history.Current!.Addresses);
    }

    [Fact]
    public void History_keeps_only_the_configured_levels_and_a_first_scan_clears_it()
    {
        var mem = new FakeProcessMemory();
        mem.SetU32(0x1000, 5);
        var engine = new ScanEngine(mem);
        var section = Sec("a", 0x1000, 0x10);
        using var history = new ScanHistory(undoLevels: 2);

        var s1 = engine.FirstScan(Pid, [section], 5);
        history.Push(s1);
        var s2 = engine.NextScan(s1, 5);
        history.Push(s2);
        var s3 = engine.NextScan(s2, 5);
        history.Push(s3);
        var s4 = engine.NextScan(s3, 5);
        history.Push(s4);

        Assert.Equal(2, history.UndoCount);
        Assert.True(s1.IsDisposed);                 // past the 2 levels
        Assert.False(s2.IsDisposed);

        var fresh = engine.FirstScan(Pid, [section], 5);
        history.Push(fresh);                        // new first scan: everything else is freed
        Assert.Equal(0, history.UndoCount);
        Assert.All(new[] { s2, s3, s4 }, r => Assert.True(r.IsDisposed));

        history.Clear();
        Assert.Null(history.Current);
        Assert.True(fresh.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => engine.NextScan(fresh, 5));
    }

    [Fact]
    public void Undo_after_a_spilled_next_scan_deletes_its_files_and_keeps_the_snapshot()
    {
        var mem = RandomMemory(0x10_0000, 0x4_0000);
        using var storage = new ScanStorage(_root);
        var engine = new ScanEngine(mem, PS4MemSettings.Default with { ScanSpillRows = 100 }, storage: storage);
        using var history = new ScanHistory();

        var first = engine.FirstScan(Pid, [Sec("a", 0x10_0000, 0x4_0000)], Unknown());
        history.Push(first);
        history.Push(engine.NextScan(first, Next(ScanCompare.Unchanged)));
        Assert.True(history.Current!.OnDisk);
        Assert.Equal(3, FilesIn(storage).Length);   // snapshot + addresses + values

        history.Undo();
        Assert.Single(FilesIn(storage));
        Assert.Same(first, history.Current);
        Assert.Equal(first.Count, history.NextCount(engine));
    }
}

internal static class HistoryTestExtensions
{
    /// <summary>Runs an unchanged next scan from the current results and returns its count.</summary>
    public static long NextCount(this ScanHistory history, ScanEngine engine)
    {
        using var r = engine.NextScan(history.Current!, new ScanQuery(history.Current!.Type, ScanCompare.Unchanged));
        return r.Count;
    }
}
