using PS4Mem.Core.Sections;

namespace PS4Mem.Core.Scanning;

/// <summary>
/// Hits of one section inside <see cref="ScanResults"/>: results
/// <c>[FirstResult, FirstResult + Hits)</c> belong to <see cref="Section"/>.
/// </summary>
public sealed record SectionScanStats(Section Section, long FirstResult, long Hits, TimeSpan Elapsed);

/// <summary>
/// Result of a first or next scan, stored compactly as an address column and a value column
/// with <see cref="ValueSize"/> bytes per result (PROMPT §5 "store results compactly"). Big
/// result sets live in files (<see cref="OnDisk"/>, see <see cref="ScanHitBuffer"/>).
/// Results are sorted by address, grouped by section. Values are the bytes read during the scan.
/// Dispose to free the memory and delete the files (new scan, undo, exit).
/// </summary>
public sealed class ScanResults : IDisposable
{
    private readonly HitColumns _hits;
    private bool _disposed;

    internal ScanResults(HitColumns hits) => _hits = hits;

    public required int Pid { get; init; }

    /// <summary>1 for the first scan, 2 for the first next scan, and so on.</summary>
    public required int ScanNumber { get; init; }

    /// <summary>"local" or "console" (first scan); next scans are always "local" re-reads.</summary>
    public required string Method { get; init; }

    /// <summary>The scan that produced these results.</summary>
    public required ScanQuery Query { get; init; }

    public ScanValueType Type => Query.Type;

    /// <summary>Bytes per value: the type's size, or the string / byte array length.</summary>
    public int ValueSize => _hits.ValueSize;

    /// <summary>Every scanned section, in address order, including the ones with 0 hits.</summary>
    public required IReadOnlyList<SectionScanStats> Sections { get; init; }

    public required TimeSpan Elapsed { get; init; }

    public long Count => _hits.Count;

    /// <summary>True when the results are in files rather than in memory.</summary>
    public bool OnDisk => _hits.OnDisk;

    public bool IsDisposed => _disposed;

    public ulong AddressAt(long index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _hits.AddressAt(index);
    }

    /// <summary>The bytes of result <paramref name="index"/> as found by the scan.</summary>
    public ReadOnlySpan<byte> ValueAt(long index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _hits.ValueAt(index);
    }

    /// <summary>Every result's address, in order (lazy, so it works for files too).</summary>
    public IEnumerable<ulong> Addresses
    {
        get
        {
            for (long i = 0; i < Count; i++)
                yield return AddressAt(i);
        }
    }

    /// <summary>Result <paramref name="index"/>'s value as text (see <see cref="ValueFormatter"/>).</summary>
    public string FormatValue(long index, bool hex = false) => ValueFormatter.Format(Type, ValueAt(index), hex);

    /// <summary>The section that result <paramref name="index"/> belongs to.</summary>
    public Section SectionOf(long index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        int lo = 0, hi = Sections.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var s = Sections[mid];
            if (index < s.FirstResult) hi = mid - 1;
            else if (index >= s.FirstResult + s.Hits) lo = mid + 1;
            else return s.Section;
        }
        throw new InvalidOperationException("Results and section ranges disagree.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hits.Dispose();
    }
}

/// <summary>
/// Progress of a running scan. First scan: <see cref="Done"/>/<see cref="Total"/> are bytes;
/// next scan: previous results checked.
/// </summary>
public sealed record ScanProgress(string Section, long Done, long Total, long Hits)
{
    public double Fraction => Total <= 0 ? 1 : Math.Clamp((double)Done / Total, 0, 1);
}
