using PS4Mem.Core.Sections;

namespace PS4Mem.Core.Scanning;

/// <summary>
/// Hits of one section inside <see cref="ScanResults"/>: results
/// <c>[FirstResult, FirstResult + Hits)</c> belong to <see cref="Section"/>.
/// </summary>
public sealed record SectionScanStats(Section Section, int FirstResult, int Hits, TimeSpan Elapsed);

/// <summary>
/// Result of a first or next scan, stored compactly: one address array and one value byte array
/// with <see cref="ValueSize"/> bytes per result (PROMPT §5 "store results compactly").
/// Results are sorted by address, grouped by section. Values are the bytes read during the scan.
/// </summary>
public sealed class ScanResults
{
    public required int Pid { get; init; }

    /// <summary>1 for the first scan, 2 for the first next scan, and so on.</summary>
    public required int ScanNumber { get; init; }

    /// <summary>"local" or "console" (first scan); next scans are always "local" re-reads.</summary>
    public required string Method { get; init; }

    /// <summary>The scan that produced these results.</summary>
    public required ScanQuery Query { get; init; }

    public ScanValueType Type => Query.Type;

    /// <summary>Bytes per value: the type's size, or the string / byte array length.</summary>
    public required int ValueSize { get; init; }

    public required ulong[] Addresses { get; init; }

    /// <summary>Result <c>i</c>'s value is <c>Values[i * ValueSize .. (i + 1) * ValueSize]</c>.</summary>
    public required byte[] Values { get; init; }

    /// <summary>Every scanned section, in address order, including the ones with 0 hits.</summary>
    public required IReadOnlyList<SectionScanStats> Sections { get; init; }

    public required TimeSpan Elapsed { get; init; }

    public int Count => Addresses.Length;

    /// <summary>The bytes of result <paramref name="index"/> as found by the scan.</summary>
    public ReadOnlySpan<byte> ValueAt(int index) => Values.AsSpan(index * ValueSize, ValueSize);

    /// <summary>Result <paramref name="index"/>'s value as text (see <see cref="ValueFormatter"/>).</summary>
    public string FormatValue(int index, bool hex = false) => ValueFormatter.Format(Type, ValueAt(index), hex);

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

/// <summary>Growable address + value arrays used while a scan runs; every value is <see cref="ValueSize"/> bytes.</summary>
public sealed class ScanHitBuffer
{
    private ulong[] _addresses;
    private byte[] _values;

    public ScanHitBuffer(int valueSize, int capacity = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(valueSize, 1);
        ValueSize = valueSize;
        _addresses = new ulong[Math.Max(16, capacity)];
        _values = new byte[checked(_addresses.Length * valueSize)];
    }

    public int ValueSize { get; }

    public int Count { get; private set; }

    /// <summary>Adds a hit; <paramref name="value"/> must be <see cref="ValueSize"/> bytes.</summary>
    public void Add(ulong address, ReadOnlySpan<byte> value)
    {
        if (value.Length != ValueSize)
            throw new ArgumentException($"Value is {value.Length} bytes, expected {ValueSize}.", nameof(value));
        if (Count == _addresses.Length)
        {
            var size = checked(_addresses.Length * 2);
            Array.Resize(ref _addresses, size);
            Array.Resize(ref _values, checked(size * ValueSize));
        }
        _addresses[Count] = address;
        value.CopyTo(_values.AsSpan(Count * ValueSize));
        Count++;
    }

    public ulong AddressAt(int index) => _addresses[index];

    public ReadOnlySpan<byte> ValueAt(int index) => _values.AsSpan(index * ValueSize, ValueSize);

    /// <summary>Sorts hits <c>[start, Count)</c> by address (for back ends that may not report in order).</summary>
    public void SortFrom(int start)
    {
        var sorted = true;
        for (var i = start + 1; i < Count && sorted; i++)
            sorted = _addresses[i] >= _addresses[i - 1];
        if (sorted) return;

        var n = Count - start;
        var keys = _addresses[start..Count];
        var order = Enumerable.Range(0, n).ToArray();
        Array.Sort(keys, order);
        var oldValues = _values.AsSpan(start * ValueSize, n * ValueSize).ToArray();
        for (var i = 0; i < n; i++)
            oldValues.AsSpan(order[i] * ValueSize, ValueSize).CopyTo(_values.AsSpan((start + i) * ValueSize));
        keys.CopyTo(_addresses, start);
    }

    public (ulong[] Addresses, byte[] Values) ToArrays() => (_addresses[..Count], _values[..(Count * ValueSize)]);
}

/// <summary>
/// Progress of a running scan. First scan: <see cref="Done"/>/<see cref="Total"/> are bytes;
/// next scan: previous results checked.
/// </summary>
public sealed record ScanProgress(string Section, long Done, long Total, int Hits)
{
    public double Fraction => Total <= 0 ? 1 : Math.Clamp((double)Done / Total, 0, 1);
}
