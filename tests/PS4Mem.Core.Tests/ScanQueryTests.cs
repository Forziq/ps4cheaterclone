using PS4Mem.Core.Scanning;

namespace PS4Mem.Core.Tests;

public class ScanQueryTests
{
    public static TheoryData<ScanValueType> AllTypes()
    {
        var data = new TheoryData<ScanValueType>();
        foreach (var t in Enum.GetValues<ScanValueType>()) data.Add(t);
        return data;
    }

    [Fact]
    public void Sizes_and_kinds_of_every_type()
    {
        Assert.Equal(1, ScanValueType.UInt8.FixedSize());
        Assert.Equal(2, ScanValueType.Int16.FixedSize());
        Assert.Equal(4, ScanValueType.UInt32.FixedSize());
        Assert.Equal(4, ScanValueType.Float.FixedSize());
        Assert.Equal(8, ScanValueType.Int64.FixedSize());
        Assert.Equal(8, ScanValueType.Double.FixedSize());
        Assert.Null(ScanValueType.String.FixedSize());
        Assert.Null(ScanValueType.ByteArray.FixedSize());

        Assert.True(ScanValueType.Int8.IsSigned());
        Assert.False(ScanValueType.UInt64.IsSigned());
        Assert.True(ScanValueType.Float.IsFloat());
        Assert.False(ScanValueType.Float.IsInteger());
        Assert.False(ScanValueType.String.IsNumber());
    }

    [Theory]
    [InlineData(1, false, ScanValueType.UInt8)]
    [InlineData(1, true, ScanValueType.Int8)]
    [InlineData(2, true, ScanValueType.Int16)]
    [InlineData(4, false, ScanValueType.UInt32)]
    [InlineData(8, true, ScanValueType.Int64)]
    public void Integer_picks_the_type_from_size_and_sign(int size, bool signed, ScanValueType expected)
    {
        Assert.Equal(expected, ScanValueTypes.Integer(size, signed));
    }

    [Fact]
    public void Integer_refuses_other_sizes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScanValueTypes.Integer(3, false));
    }

    [Theory]
    [InlineData(ScanCompare.Exact, 1, false)]
    [InlineData(ScanCompare.BiggerThan, 1, false)]
    [InlineData(ScanCompare.SmallerThan, 1, false)]
    [InlineData(ScanCompare.Between, 2, false)]
    [InlineData(ScanCompare.Increased, 0, true)]
    [InlineData(ScanCompare.IncreasedBy, 1, true)]
    [InlineData(ScanCompare.Decreased, 0, true)]
    [InlineData(ScanCompare.DecreasedBy, 1, true)]
    [InlineData(ScanCompare.Changed, 0, true)]
    [InlineData(ScanCompare.Unchanged, 0, true)]
    [InlineData(ScanCompare.UnknownInitial, 0, false)]
    public void Compare_value_counts_and_scan_kinds(ScanCompare compare, int values, bool needsPrevious)
    {
        Assert.Equal(values, compare.ValueCount());
        Assert.Equal(needsPrevious, compare.NeedsPrevious());
        Assert.Equal(!needsPrevious, compare.AllowedOnFirstScan());
        Assert.Equal(compare != ScanCompare.UnknownInitial, compare.AllowedOnNextScan());
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void Every_type_allows_exact_changed_and_unchanged(ScanValueType type)
    {
        Assert.True(ScanCompare.Exact.AllowedFor(type));
        Assert.True(ScanCompare.Changed.AllowedFor(type));
        Assert.True(ScanCompare.Unchanged.AllowedFor(type));
    }

    [Theory]
    [InlineData(ScanValueType.String)]
    [InlineData(ScanValueType.ByteArray)]
    public void Strings_and_byte_arrays_refuse_ordered_compares(ScanValueType type)
    {
        foreach (var c in new[] { ScanCompare.BiggerThan, ScanCompare.SmallerThan, ScanCompare.Between,
                     ScanCompare.Increased, ScanCompare.IncreasedBy, ScanCompare.Decreased,
                     ScanCompare.DecreasedBy, ScanCompare.UnknownInitial })
        {
            Assert.False(c.AllowedFor(type));
            Assert.Throws<ArgumentException>(() => new ScanQuery(type, c));
        }
    }

    [Fact]
    public void Parse_builds_a_query_with_the_right_number_of_values()
    {
        var between = ScanQuery.Parse(ScanValueType.Int16, ScanCompare.Between, "-5", "10");
        Assert.Equal([0xFB, 0xFF], between.Value!.Bytes);
        Assert.Equal([0x0A, 0x00], between.Value2!.Bytes);
        Assert.Equal(2, between.ValueSize);

        var increased = ScanQuery.Parse(ScanValueType.Float, ScanCompare.Increased, "ignored");
        Assert.Null(increased.Value);
        Assert.Equal(4, increased.ValueSize);

        var bytes = ScanQuery.Parse(ScanValueType.ByteArray, ScanCompare.Exact, "11 ?? 33 44");
        Assert.Equal(4, bytes.ValueSize);

        Assert.Null(new ScanQuery(ScanValueType.String, ScanCompare.Changed).ValueSize);
    }

    [Fact]
    public void Parse_reports_missing_values()
    {
        Assert.Throws<FormatException>(() => ScanQuery.Parse(ScanValueType.UInt32, ScanCompare.Exact));
        Assert.Throws<FormatException>(() => ScanQuery.Parse(ScanValueType.UInt32, ScanCompare.Between, "1"));
    }

    [Fact]
    public void Constructor_checks_value_count_and_size()
    {
        var four = new ScanPattern([1, 2, 3, 4]);
        Assert.Contains("takes one value", Assert.Throws<ArgumentException>(
            () => new ScanQuery(ScanValueType.UInt32, ScanCompare.Exact)).Message);
        Assert.Contains("takes no value", Assert.Throws<ArgumentException>(
            () => new ScanQuery(ScanValueType.UInt32, ScanCompare.Changed, four)).Message);
        Assert.Contains("takes two values", Assert.Throws<ArgumentException>(
            () => new ScanQuery(ScanValueType.UInt32, ScanCompare.Between, four)).Message);
        Assert.Contains("is 2 bytes", Assert.Throws<ArgumentException>(
            () => new ScanQuery(ScanValueType.UInt16, ScanCompare.Exact, four)).Message);
        Assert.Throws<ArgumentException>(() => new ScanQuery(ScanValueType.UInt32, ScanCompare.Exact,
            new ScanPattern([1, 2, 3, 4], [false, true, false, false])));
    }

    [Fact]
    public void ToString_describes_the_query_for_logs()
    {
        Assert.Equal("exact u32 123456", ScanQuery.ExactUInt32(123_456).ToString());
        Assert.Equal("between f32 1.5 and 2",
            ScanQuery.Parse(ScanValueType.Float, ScanCompare.Between, "1.5", "2").ToString());
        Assert.Equal("increased i16", new ScanQuery(ScanValueType.Int16, ScanCompare.Increased).ToString());
        Assert.Equal("exact bytes 11 ?? 33",
            ScanQuery.Parse(ScanValueType.ByteArray, ScanCompare.Exact, "11??33").ToString());
        Assert.Equal("exact string \"Gold\"",
            ScanQuery.Parse(ScanValueType.String, ScanCompare.Exact, "Gold").ToString());
    }
}
