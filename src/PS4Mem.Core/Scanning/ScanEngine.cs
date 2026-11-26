using System.Diagnostics;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Sections;

namespace PS4Mem.Core.Scanning;

public enum ScanMethod
{
    /// <summary>Read memory to the Mac and compare here (default, the reference).</summary>
    Local,
    /// <summary>Compare on the PS4 (ps4debug-NG Turbo Scan or the scan trio).</summary>
    Console,
}

public sealed record ScanOptions
{
    public ScanMethod Method { get; init; } = ScanMethod.Local;

    /// <summary>Scan step in bytes; null uses <see cref="PS4MemSettings.ScanAlignment"/>.</summary>
    public int? Alignment { get; init; }

    /// <summary>Called after each chunk (first scan) or batch (next scan). Runs on the scan thread.</summary>
    public Action<ScanProgress>? Progress { get; init; }

    /// <summary>Called when a section is finished, with its hit count and time.</summary>
    public Action<SectionScanStats>? SectionDone { get; init; }
}

/// <summary>
/// First scan and next scan for every value type and compare (<see cref="ScanQuery"/>).
/// <list type="bullet">
/// <item>First scan: local by default: every section is read in <see cref="PS4MemSettings.ScanChunkBytes"/>
/// chunks with all read connections at once (<see cref="ParallelReader"/>) and compared here, in
/// address order, so results don't depend on how many connections read. The console scanner
/// runs instead when asked and it supports the query.</item>
/// <item>Next scan: re-reads the previous hits on the Mac, batching nearby addresses into one read,
/// and keeps the ones that match, comparing with the typed value or the previous value.</item>
/// </list>
/// Alignment: <see cref="ScanOptions.Alignment"/> (or <see cref="PS4MemSettings.ScanAlignment"/>),
/// capped by the type like PS4Cheater: u8, strings and byte arrays 1, 16-bit 2, others 4.
/// Every section's name, start, size, hits and time goes to the debug log (category <c>scan</c>)
/// and to <see cref="ScanOptions.SectionDone"/>, so a bad scan is obvious (PROMPT §3.4).
/// No timeouts; cancel with the token (throws <see cref="OperationCanceledException"/>).
/// </summary>
public sealed class ScanEngine
{
    private const string Cat = "scan";

    private readonly IProcessMemory _memory;
    private readonly IReadOnlyList<IProcessMemory> _readers;
    private readonly PS4MemSettings _settings;
    private readonly DebugLog _log;
    private readonly ISectionScanner? _console;
    private readonly ScanStorage? _storage;

    /// <param name="storage">Where big result sets spill to disk; null keeps everything in memory.</param>
    /// <param name="readers">
    /// Connections that local scans read with, in parallel (<see cref="ParallelReader"/>); null or
    /// empty reads with <paramref name="memory"/> only. Results are the same either way.
    /// </param>
    public ScanEngine(IProcessMemory memory, PS4MemSettings? settings = null, DebugLog? log = null,
        ISectionScanner? consoleScanner = null, ScanStorage? storage = null,
        IReadOnlyList<IProcessMemory>? readers = null)
    {
        _memory = memory;
        _readers = readers is { Count: > 0 } ? readers : [memory];
        _settings = settings ?? PS4MemSettings.Default;
        _log = log ?? DebugLog.Disabled;
        _console = consoleScanner;
        _storage = storage;
    }

    public bool HasConsoleScanner => _console is not null;

    /// <summary>How many connections local scans read with.</summary>
    public int ReaderCount => _readers.Count;

