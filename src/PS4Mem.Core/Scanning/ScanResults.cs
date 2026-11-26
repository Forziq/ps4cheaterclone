using PS4Mem.Core.Sections;

namespace PS4Mem.Core.Scanning;

/// <summary>
/// Hits of one section inside <see cref="ScanResults"/>: results
/// <c>[FirstResult, FirstResult + Hits)</c> belong to <see cref="Section"/>.
/// </summary>
public sealed record SectionScanStats(Section Section, int FirstResult, int Hits, TimeSpan Elapsed);

/// <summary>
/// Result of a first or next scan, stored compactly: one address array and one value array
/// (PROMPT §5 "store results compactly"). Results are sorted by address, grouped by section.
/// </summary>
public sealed class ScanResults
{
    public required int Pid { get; init; }

    /// <summary>1 for the first scan, 2 for the first next scan, and so on.</summary>
    public required int ScanNumber { get; init; }

    /// <summary>"local" or "console" (first scan); next scans are always "local" re-reads.</summary>
    public required string Method { get; init; }

    public required ulong[] Addresses { get; init; }
    public required uint[] Values { get; init; }

    /// <summary>Every scanned section, in address order, including the ones with 0 hits.</summary>
    public required IReadOnlyList<SectionScanStats> Sections { get; init; }

    public required TimeSpan Elapsed { get; init; }

    public int Count => Addresses.Length;

    /// <summary>The section that result <paramref name="index"/> belongs to.</summary>
    public Section SectionOf(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        foreach (var s in Sections)
            if (index < s.FirstResult + s.Hits)
                return s.Section;
        throw new InvalidOperationException("Results and section ranges disagree.");
    }
}

/// <summary>Growable address + value arrays used while a scan runs.</summary>
public sealed class ScanHitBuffer
{
    private ulong[] _addresses;
    private uint[] _values;

    public ScanHitBuffer(int capacity = 1024)
    {
        _addresses = new ulong[Math.Max(16, capacity)];
        _values = new uint[_addresses.Length];
    }

    public int Count { get; private set; }

    public void Add(ulong address, uint value)
    {
        if (Count == _addresses.Length)
        {
            var size = checked(_addresses.Length * 2);
            Array.Resize(ref _addresses, size);
            Array.Resize(ref _values, size);
        }
        _addresses[Count] = address;
        _values[Count] = value;
        Count++;
    }

    public ulong AddressAt(int index) => _addresses[index];

    public (ulong[] Addresses, uint[] Values) ToArrays() => (_addresses[..Count], _values[..Count]);
}

/// <summary>
/// Progress of a running scan. First scan: <see cref="Done"/>/<see cref="Total"/> are bytes;
/// next scan: previous results checked.
/// </summary>
public sealed record ScanProgress(string Section, long Done, long Total, int Hits)
{
    public double Fraction => Total <= 0 ? 1 : Math.Clamp((double)Done / Total, 0, 1);
}
