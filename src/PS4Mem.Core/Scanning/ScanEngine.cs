using System.Buffers.Binary;
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
/// First scan and next scan for exact 4-byte values (T0.4; more types come in T1.3).
/// <list type="bullet">
/// <item>First scan: each section goes through a back end (<see cref="LocalSectionScanner"/>
/// by default, the console scanner when asked).</item>
/// <item>Next scan: re-reads the previous hits on the Mac, batching nearby addresses into one read,
/// and keeps the ones that now equal the value.</item>
/// </list>
/// Every section's name, start, size, hits and time goes to the debug log (category <c>scan</c>)
/// and to <see cref="ScanOptions.SectionDone"/>, so a bad scan is obvious (PROMPT §3.4).
/// No timeouts; cancel with the token (throws <see cref="OperationCanceledException"/>).
/// </summary>
public sealed class ScanEngine
{
    private const string Cat = "scan";
    private const int ValueSize = 4;

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

    /// <summary>Scans <paramref name="sections"/> (usually the selected ones) for <paramref name="value"/>.</summary>
    public ScanResults FirstScan(int pid, IEnumerable<Section> sections, uint value,
        ScanOptions? options = null, CancellationToken cancel = default)
    {
        options ??= new ScanOptions();
        var scanner = options.Method == ScanMethod.Console
            ? _console ?? throw new InvalidOperationException("Console scan isn't available on this connection.")
            : _local;
        var alignment = options.Alignment ?? _settings.ScanAlignment;
        ArgumentOutOfRangeException.ThrowIfLessThan(alignment, 1);

        var list = sections.OrderBy(s => s.Start).ToList();
        var totalBytes = list.Aggregate(0L, (sum, s) => sum + (long)s.Size);
        _log.Info(Cat, $"First scan ({scanner.Name}): pid {pid}, exact u32 {value} (0x{value:X8}), " +
                       $"alignment {alignment}, {list.Count} sections, {totalBytes} bytes");

        var watch = Stopwatch.StartNew();
        var hits = new ScanHitBuffer();
        var stats = new List<SectionScanStats>(list.Count);
        long done = 0;
        try
        {
            foreach (var section in list)
            {
                var first = hits.Count;
                var sectionWatch = Stopwatch.StartNew();
                scanner.ScanSection(pid, section, value, alignment, hits, bytes =>
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

        return Finish(pid, 1, scanner.Name, hits, stats, watch.Elapsed);
    }

    /// <summary>
    /// Re-reads every previous hit and keeps those whose u32 now equals <paramref name="value"/>.
    /// Hits in the same section that are at most <see cref="PS4MemSettings.NextScanMaxGapBytes"/>
    /// apart share one read of at most <see cref="PS4MemSettings.ScanChunkBytes"/>.
    /// </summary>
    public ScanResults NextScan(ScanResults previous, uint value,
        ScanOptions? options = null, CancellationToken cancel = default)
    {
        options ??= new ScanOptions();
        var pid = previous.Pid;
        var maxBatch = (ulong)Math.Max(ValueSize, _settings.ScanChunkBytes);
        var maxGap = (ulong)Math.Max(0, _settings.NextScanMaxGapBytes);
        _log.Info(Cat, $"Next scan #{previous.ScanNumber + 1}: pid {pid}, exact u32 {value} (0x{value:X8}), " +
                       $"{previous.Count} previous results in {previous.Sections.Count(s => s.Hits > 0)} sections");

        var watch = Stopwatch.StartNew();
        var hits = new ScanHitBuffer(Math.Max(16, previous.Count / 4));
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
                    var batchEnd = batchStart + ValueSize;
                    var j = i + 1;
                    // Addresses are sorted; unaligned hits can overlap the batch end (gap 0).
                    while (j < end
                           && (addresses[j] > batchEnd ? addresses[j] - batchEnd : 0) <= maxGap
                           && addresses[j] + ValueSize - batchStart <= maxBatch)
                    {
                        batchEnd = Math.Max(batchEnd, addresses[j] + ValueSize);
                        j++;
                    }

                    var length = (int)(batchEnd - batchStart);
                    if (buffer.Length < length) buffer = new byte[Math.Max(length, 4096)];
                    _memory.ReadMemory(pid, batchStart, buffer.AsMemory(0, length), cancel);
                    reads++;

                    for (var k = i; k < j; k++)
                    {
                        var offset = (int)(addresses[k] - batchStart);
                        var current = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, ValueSize));
                        if (current == value)
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
        return Finish(pid, previous.ScanNumber + 1, "local", hits, stats, watch.Elapsed);
    }

    private SectionScanStats SectionFinished(SectionScanStats s, ScanOptions options)
    {
        _log.Info(Cat, $"section '{s.Section.Name}' start 0x{s.Section.Start:X} size 0x{s.Section.Size:X}: " +
                       $"{s.Hits} hits, {s.Elapsed.TotalMilliseconds:0} ms");
        options.SectionDone?.Invoke(s);
        return s;
    }

    private ScanResults Finish(int pid, int number, string method, ScanHitBuffer hits,
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
            Addresses = addresses,
            Values = values,
            Sections = stats,
            Elapsed = elapsed,
        };
    }
}
