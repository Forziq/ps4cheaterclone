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
/// <item>First scan: each section goes through a back end (<see cref="LocalSectionScanner"/>
/// by default; the console scanner when asked and it supports the query, else local).</item>
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
    private readonly PS4MemSettings _settings;
    private readonly DebugLog _log;
    private readonly ISectionScanner _local;
    private readonly ISectionScanner? _console;

    public ScanEngine(IProcessMemory memory, PS4MemSettings? settings = null, DebugLog? log = null,
        ISectionScanner? consoleScanner = null)
    {
        _memory = memory;
        _settings = settings ?? PS4MemSettings.Default;
        _log = log ?? DebugLog.Disabled;
        _local = new LocalSectionScanner(memory, _settings);
        _console = consoleScanner;
    }

    public bool HasConsoleScanner => _console is not null;

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

        var scanner = _local;
        if (options.Method == ScanMethod.Console)
        {
            scanner = _console ?? throw new InvalidOperationException("Console scan isn't available on this connection.");
            if (!scanner.Supports(query))
            {
                _log.Warn(Cat, $"Console scan can't run '{query}'; scanning locally instead");
                scanner = _local;
            }
        }
        var alignment = EffectiveAlignment(query.Type, options.Alignment ?? _settings.ScanAlignment);

        var list = sections.OrderBy(s => s.Start).ToList();
        var totalBytes = list.Aggregate(0L, (sum, s) => sum + (long)s.Size);
        _log.Info(Cat, $"First scan ({scanner.Name}): pid {pid}, {query}, " +
                       $"alignment {alignment}, {list.Count} sections, {totalBytes} bytes");

        var watch = Stopwatch.StartNew();
        var hits = new ScanHitBuffer(size);
        var stats = new List<SectionScanStats>(list.Count);
        long done = 0;
        try
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
        catch (OperationCanceledException)
        {
            _log.Warn(Cat, $"First scan cancelled after {done}/{totalBytes} bytes, {hits.Count} hits so far");
            throw;
        }

        return Finish(pid, 1, scanner.Name, query, hits, stats, watch.Elapsed);
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
        if (!query.Compare.AllowedOnNextScan())
            throw new ArgumentException($"'{query.Compare.ShortName()}' is for a first scan only.");
        if (query.Type != previous.Type)
            throw new ArgumentException($"The previous scan was {previous.Type.ShortName()}, not {query.Type.ShortName()}: " +
                                        "start a new scan to change the type.");
        var size = query.ValueSize ?? previous.ValueSize;
        var matcher = ValueMatcher.Create(query, size, _settings.UnknownInitialSkipZeros);

        var pid = previous.Pid;
        var maxBatch = (ulong)Math.Max(size, _settings.ScanChunkBytes);
        var maxGap = (ulong)Math.Max(0, _settings.NextScanMaxGapBytes);
        _log.Info(Cat, $"Next scan #{previous.ScanNumber + 1}: pid {pid}, {query}, " +
                       $"{previous.Count} previous results in {previous.Sections.Count(s => s.Hits > 0)} sections");

        var watch = Stopwatch.StartNew();
        var hits = new ScanHitBuffer(size, Math.Max(16, previous.Count / 4));
        var stats = new List<SectionScanStats>(previous.Sections.Count);
        var addresses = previous.Addresses;
        var buffer = Array.Empty<byte>();
        long checkedCount = 0, reads = 0;
        try
        {
            foreach (var old in previous.Sections)
            {
                var first = hits.Count;
                var sectionWatch = Stopwatch.StartNew();
                var end = old.FirstResult + old.Hits;
                for (var i = old.FirstResult; i < end;)
                {
                    cancel.ThrowIfCancellationRequested();

                    // Grow the batch while the next hit is close and the read stays small enough.
                    var batchStart = addresses[i];
                    var batchEnd = batchStart + (ulong)size;
                    var j = i + 1;
                    // Addresses are sorted; unaligned hits can overlap the batch end (gap 0).
                    while (j < end
                           && (addresses[j] > batchEnd ? addresses[j] - batchEnd : 0) <= maxGap
                           && addresses[j] + (ulong)size - batchStart <= maxBatch)
                    {
                        batchEnd = Math.Max(batchEnd, addresses[j] + (ulong)size);
                        j++;
                    }

                    var length = (int)(batchEnd - batchStart);
                    if (buffer.Length < length) buffer = new byte[Math.Max(length, 4096)];
                    _memory.ReadMemory(pid, batchStart, buffer.AsMemory(0, length), cancel);
                    reads++;

                    for (var k = i; k < j; k++)
                    {
                        var current = buffer.AsSpan((int)(addresses[k] - batchStart), size);
                        // A string / byte array exact scan may change the length; then the old value isn't used.
                        var before = previous.ValueSize == size ? previous.ValueAt(k) : default;
                        if (matcher.IsMatch(current, before))
                            hits.Add(addresses[k], current);
                    }

                    checkedCount += j - i;
                    i = j;
                    options.Progress?.Invoke(new ScanProgress(old.Section.Name, checkedCount, previous.Count, hits.Count));
                }
                stats.Add(SectionFinished(new SectionScanStats(old.Section, first, hits.Count - first, sectionWatch.Elapsed), options));
            }
        }
        catch (OperationCanceledException)
        {
            _log.Warn(Cat, $"Next scan cancelled after {checkedCount}/{previous.Count} results, {hits.Count} hits so far");
            throw;
        }

        _log.Info(Cat, $"Next scan read {checkedCount} results in {reads} reads");
        return Finish(pid, previous.ScanNumber + 1, "local", query, hits, stats, watch.Elapsed);
    }

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
        var (addresses, values) = hits.ToArrays();
        _log.Info(Cat, $"Scan #{number} ({method}) done: {addresses.Length} results in " +
                       $"{stats.Count(s => s.Hits > 0)}/{stats.Count} sections, {elapsed.TotalMilliseconds:0} ms");
        return new ScanResults
        {
            Pid = pid,
            ScanNumber = number,
            Method = method,
            Query = query,
            ValueSize = hits.ValueSize,
            Addresses = addresses,
            Values = values,
            Sections = stats,
            Elapsed = elapsed,
        };
    }
}
