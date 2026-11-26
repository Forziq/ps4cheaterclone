using System.Numerics;
using System.Runtime.InteropServices;

namespace PS4Mem.Core.Scanning;

/// <summary>
/// Decides whether a value in memory matches a <see cref="ScanQuery"/>. Used by LocalScan
/// (<see cref="FindAll"/> over a chunk) and by next scans (<see cref="IsMatch"/> per result).
/// Semantics follow PS4Cheater (docs/research/value-compare-types.md):
/// <list type="bullet">
/// <item>Float/double exact, increased by and decreased by match within <see cref="FloatTolerance"/>
/// (PS4Cheater MemoryHelper.cs:728); bigger, smaller, between, increased, decreased are plain
/// IEEE compares, so NaN never matches them. Changed/unchanged compare values, so NaN counts as changed.</item>
/// <item>Integer increased by / decreased by are exact and wrap around (u8 255 + 1 = 0).</item>
/// <item>Between includes both ends; the two values may be typed in either order.</item>
/// <item>Unknown initial value keeps every value, or every non-zero one when
/// <c>skipZeros</c> is set (PS4Cheater keeps only non-zero ones).</item>
/// </list>
/// </summary>
public abstract class ValueMatcher
{
    /// <summary>PS4Cheater's float/double equality: <c>Math.Abs(a - b) &lt; 0.0001</c>.</summary>
    public const double FloatTolerance = 0.0001;

    protected ValueMatcher(int size) => Size = size;

    /// <summary>Bytes per value.</summary>
    public int Size { get; }

    /// <summary>
    /// True when <paramref name="current"/> matches. <paramref name="previous"/> is the value from
    /// the previous scan (same size); it's only looked at by compares that need it.
    /// </summary>
    public abstract bool IsMatch(ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous);

    /// <summary>
    /// Adds every match that starts in the first <paramref name="own"/> bytes of <paramref name="data"/>
    /// at an address that's a multiple of <paramref name="alignment"/>. <paramref name="data"/> holds
    /// <see cref="Size"/> - 1 bytes more than <paramref name="own"/> when the section goes on, so a
    /// value on a chunk edge is complete; values that don't fit before the end are skipped.
    /// </summary>
    public virtual void FindAll(ReadOnlySpan<byte> data, int own, ulong baseAddress, int alignment, ScanHitBuffer hits)
    {
        var last = Math.Min(own, data.Length - Size + 1); // starts must be below this
        for (var offset = FirstAligned(baseAddress, alignment); offset < last; offset += alignment)
        {
            var value = data.Slice(offset, Size);
            if (IsMatch(value, default))
                hits.Add(baseAddress + (ulong)offset, value);
        }
    }

    /// <summary>Offset of the first address at or after <paramref name="baseAddress"/> that's aligned.</summary>
    protected static int FirstAligned(ulong baseAddress, int alignment) =>
        (int)((ulong)(alignment - (int)(baseAddress % (ulong)alignment)) % (ulong)alignment);

    /// <summary>
    /// The matcher for <paramref name="query"/>; <paramref name="size"/> is the value size
    /// (the query's, or the previous scan's for a string/byte array changed or unchanged scan).
    /// </summary>
    public static ValueMatcher Create(ScanQuery query, int size, bool skipZeros = false)
    {
        if (!BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("Scanning needs a little-endian Mac, like the PS4.");

        // Integers, strings and byte arrays: exact is a byte compare, done with a vectorised search.
        if (query.Compare == ScanCompare.Exact && !query.Type.IsFloat())
            return new ExactBytesMatcher(query.Value!);
        if (!query.Type.IsNumber())
            return new BytesChangeMatcher(size, query.Compare); // changed / unchanged

        return query.Type switch
        {
            ScanValueType.UInt8 => new NumberMatcher<byte>(query, skipZeros),
            ScanValueType.Int8 => new NumberMatcher<sbyte>(query, skipZeros),
            ScanValueType.UInt16 => new NumberMatcher<ushort>(query, skipZeros),
            ScanValueType.Int16 => new NumberMatcher<short>(query, skipZeros),
            ScanValueType.UInt32 => new NumberMatcher<uint>(query, skipZeros),
            ScanValueType.Int32 => new NumberMatcher<int>(query, skipZeros),
            ScanValueType.UInt64 => new NumberMatcher<ulong>(query, skipZeros),
            ScanValueType.Int64 => new NumberMatcher<long>(query, skipZeros),
            ScanValueType.Float => new NumberMatcher<float>(query, skipZeros),
            ScanValueType.Double => new NumberMatcher<double>(query, skipZeros),
            _ => throw new ArgumentOutOfRangeException(nameof(query), query.Type, "Unknown value type."),
        };
    }
}

/// <summary>Exact match of a byte pattern (integers, strings, byte arrays with <c>??</c> wildcards).</summary>
internal sealed class ExactBytesMatcher : ValueMatcher
{
    private readonly ScanPattern _pattern;
    private readonly byte[] _anchor;     // the first run of non-wildcard bytes, searched for first
    private readonly int _anchorOffset;  // where that run starts in the pattern

