using PS4Mem.Core.Scanning;

namespace PS4Mem.Core.Tests;

/// <summary>
/// Compare semantics for every value type × compare (T1.3 "done when").
/// Values are typed as text and parsed, so the same table runs for every number type.
/// </summary>
public class ValueMatcherTests
{
    private static readonly ScanValueType[] NumberTypes = Enum.GetValues<ScanValueType>().Where(t => t.IsNumber()).ToArray();

    private static byte[] Bytes(ScanValueType type, string text) =>
        ValueParser.Parse(type, text, hex: text.StartsWith("0x")).Bytes;

    private static bool Match(ScanValueType type, ScanCompare compare, string? v1, string? v2, string current,
        string? previous = null, bool skipZeros = false)
    {
        var query = ScanQuery.Parse(type, compare, v1, v2);
        var matcher = ValueMatcher.Create(query, type.FixedSize()!.Value, skipZeros);
        return matcher.IsMatch(Bytes(type, current), previous is null ? default : Bytes(type, previous));
    }

    // compare, value 1, value 2, current, previous, expected. Small positive numbers fit every type.
    private static readonly (ScanCompare C, string? V1, string? V2, string Cur, string? Prev, bool Expected)[] Table =
    [
        (ScanCompare.Exact, "10", null, "10", null, true),
        (ScanCompare.Exact, "10", null, "11", null, false),
        (ScanCompare.Exact, "10", null, "9", null, false),
        (ScanCompare.BiggerThan, "10", null, "11", null, true),
        (ScanCompare.BiggerThan, "10", null, "10", null, false),
        (ScanCompare.BiggerThan, "10", null, "9", null, false),
        (ScanCompare.SmallerThan, "10", null, "9", null, true),
        (ScanCompare.SmallerThan, "10", null, "10", null, false),
        (ScanCompare.SmallerThan, "10", null, "11", null, false),
        (ScanCompare.Between, "5", "20", "5", null, true),     // both ends included
        (ScanCompare.Between, "5", "20", "20", null, true),
        (ScanCompare.Between, "5", "20", "12", null, true),
        (ScanCompare.Between, "5", "20", "4", null, false),
        (ScanCompare.Between, "5", "20", "21", null, false),
        (ScanCompare.Between, "20", "5", "12", null, true),    // either order
        (ScanCompare.Increased, null, null, "11", "10", true),
        (ScanCompare.Increased, null, null, "10", "10", false),
        (ScanCompare.Increased, null, null, "9", "10", false),
        (ScanCompare.IncreasedBy, "5", null, "15", "10", true),
        (ScanCompare.IncreasedBy, "5", null, "14", "10", false),
        (ScanCompare.IncreasedBy, "5", null, "16", "10", false),
        (ScanCompare.Decreased, null, null, "9", "10", true),
        (ScanCompare.Decreased, null, null, "10", "10", false),
        (ScanCompare.Decreased, null, null, "11", "10", false),
        (ScanCompare.DecreasedBy, "3", null, "7", "10", true),
        (ScanCompare.DecreasedBy, "3", null, "8", "10", false),
        (ScanCompare.Changed, null, null, "11", "10", true),
        (ScanCompare.Changed, null, null, "10", "10", false),
        (ScanCompare.Unchanged, null, null, "10", "10", true),
        (ScanCompare.Unchanged, null, null, "11", "10", false),
        (ScanCompare.UnknownInitial, null, null, "10", null, true),
        (ScanCompare.UnknownInitial, null, null, "0", null, true), // zeros kept unless skipZeros
    ];

