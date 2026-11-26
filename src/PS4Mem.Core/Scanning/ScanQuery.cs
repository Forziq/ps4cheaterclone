using System.Buffers.Binary;

namespace PS4Mem.Core.Scanning;

/// <summary>
/// One scan request: value type, compare and the typed value(s), already parsed into bytes.
/// The constructor checks that they fit together and throws <see cref="ArgumentException"/>
/// with a message that can be shown as is. Whether it may run as a first or next scan is
/// checked by <see cref="ScanEngine"/>.
/// </summary>
public sealed record ScanQuery
{
    public ScanQuery(ScanValueType type, ScanCompare compare, ScanPattern? value = null, ScanPattern? value2 = null)
    {
        var what = $"{compare.ShortName()} ({type.ShortName()})";
        if (!compare.AllowedFor(type))
            throw new ArgumentException(compare == ScanCompare.UnknownInitial
                ? $"Unknown initial value needs a number type, not {type.ShortName()}."
                : $"{type.ShortName()} values have no order: use exact, changed or unchanged, not {compare.ShortName()}.");

        var count = (value is null ? 0 : 1) + (value2 is null ? 0 : 1);
        if (value2 is not null && value is null || count != compare.ValueCount())
            throw new ArgumentException(compare.ValueCount() switch
            {
                0 => $"{what} takes no value.",
                1 => $"{what} takes one value.",
                _ => $"{what} takes two values.",
            });

        if (type.FixedSize() is { } size)
            foreach (var v in new[] { value, value2 })
                if (v is not null && (v.Length != size || v.HasWildcards))
                    throw new ArgumentException($"A {type.ShortName()} value is {size} bytes, not {v.Length}.");

        Type = type;
        Compare = compare;
        Value = value;
        Value2 = value2;
    }

    public ScanValueType Type { get; }
    public ScanCompare Compare { get; }
    public ScanPattern? Value { get; }
    public ScanPattern? Value2 { get; }

    /// <summary>
    /// Bytes per value: the type's size, or the typed string / byte array length. Null for a
    /// string or byte array changed/unchanged scan, which takes the previous scan's length.
    /// </summary>
    public int? ValueSize => Type.FixedSize() ?? Value?.Length;

    /// <summary>Parses the typed text (see <see cref="ValueParser"/>) and builds the query.</summary>
    public static ScanQuery Parse(ScanValueType type, ScanCompare compare, string? value = null,
        string? value2 = null, bool hex = false)
    {
        var count = compare.ValueCount();
        var first = count >= 1 ? ValueParser.Parse(type, value, hex) : null;
        var second = count >= 2 ? ValueParser.Parse(type, value2, hex) : null;
        return new ScanQuery(type, compare, first, second);
    }

    /// <summary>The Phase 0 scan: exact 4-byte unsigned value.</summary>
    public static ScanQuery ExactUInt32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return new ScanQuery(ScanValueType.UInt32, ScanCompare.Exact, new ScanPattern(bytes));
    }

    /// <summary>For logs: "exact u32 123456", "between f32 1.5 and 2", "increased i16".</summary>
    public override string ToString()
    {
        var text = $"{Compare.ShortName()} {Type.ShortName()}";
        if (Value is not null) text += " " + Show(Value);
        if (Value2 is not null) text += " and " + Show(Value2);
        return text;
    }

    private string Show(ScanPattern p) => Type == ScanValueType.ByteArray && p.Wildcards is { } w
        ? string.Join(' ', p.Bytes.Select((b, i) => w[i] ? "??" : b.ToString("X2")))
        : Type == ScanValueType.String
            ? $"\"{ValueFormatter.Format(Type, p.Bytes)}\""
            : ValueFormatter.Format(Type, p.Bytes);
}
