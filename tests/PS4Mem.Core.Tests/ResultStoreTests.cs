using System.Buffers.Binary;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Scanning;
using PS4Mem.Core.Sections;
using PS4Mem.Core.Tests.Fakes;

namespace PS4Mem.Core.Tests;

/// <summary>Result store (T1.4 m1): compact columns, spilling to disk, cleanup.</summary>
public sealed class ResultStoreTests : IDisposable
{
    private const int Pid = 77;
    private const uint Money = 123_456;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ps4mem-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static Section Sec(string name, ulong start, ulong size) =>
        new(new MemoryMap(name, start, start + size, 0, 3), true, "test");

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }

    private static string[] FilesIn(ScanStorage storage) => Directory.GetFiles(storage.SessionDirectory);

    [Fact]
    public void Without_a_storage_hits_stay_in_memory()
    {
        using var hits = new ScanHitBuffer(4, capacity: 16, storage: null, spillRows: 10);
        for (var i = 0u; i < 100; i++) hits.Add(i * 4, U32(i));

        Assert.False(hits.Spilled);
        using var results = Results(hits);
        Assert.False(results.OnDisk);
        Assert.Equal(100, results.Count);
        Assert.Equal(396UL, results.AddressAt(99));
    }

    [Fact]
    public void Hits_above_the_threshold_spill_to_files_and_read_back()
    {
        using var storage = new ScanStorage(_root);
        var hits = new ScanHitBuffer(4, capacity: 16, storage, spillRows: 1000);
        for (var i = 0u; i < 1000; i++) hits.Add(0x1000 + i * 4, U32(i));
        Assert.False(hits.Spilled);                       // exactly at the threshold: still in memory
        Assert.Empty(FilesIn(storage));

        for (var i = 1000u; i < 200_000; i++) hits.Add(0x1000 + i * 4, U32(i));
        Assert.True(hits.Spilled);
        Assert.Equal(2, FilesIn(storage).Length);         // addresses + values
        Assert.Equal(200_000, hits.Count);

        var results = Results(hits);
        Assert.True(results.OnDisk);
        Assert.Equal(200_000, results.Count);
        foreach (var i in new long[] { 0, 999, 1000, 65_535, 131_072, 199_999 })
        {
            Assert.Equal(0x1000UL + (ulong)i * 4, results.AddressAt(i));
            Assert.Equal(U32((uint)i), results.ValueAt(i).ToArray());
        }
        Assert.Equal(200_000, results.Addresses.Count());
        Assert.Throws<ArgumentOutOfRangeException>(() => results.AddressAt(200_000));

        results.Dispose();
        Assert.Empty(FilesIn(storage));                   // undo / new scan frees the disk space
        Assert.Throws<ObjectDisposedException>(() => results.AddressAt(0));
    }

    [Fact]
    public void An_unfinished_buffer_deletes_its_files_when_disposed()
    {
        using var storage = new ScanStorage(_root);
        var hits = new ScanHitBuffer(8, storage: storage, spillRows: 10);
        for (var i = 0u; i < 100; i++) hits.Add(i * 8, new byte[8]);
        Assert.NotEmpty(FilesIn(storage));

        hits.Dispose();
        Assert.Empty(FilesIn(storage));
    }

    [Fact]
    public void Only_hits_still_in_memory_can_be_sorted()
    {
        using var storage = new ScanStorage(_root);
        using var hits = new ScanHitBuffer(1, capacity: 16, storage, spillRows: 16);
        for (var i = 0; i < 20; i++) hits.Add((ulong)(100 - i), [(byte)i]);   // 16 spilled, 4 in memory
        Assert.Equal(16, hits.SpilledCount);

        Assert.Throws<InvalidOperationException>(() => hits.SortFrom(0));
        hits.SortFrom(16);
        Assert.Equal([81UL, 82, 83, 84], Enumerable.Range(16, 4).Select(i => hits.AddressAt(i)));
        Assert.Equal([19, 18, 17, 16], Enumerable.Range(16, 4).Select(i => hits.ValueAt(i)[0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => hits.AddressAt(3));  // on disk
    }

    [Fact]
    public void Storage_uses_its_own_folder_and_deletes_it_and_dead_runs_leftovers()
    {
        // A folder from a run whose process is gone, and one from this process (another storage).
        var dead = Path.Combine(_root, $"session-{int.MaxValue}-old");
        Directory.CreateDirectory(dead);
        File.WriteAllText(Path.Combine(dead, "values-000001.bin"), "x");
        using var other = new ScanStorage(_root);

        var storage = new ScanStorage(_root);
        Assert.False(Directory.Exists(dead));
        Assert.True(Directory.Exists(other.SessionDirectory));
        Assert.StartsWith(Path.Combine(_root, $"session-{Environment.ProcessId}-"), storage.SessionDirectory);
        Assert.NotEqual(storage.NewFile("values"), storage.NewFile("values"));

        File.WriteAllText(storage.NewFile("values"), "x");
        storage.Dispose();
        Assert.False(Directory.Exists(storage.SessionDirectory));
        Assert.Throws<ObjectDisposedException>(() => storage.NewFile("values"));
    }

    [Fact]
    public void Empty_folder_setting_means_application_support()
    {
        Assert.EndsWith(Path.Combine("Library", "Application Support", "PS4Mem", "scans"), ScanStorage.DefaultRoot);
        Assert.Equal("", PS4MemSettings.Default.ScanDataDirectory);
        Assert.Equal(2_000_000, PS4MemSettings.Default.ScanSpillRows);
    }

    [Fact]
    public void A_scan_with_millions_of_results_spills_and_next_scan_narrows_it()
    {
        // 16 MB where every aligned u32 is the value: 4,194,304 hits (over the default 2M threshold).
        const int size = 16 * 1024 * 1024;
        var mem = new DenseProcessMemory(0x1_0000_0000, size);
        for (var i = 0; i < size; i += 4) BinaryPrimitives.WriteUInt32LittleEndian(mem.Data.AsSpan(i), Money);
        using var storage = new ScanStorage(_root);
        var engine = new ScanEngine(mem, PS4MemSettings.Default, storage: storage);
        var a = Sec("anon:a", mem.Base, size / 2);
        var b = Sec("anon:b", mem.Base + size / 2, size / 2);

        var first = engine.FirstScan(Pid, [a, b], Money);

        Assert.Equal(size / 4, first.Count);
        Assert.True(first.OnDisk);
        Assert.Equal(mem.Base, first.AddressAt(0));
        Assert.Equal(mem.Base + size - 4, first.AddressAt(first.Count - 1));
        Assert.Equal(U32(Money), first.ValueAt(first.Count - 1).ToArray());
        Assert.Equal([size / 8L, size / 8L], first.Sections.Select(s => s.Hits));
        Assert.Equal("anon:a", first.SectionOf(size / 8 - 1).Name);
        Assert.Equal("anon:b", first.SectionOf(size / 8).Name);

        // The value changes in three places; the next scan keeps those, in memory.
        var changed = new[] { 0, size / 2, size - 4 };
        foreach (var offset in changed) BinaryPrimitives.WriteUInt32LittleEndian(mem.Data.AsSpan(offset), Money + 1);
        var next = engine.NextScan(first, Money + 1);

        Assert.Equal(changed.Select(o => mem.Base + (ulong)o), next.Addresses);
        Assert.False(next.OnDisk);
        Assert.Equal([1L, 2L], next.Sections.Select(s => s.Hits));
        Assert.Equal(0, mem.WriteCount);

        first.Dispose();
        next.Dispose();
        Assert.Empty(FilesIn(storage));
    }

    [Fact]
    public void A_cancelled_scan_leaves_no_files()
    {
        const int size = 4 * 1024 * 1024;
        var mem = new DenseProcessMemory(0x10_0000, size);
        using var storage = new ScanStorage(_root);
        var settings = PS4MemSettings.Default with { ScanSpillRows = 1000, ScanChunkBytes = 64 * 1024, UnknownInitialSkipZeros = false };
        var engine = new ScanEngine(mem, settings, storage: storage);
        using var cts = new CancellationTokenSource();
        mem.OnRead = address => { if (address >= mem.Base + size / 2) cts.Cancel(); };

        Assert.ThrowsAny<OperationCanceledException>(() => engine.FirstScan(Pid, [Sec("anon:a", mem.Base, size)],
            new ScanQuery(ScanValueType.UInt32, ScanCompare.Between, new ScanPattern(U32(0)), new ScanPattern(U32(10))),
            cancel: cts.Token));
        Assert.Empty(FilesIn(storage));
    }

    private static ScanResults Results(ScanHitBuffer hits) => new(hits.Complete())
    {
        Pid = Pid,
        ScanNumber = 1,
        Method = "local",
        Query = ScanQuery.ExactUInt32(0),
        Sections = [],
        Elapsed = TimeSpan.Zero,
    };
}