    public static TheoryData<ScanValueType, ScanCompare, string?, string?, string, string?, bool> EveryTypeAndCompare()
    {
        var data = new TheoryData<ScanValueType, ScanCompare, string?, string?, string, string?, bool>();
        foreach (var type in NumberTypes)
            foreach (var row in Table)
                data.Add(type, row.C, row.V1, row.V2, row.Cur, row.Prev, row.Expected);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryTypeAndCompare))]
    public void Every_number_type_supports_every_compare(ScanValueType type, ScanCompare compare,
        string? v1, string? v2, string current, string? previous, bool expected)
    {
        Assert.Equal(expected, Match(type, compare, v1, v2, current, previous));
    }

    [Fact]
    public void The_table_covers_every_compare()
    {
        Assert.Equal(Enum.GetValues<ScanCompare>().Order(), Table.Select(r => r.C).Distinct().Order());
    }

    [Theory]
    [MemberData(nameof(NumberTypesData))]
    public void Unknown_initial_skips_zeros_when_asked(ScanValueType type)
    {
        Assert.False(Match(type, ScanCompare.UnknownInitial, null, null, "0", skipZeros: true));
        Assert.True(Match(type, ScanCompare.UnknownInitial, null, null, "3", skipZeros: true));
    }

    public static TheoryData<ScanValueType> NumberTypesData()
    {
        var data = new TheoryData<ScanValueType>();
        foreach (var t in NumberTypes) data.Add(t);
        return data;
    }

    [Theory]
    [InlineData(ScanValueType.Int8, ScanCompare.Between, "-10", "-1", "-5", true)]
    [InlineData(ScanValueType.Int8, ScanCompare.BiggerThan, "-1", null, "0", true)]
    [InlineData(ScanValueType.Int16, ScanCompare.SmallerThan, "0", null, "-300", true)]
    [InlineData(ScanValueType.Int32, ScanCompare.Exact, "-123456", null, "-123456", true)]
    [InlineData(ScanValueType.Int64, ScanCompare.Between, "-5", "5", "-6", false)]
    [InlineData(ScanValueType.UInt32, ScanCompare.BiggerThan, "0", null, "4294967295", true)]   // unsigned order
    [InlineData(ScanValueType.Int32, ScanCompare.BiggerThan, "0", null, "0xFFFFFFFF", false)]   // -1 as i32
    [InlineData(ScanValueType.UInt8, ScanCompare.SmallerThan, "128", null, "0xFF", false)]
    [InlineData(ScanValueType.Int8, ScanCompare.SmallerThan, "0", null, "0xFF", true)]          // -1 as i8
    public void Signed_and_unsigned_order(ScanValueType type, ScanCompare compare, string v1, string? v2,
        string current, bool expected)
    {
        Assert.Equal(expected, Match(type, compare, v1, v2, current));
    }

    [Theory]
    [InlineData(ScanValueType.UInt8, ScanCompare.IncreasedBy, "1", "0", "255")]
    [InlineData(ScanValueType.Int8, ScanCompare.IncreasedBy, "1", "-128", "127")]
    [InlineData(ScanValueType.UInt16, ScanCompare.IncreasedBy, "10", "4", "65530")]
    [InlineData(ScanValueType.UInt32, ScanCompare.DecreasedBy, "1", "4294967295", "0")]
    [InlineData(ScanValueType.Int32, ScanCompare.DecreasedBy, "1", "2147483647", "-2147483648")]
    [InlineData(ScanValueType.UInt64, ScanCompare.IncreasedBy, "2", "1", "18446744073709551615")]
    [InlineData(ScanValueType.Int16, ScanCompare.DecreasedBy, "-5", "15", "10")]   // decreased by a negative
    public void Integer_increased_and_decreased_by_wrap_around(ScanValueType type, ScanCompare compare,
        string by, string current, string previous)
    {
        Assert.True(Match(type, compare, by, null, current, previous));
    }

    [Theory]
    [InlineData(ScanValueType.Float, ScanCompare.Exact, "1.5", "1.50005", null, true)]   // within 0.0001
    [InlineData(ScanValueType.Float, ScanCompare.Exact, "1.5", "1.49995", null, true)]
    [InlineData(ScanValueType.Float, ScanCompare.Exact, "1.5", "1.5002", null, false)]
    [InlineData(ScanValueType.Double, ScanCompare.Exact, "0.1", "0.10005", null, true)]
    [InlineData(ScanValueType.Double, ScanCompare.Exact, "0.1", "0.1002", null, false)]
    [InlineData(ScanValueType.Float, ScanCompare.IncreasedBy, "0.1", "1.1", "1", true)]   // 1f + 0.1f != 1.1f in bits
    [InlineData(ScanValueType.Double, ScanCompare.IncreasedBy, "0.1", "0.3", "0.2", true)]
    [InlineData(ScanValueType.Float, ScanCompare.DecreasedBy, "0.25", "99.75", "100", true)]
    [InlineData(ScanValueType.Float, ScanCompare.DecreasedBy, "0.25", "99.5", "100", false)]
    [InlineData(ScanValueType.Float, ScanCompare.BiggerThan, "1.5", "1.50001", null, true)]  // ordered compares are exact
    [InlineData(ScanValueType.Float, ScanCompare.Between, "-1.5", "1.5", "-1.5", true)]
    [InlineData(ScanValueType.Double, ScanCompare.SmallerThan, "-2", "-2.5", null, true)]
    [InlineData(ScanValueType.Float, ScanCompare.Unchanged, null, "0", "-0", true)]       // value compare: 0 == -0
    [InlineData(ScanValueType.Float, ScanCompare.Changed, null, "1.00001", "1", true)]
    public void Floats_follow_ps4cheater(ScanValueType type, ScanCompare compare, string? v1, string current,
        string? previous, bool expected)
    {
        Assert.Equal(expected, Match(type, compare, v1, compare == ScanCompare.Between ? "1.5" : null, current, previous));
    }

    [Theory]
    [InlineData(ScanValueType.Float, "0x7FC00000")]
    [InlineData(ScanValueType.Double, "0x7FF8000000000000")]
    public void Nan_in_memory_matches_only_changed_and_unknown(ScanValueType type, string nanHex)
    {
        var nan = RawBits(type, nanHex);
        bool M(ScanCompare c, string? v1 = null, string? v2 = null, byte[]? prev = null) =>
            ValueMatcher.Create(ScanQuery.Parse(type, c, v1, v2), type.FixedSize()!.Value).IsMatch(nan, prev ?? nan);

        Assert.False(M(ScanCompare.Exact, "0"));
        Assert.False(M(ScanCompare.BiggerThan, "0"));
        Assert.False(M(ScanCompare.SmallerThan, "0"));
        Assert.False(M(ScanCompare.Between, "-1e30", "1e30"));
        Assert.False(M(ScanCompare.Increased));
        Assert.False(M(ScanCompare.Decreased));
        Assert.False(M(ScanCompare.IncreasedBy, "1"));
        Assert.False(M(ScanCompare.Unchanged));
        Assert.True(M(ScanCompare.Changed));          // NaN != NaN, as in PS4Cheater
        Assert.True(M(ScanCompare.UnknownInitial));
    }

    [Fact]
    public void Infinity_in_memory_is_bigger_than_any_number()
    {
        var inf = RawBits(ScanValueType.Float, "0x7F800000");
        var m = ValueMatcher.Create(ScanQuery.Parse(ScanValueType.Float, ScanCompare.BiggerThan, "1e30"), 4);
        Assert.True(m.IsMatch(inf, default));
    }

    private static byte[] RawBits(ScanValueType type, string hex)
    {
        var bits = Convert.ToUInt64(hex, 16);
        var all = BitConverter.GetBytes(bits); // little-endian host
        return all[..type.FixedSize()!.Value];
    }

    [Fact]
    public void String_exact_changed_and_unchanged()
    {
        var exact = ValueMatcher.Create(ScanQuery.Parse(ScanValueType.String, ScanCompare.Exact, "Gold"), 4);
        Assert.True(exact.IsMatch("Gold"u8, default));
        Assert.False(exact.IsMatch("gold"u8, default));   // case-sensitive, like PS4Cheater

        var changed = ValueMatcher.Create(new ScanQuery(ScanValueType.String, ScanCompare.Changed), 4);
        var unchanged = ValueMatcher.Create(new ScanQuery(ScanValueType.String, ScanCompare.Unchanged), 4);
        Assert.True(changed.IsMatch("Gold"u8, "Gale"u8));
        Assert.False(changed.IsMatch("Gold"u8, "Gold"u8));
        Assert.True(unchanged.IsMatch("Gold"u8, "Gold"u8));
        Assert.False(unchanged.IsMatch("Gold"u8, "Gale"u8));
    }

    [Fact]
    public void Byte_array_exact_with_wildcards_changed_and_unchanged()
    {
        var exact = ValueMatcher.Create(ScanQuery.Parse(ScanValueType.ByteArray, ScanCompare.Exact, "?? 34 ?? 78"), 4);
        Assert.True(exact.IsMatch([0x00, 0x34, 0xFF, 0x78], default));
        Assert.True(exact.IsMatch([0x12, 0x34, 0x56, 0x78], default));
        Assert.False(exact.IsMatch([0x12, 0x35, 0x56, 0x78], default));

        var unchanged = ValueMatcher.Create(new ScanQuery(ScanValueType.ByteArray, ScanCompare.Unchanged), 3);
        Assert.True(unchanged.IsMatch([1, 2, 3], [1, 2, 3]));
        Assert.False(unchanged.IsMatch([1, 2, 3], [1, 2, 4]));
    }

    [Theory]
    [InlineData(ScanValueType.UInt8, 4, 1)]
    [InlineData(ScanValueType.Int16, 4, 2)]
    [InlineData(ScanValueType.UInt32, 4, 4)]
    [InlineData(ScanValueType.Float, 4, 4)]
    [InlineData(ScanValueType.Int64, 4, 4)]
    [InlineData(ScanValueType.Double, 8, 4)]   // 8-byte values step 4, like PS4Cheater
    [InlineData(ScanValueType.String, 4, 1)]
    [InlineData(ScanValueType.ByteArray, 4, 1)]
    [InlineData(ScanValueType.UInt32, 1, 1)]   // alignment off
    [InlineData(ScanValueType.UInt64, 2, 2)]
    public void Alignment_is_capped_by_the_type(ScanValueType type, int alignment, int expected)
    {
        Assert.Equal(expected, ScanEngine.EffectiveAlignment(type, alignment));
    }
}