    /// <summary>The scan step for <paramref name="type"/>: the alignment option capped by the type's own step.</summary>
    public static int EffectiveAlignment(ScanValueType type, int alignment)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(alignment, 1);
        var step = type.FixedSize() is { } size ? Math.Min(size, 4) : 1;
        return Math.Min(alignment, step);
    }

    /// <summary>Exact u32 first scan (the Phase 0 scan).</summary>
    public ScanResults FirstScan(int pid, IEnumerable<Section> sections, uint value,
        ScanOptions? options = null, CancellationToken cancel = default) =>
        FirstScan(pid, sections, ScanQuery.ExactUInt32(value), options, cancel);

    /// <summary>Scans <paramref name="sections"/> (usually the selected ones) for <paramref name="query"/>.</summary>
    public ScanResults FirstScan(int pid, IEnumerable<Section> sections, ScanQuery query,
        ScanOptions? options = null, CancellationToken cancel = default)
    {
        options ??= new ScanOptions();
        if (!query.Compare.AllowedOnFirstScan())
            throw new ArgumentException($"'{query.Compare.ShortName()}' needs a previous scan: use it for a next scan.");
        var size = query.ValueSize ?? throw new ArgumentException($"'{query}' has no value size.");
        if (query.Compare == ScanCompare.UnknownInitial)
            return SnapshotScan(pid, sections, query, options, cancel);

        ISectionScanner? scanner = null; // null = local
        if (options.Method == ScanMethod.Console)
        {
            scanner = _console ?? throw new InvalidOperationException("Console scan isn't available on this connection.");
            if (!scanner.Supports(query))
            {
                _log.Warn(Cat, $"Console scan can't run '{query}'; scanning locally instead");
                scanner = null;
            }
        }
        var method = scanner?.Name ?? "local";
        var alignment = EffectiveAlignment(query.Type, options.Alignment ?? _settings.ScanAlignment);

        var list = sections.OrderBy(s => s.Start).ToList();
        var totalBytes = list.Aggregate(0L, (sum, s) => sum + (long)s.Size);
        _log.Info(Cat, $"First scan ({method}): pid {pid}, {query}, " +
                       $"alignment {alignment}, {list.Count} sections, {totalBytes} bytes" +
                       (scanner is null ? $", {_readers.Count} read connection(s)" : ""));

        var watch = Stopwatch.StartNew();
        using var hits = NewBuffer(size);
        var stats = new List<SectionScanStats>(list.Count);
        long done = 0;
        try
        {
            if (scanner is null)
            {
                // Local: every section's chunks through all readers, compared here in address order.
                var matcher = ValueMatcher.Create(query, size, _settings.UnknownInitialSkipZeros);
                long first = 0;
                ReadSections(pid, list, size, cancel,
                    begin: _ => first = hits.Count,
                    chunk: (s, data, own, address) =>
                    {
                        matcher.FindAll(data, own, address, alignment, hits);
                        done += own;
                        options.Progress?.Invoke(new ScanProgress(list[s].Name, done, totalBytes, hits.Count));
                    },
                    end: (s, elapsed) => stats.Add(SectionFinished(
                        new SectionScanStats(list[s], first, hits.Count - first, elapsed), options)));
            }
            else
            {
                foreach (var section in list)
                {
                    var first = hits.Count;
                    var sectionWatch = Stopwatch.StartNew();
                    scanner.ScanSection(pid, section, query, alignment, hits, bytes =>
                    {
                        done += bytes;
                        options.Progress?.Invoke(new ScanProgress(section.Name, done, totalBytes, hits.Count));
                    }, cancel);
                    stats.Add(SectionFinished(new SectionScanStats(section, first, hits.Count - first, sectionWatch.Elapsed), options));
                }
            }
        }
        catch (OperationCanceledException)
        {
            _log.Warn(Cat, $"First scan cancelled after {done}/{totalBytes} bytes, {hits.Count} hits so far");
            throw;
        }

        return Finish(pid, 1, method, query, hits, stats, watch.Elapsed);
    }

    /// <summary>Exact u32 next scan (the Phase 0 scan).</summary>
    public ScanResults NextScan(ScanResults previous, uint value,
        ScanOptions? options = null, CancellationToken cancel = default) =>
        NextScan(previous, ScanQuery.ExactUInt32(value), options, cancel);

    /// <summary>
    /// Re-reads every previous hit and keeps those that match <paramref name="query"/>.
    /// The type must be the previous scan's. Hits in the same section that are at most
    /// <see cref="PS4MemSettings.NextScanMaxGapBytes"/> apart share one read of at most
    /// <see cref="PS4MemSettings.ScanChunkBytes"/>.
    /// </summary>
    public ScanResults NextScan(ScanResults previous, ScanQuery query,
        ScanOptions? options = null, CancellationToken cancel = default)
    {
        options ??= new ScanOptions();
        ObjectDisposedException.ThrowIf(previous.IsDisposed, previous);
        if (!query.Compare.AllowedOnNextScan())
            throw new ArgumentException($"'{query.Compare.ShortName()}' is for a first scan only.");
        if (query.Type != previous.Type)
            throw new ArgumentException($"The previous scan was {previous.Type.ShortName()}, not {query.Type.ShortName()}: " +
                                        "start a new scan to change the type.");
        var size = query.ValueSize ?? previous.ValueSize;
        var matcher = ValueMatcher.Create(query, size, _settings.UnknownInitialSkipZeros);
        if (previous.Columns is SnapshotHitColumns snapshot)
            return NextFromSnapshot(previous, snapshot, query, matcher, options, cancel);

        var pid = previous.Pid;
        var maxBatch = (ulong)Math.Max(size, _settings.ScanChunkBytes);
        var maxGap = (ulong)Math.Max(0, _settings.NextScanMaxGapBytes);
        _log.Info(Cat, $"Next scan #{previous.ScanNumber + 1}: pid {pid}, {query}, " +
                       $"{previous.Count} previous results in {previous.Sections.Count(s => s.Hits > 0)} sections");

        var watch = Stopwatch.StartNew();
        using var hits = NewBuffer(size, (int)Math.Min(int.MaxValue / 16, Math.Max(16, previous.Count / 4)));
        var stats = new List<SectionScanStats>(previous.Sections.Count);
        long checkedCount = 0, reads = 0, bytes = 0;
        var sections = previous.Sections;
        var readWatch = Stopwatch.StartNew();
        try
        {
            long first = 0;
            var current = -1;
            var sectionWatch = Stopwatch.StartNew();
            void EndSection()
            {
                if (current < 0) return;
                stats.Add(SectionFinished(new SectionScanStats(sections[current].Section, first, hits.Count - first,
                    sectionWatch.Elapsed), options));
            }

            ParallelReader.Run(_readers, pid, Batches(previous, size, maxGap, maxBatch),
                b => new ReadRange(b.Start, b.Length),
                (b, data) =>
                {
                    cancel.ThrowIfCancellationRequested();
                    if (b.Section != current)
                    {
                        EndSection();
                        sectionWatch.Restart();
                        current = b.Section;
                        first = hits.Count;
                    }
                    if (b.Length == 0) return; // a section without previous hits
                    for (var k = b.First; k < b.End; k++)
                    {
                        var address = previous.AddressAt(k);
                        var value = data.Slice((int)(address - b.Start), size);
                        // A string / byte array exact scan may change the length; then the old value isn't used.
                        var before = previous.ValueSize == size ? previous.ValueAt(k) : default;
                        if (matcher.IsMatch(value, before))
                            hits.Add(address, value);
                    }
                    reads++;
                    bytes += b.Length;
                    checkedCount += b.End - b.First;
                    options.Progress?.Invoke(new ScanProgress(sections[b.Section].Section.Name, checkedCount, previous.Count, hits.Count));
                }, cancel);
            EndSection();
        }
        catch (OperationCanceledException)
        {
            _log.Warn(Cat, $"Next scan cancelled after {checkedCount}/{previous.Count} results, {hits.Count} hits so far");
            throw;
        }

        _log.Info(Cat, $"Next scan read {checkedCount} results in {reads} reads");
        LogRate(bytes, readWatch.Elapsed);
        return Finish(pid, previous.ScanNumber + 1, "local", query, hits, stats, watch.Elapsed);
    }

    /// <summary>One next-scan read: results <c>[First, End)</c> of a section, <c>Length</c> bytes at <c>Start</c>.</summary>
    private readonly record struct Batch(int Section, long First, long End, ulong Start, int Length);

    /// <summary>
    /// Groups each section's previous hits into reads: the batch grows while the next hit is at most
    /// <paramref name="maxGap"/> bytes away and the read stays within <paramref name="maxBatch"/>.
    /// A section without hits is one empty batch, so it still gets its stats row.
    /// </summary>
    private static IEnumerable<Batch> Batches(ScanResults previous, int size, ulong maxGap, ulong maxBatch)
    {
        for (var s = 0; s < previous.Sections.Count; s++)
        {
            var old = previous.Sections[s];
            var end = old.FirstResult + old.Hits;
            if (old.Hits == 0)
            {
                yield return new Batch(s, end, end, old.Section.Start, 0);
                continue;
            }
            for (var i = old.FirstResult; i < end;)
            {
                var batchStart = previous.AddressAt(i);
                var batchEnd = batchStart + (ulong)size;
                var j = i + 1;
                // Addresses are sorted; unaligned hits can overlap the batch end (gap 0).
                while (j < end)
                {
                    var next = previous.AddressAt(j);
                    if ((next > batchEnd ? next - batchEnd : 0) > maxGap || next + (ulong)size - batchStart > maxBatch)
                        break;
                    batchEnd = Math.Max(batchEnd, next + (ulong)size);
                    j++;
                }
                yield return new Batch(s, i, j, batchStart, (int)(batchEnd - batchStart));
                i = j;
            }
        }
    }

    /// <summary>
    /// Unknown initial value: reads every section to the Mac (never on the console: the values
    /// themselves are the result) and keeps the bytes as a snapshot, on disk with a storage.
    /// Results are the candidate values in it; nothing is stored per result.
    /// </summary>
    private ScanResults SnapshotScan(int pid, IEnumerable<Section> sections, ScanQuery query,
        ScanOptions options, CancellationToken cancel)
    {
        if (options.Method == ScanMethod.Console)
            _log.Info(Cat, "Unknown initial value always reads memory to the Mac (snapshot); not using console scan");
        var alignment = EffectiveAlignment(query.Type, options.Alignment ?? _settings.ScanAlignment);
        var candidate = ValueMatcher.Create(query, query.ValueSize!.Value, _settings.UnknownInitialSkipZeros);
        var size = candidate.Size;
        var list = sections.OrderBy(s => s.Start).ToList();
        var totalBytes = list.Aggregate(0L, (sum, s) => sum + (long)s.Size);
        _log.Info(Cat, $"First scan (snapshot): pid {pid}, {query}, alignment {alignment}, {list.Count} sections, " +
                       $"{totalBytes} bytes, skip zeros {_settings.UnknownInitialSkipZeros}, " +
                       (_storage is null ? "in memory" : $"to {_storage.SessionDirectory}"));

        var watch = Stopwatch.StartNew();
        using var builder = new SnapshotBuilder(_storage, totalBytes, alignment, candidate);
        var stats = new List<SectionScanStats>(list.Count);
        long done = 0, count = 0;
        try
        {
            long first = 0;
            ReadSections(pid, list, size, cancel,
                begin: s =>
                {
                    first = count;
                    builder.BeginSection(list[s]);
                },
                chunk: (s, data, own, address) =>
                {
                    count += builder.AddChunk(data, own, address);
                    done += own;
                    options.Progress?.Invoke(new ScanProgress(list[s].Name, done, totalBytes, count));
                },
                end: (s, elapsed) => stats.Add(SectionFinished(
                    new SectionScanStats(list[s], first, count - first, elapsed), options)));
        }
        catch (OperationCanceledException)
        {
            _log.Warn(Cat, $"Snapshot cancelled after {done}/{totalBytes} bytes");
            throw;
        }

        var columns = builder.Complete();
        _log.Info(Cat, $"Scan #1 (snapshot) done: {columns.Count} results in {stats.Count(s => s.Hits > 0)}/{stats.Count} " +
                       $"sections, {watch.Elapsed.TotalMilliseconds:0} ms{(columns.OnDisk ? " (snapshot on disk)" : "")}");
        return new ScanResults(columns)
        {
            Pid = pid,
            ScanNumber = 1,
            Method = "local",
            Query = query,
            Sections = stats,
            Elapsed = watch.Elapsed,
        };
    }

    /// <summary>
    /// Next scan after unknown initial value: reads each section again and compares every
    /// candidate with its snapshot bytes. Progress is in bytes.
    /// </summary>
    private ScanResults NextFromSnapshot(ScanResults previous, SnapshotHitColumns snapshot, ScanQuery query,
        ValueMatcher matcher, ScanOptions options, CancellationToken cancel)
    {
        var pid = previous.Pid;
        var size = snapshot.ValueSize;
        var alignment = snapshot.Alignment;
        var totalBytes = snapshot.Sections.Aggregate(0L, (sum, s) => sum + (long)s.Section.Size);
        _log.Info(Cat, $"Next scan #{previous.ScanNumber + 1}: pid {pid}, {query}, against the snapshot of " +
                       $"{snapshot.Sections.Count} sections ({previous.Count} candidates, {totalBytes} bytes)");

        var watch = Stopwatch.StartNew();
        using var hits = NewBuffer(size);
        var stats = new List<SectionScanStats>(snapshot.Sections.Count);
        long done = 0;
        try
        {
            var sections = snapshot.Sections;
            long first = 0;
            ReadSections(pid, sections.Select(x => x.Section).ToList(), size, cancel,
                begin: _ => first = hits.Count,
                chunk: (i, data, own, address) =>
                {
                    var s = sections[i];
                    var offset = (long)(address - s.Section.Start);
                    var start = (int)((ulong)(alignment - (int)(address % (ulong)alignment)) % (ulong)alignment);
                    for (var o = start; o < own && o + size <= data.Length; o += alignment)
                    {
                        var before = snapshot.Data.Slice(s.DataOffset + offset + o, size);
                        if (!snapshot.Candidate.IsMatch(before, default)) continue;
                        var current = data.Slice(o, size);
                        if (matcher.IsMatch(current, before))
                            hits.Add(address + (ulong)o, current);
                    }
                    done += own;
                    options.Progress?.Invoke(new ScanProgress(s.Section.Name, done, totalBytes, hits.Count));
                },
                end: (i, elapsed) => stats.Add(SectionFinished(
                    new SectionScanStats(sections[i].Section, first, hits.Count - first, elapsed), options)));
        }
        catch (OperationCanceledException)
        {
            _log.Warn(Cat, $"Next scan cancelled after {done}/{totalBytes} bytes, {hits.Count} hits so far");
            throw;
        }
        return Finish(pid, previous.ScanNumber + 1, "local", query, hits, stats, watch.Elapsed);
    }

    private delegate void SectionChunk(int section, ReadOnlySpan<byte> data, int own, ulong address);

    /// <summary>One read of a section: <c>Own</c> bytes it owns plus the overlap, <c>Read</c> in all.</summary>
    private readonly record struct Chunk(int Section, ulong Address, int Own, int Read);

    /// <summary>
    /// Every section in <see cref="PS4MemSettings.ScanChunkBytes"/> chunks, each with
    /// (<paramref name="valueSize"/> - 1) extra bytes clamped to the section end, so a value on a
    /// chunk edge is complete. An empty section is one empty chunk, so it still gets begin and end.
    /// </summary>
    private static IEnumerable<Chunk> Chunks(IReadOnlyList<Section> sections, int chunkBytes, int valueSize)
    {
        for (var s = 0; s < sections.Count; s++)
        {
            var section = sections[s];
            if (section.End <= section.Start)
            {
                yield return new Chunk(s, section.Start, 0, 0);
                continue;
            }
            for (var pos = section.Start; pos < section.End;)
            {
                var left = section.End - pos;
                var own = (int)Math.Min((ulong)chunkBytes, left);
                var read = (int)Math.Min((ulong)own + (ulong)valueSize - 1, left);
                yield return new Chunk(s, pos, own, read);
                pos += (ulong)own;
            }
        }
    }

    /// <summary>
    /// Reads <paramref name="sections"/> with every reader at once (<see cref="ParallelReader"/>).
    /// <paramref name="chunk"/> gets the chunks in address order on this thread; <paramref name="begin"/>
    /// runs before a section's first chunk and <paramref name="end"/> after its last, with the time
    /// since the previous section ended. Logs the read rate.
    /// </summary>
    private void ReadSections(int pid, IReadOnlyList<Section> sections, int valueSize, CancellationToken cancel,
        Action<int> begin, SectionChunk chunk, Action<int, TimeSpan> end)
    {
        var chunkBytes = Math.Max(valueSize, _settings.ScanChunkBytes);
        var watch = Stopwatch.StartNew();
        var sectionWatch = Stopwatch.StartNew();
        var current = -1;
        long bytes = 0;
        ParallelReader.Run(_readers, pid, Chunks(sections, chunkBytes, valueSize),
            c => new ReadRange(c.Address, c.Read),
            (c, data) =>
            {
                if (c.Section != current)
                {
                    if (current >= 0) end(current, sectionWatch.Elapsed);
                    sectionWatch.Restart();
                    current = c.Section;
                    begin(current);
                }
                if (c.Read > 0) chunk(c.Section, data, c.Own, c.Address);
                bytes += c.Own;
            }, cancel);
        if (current >= 0) end(current, sectionWatch.Elapsed);
        LogRate(bytes, watch.Elapsed);
    }

    private void LogRate(long bytes, TimeSpan elapsed)
    {
        var seconds = Math.Max(elapsed.TotalSeconds, 0.001);
        _log.Info(Cat, $"Read {bytes / 1048576.0:0.0} MB in {seconds:0.0} s ({bytes / 1048576.0 / seconds:0.0} MB/s) " +
                       $"with {_readers.Count} connection(s)");
    }

    private ScanHitBuffer NewBuffer(int valueSize, int capacity = 1024) =>
        new(valueSize, capacity, _storage, _settings.ScanSpillRows);

    private SectionScanStats SectionFinished(SectionScanStats s, ScanOptions options)
    {
        _log.Info(Cat, $"section '{s.Section.Name}' start 0x{s.Section.Start:X} size 0x{s.Section.Size:X}: " +
                       $"{s.Hits} hits, {s.Elapsed.TotalMilliseconds:0} ms");
        options.SectionDone?.Invoke(s);
        return s;
    }

    private ScanResults Finish(int pid, int number, string method, ScanQuery query, ScanHitBuffer hits,
        List<SectionScanStats> stats, TimeSpan elapsed)
    {
        var spilled = hits.Spilled;
        var columns = hits.Complete();
        _log.Info(Cat, $"Scan #{number} ({method}) done: {columns.Count} results in " +
                       $"{stats.Count(s => s.Hits > 0)}/{stats.Count} sections, {elapsed.TotalMilliseconds:0} ms" +
                       (spilled ? " (stored on disk)" : ""));
        return new ScanResults(columns)
        {
            Pid = pid,
            ScanNumber = number,
            Method = method,
            Query = query,
            Sections = stats,
            Elapsed = elapsed,
        };
    }
}