    public ExactBytesMatcher(ScanPattern pattern) : base(pattern.Length)
    {
        _pattern = pattern;
        var w = pattern.Wildcards;
        var start = 0;
        while (w is not null && w[start]) start++;
        var end = start;
        while (end < pattern.Length && (w is null || !w[end])) end++;
        _anchorOffset = start;
        _anchor = pattern.Bytes[start..end];
    }

    public override bool IsMatch(ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous) => _pattern.Matches(current);

    public override void FindAll(ReadOnlySpan<byte> data, int own, ulong baseAddress, int alignment, ScanHitBuffer hits)
    {
        var from = 0; // lowest start still to check
        while (from < own)
        {
            var search = from + _anchorOffset;
            if (search >= data.Length) return;
            var found = data[search..].IndexOf(_anchor); // vectorised
            if (found < 0) return;
            var start = search + found - _anchorOffset;
            if (start >= own) return; // starts in the next chunk; that chunk reports it
            if (start + Size <= data.Length
                && (baseAddress + (ulong)start) % (ulong)alignment == 0
                && _pattern.Matches(data[start..]))
                hits.Add(baseAddress + (ulong)start, data.Slice(start, Size));
            from = start + 1;
        }
    }
}

/// <summary>Changed / unchanged for strings and byte arrays: a byte compare with the previous value.</summary>
internal sealed class BytesChangeMatcher(int size, ScanCompare compare) : ValueMatcher(size)
{
    public override bool IsMatch(ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous) =>
        current.SequenceEqual(previous) == (compare == ScanCompare.Unchanged);
}

/// <summary>Every compare for one number type. Generic so each type gets its own fast code.</summary>
internal sealed class NumberMatcher<T> : ValueMatcher where T : unmanaged, INumber<T>
{
    private static readonly bool IsFloat = typeof(T) == typeof(float) || typeof(T) == typeof(double);
    private static readonly T Tolerance = T.CreateChecked(FloatTolerance);

    private readonly ScanCompare _compare;
    private readonly bool _skipZeros;
    private readonly T _a, _b;

    public NumberMatcher(ScanQuery query, bool skipZeros) : base(query.ValueSize!.Value)
    {
        _compare = query.Compare;
        _skipZeros = skipZeros;
        if (query.Value is not null) _a = Read(query.Value.Bytes);
        if (query.Value2 is not null) _b = Read(query.Value2.Bytes);
        if (_compare == ScanCompare.Between && _a > _b) (_a, _b) = (_b, _a); // either order
    }

    private static T Read(ReadOnlySpan<byte> bytes) => MemoryMarshal.Read<T>(bytes); // host is little-endian

    /// <summary>Equal for integers; within <see cref="ValueMatcher.FloatTolerance"/> for floats.</summary>
    private static bool Same(T x, T y) => IsFloat ? T.Abs(x - y) < Tolerance : x == y;

    public override bool IsMatch(ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous)
    {
        var v = Read(current);
        return _compare switch
        {
            ScanCompare.Exact => Same(v, _a),
            ScanCompare.BiggerThan => v > _a,
            ScanCompare.SmallerThan => v < _a,
            ScanCompare.Between => v >= _a && v <= _b,
            ScanCompare.Increased => v > Read(previous),
            ScanCompare.IncreasedBy => Same(v, unchecked(Read(previous) + _a)),
            ScanCompare.Decreased => v < Read(previous),
            ScanCompare.DecreasedBy => Same(v, unchecked(Read(previous) - _a)),
            ScanCompare.Changed => v != Read(previous),
            ScanCompare.Unchanged => v == Read(previous),
            ScanCompare.UnknownInitial => !_skipZeros || v != T.Zero,
            _ => throw new InvalidOperationException($"Unknown compare {_compare}."),
        };
    }
}
