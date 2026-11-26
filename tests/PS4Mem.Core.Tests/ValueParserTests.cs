using PS4Mem.Core.Scanning;

namespace PS4Mem.Core.Tests;

public class ValueParserTests
{
    private static string Hex(ScanPattern p) => Convert.ToHexString(p.Bytes);

    [Theory]
    [InlineData(ScanValueType.UInt8, "255", "FF")]
    [InlineData(ScanValueType.Int8, "-1", "FF")]
    [InlineData(ScanValueType.Int8, "-128", "80")]
    [InlineData(ScanValueType.UInt16, "4660", "3412")]
    [InlineData(ScanValueType.Int16, "-2", "FEFF")]
    [InlineData(ScanValueType.UInt32, "123456", "40E20100")]
    [InlineData(ScanValueType.Int32, "-123456", "C01DFEFF")]
    [InlineData(ScanValueType.UInt64, "18446744073709551615", "FFFFFFFFFFFFFFFF")]
    [InlineData(ScanValueType.Int64, "-9223372036854775808", "0000000000000080")]
    [InlineData(ScanValueType.UInt32, " 7 ", "07000000")]
    public void Integers_parse_to_little_endian_bytes(ScanValueType type, string text, string expected)
    {
        Assert.Equal(expected, Hex(ValueParser.Parse(type, text)));
    }

    [Theory]
    [InlineData(ScanValueType.UInt32, "0x1E240", false, "40E20100")]
    [InlineData(ScanValueType.UInt32, "1E240", true, "40E20100")]
    [InlineData(ScanValueType.UInt32, "0X1e240", true, "40E20100")]
    [InlineData(ScanValueType.Int8, "FF", true, "FF")]          // hex is the raw bits: -1
    [InlineData(ScanValueType.Int32, "FFFFFFFF", true, "FFFFFFFF")]
    [InlineData(ScanValueType.UInt16, "10", true, "1000")]      // 0x10 = 16, not ten
    [InlineData(ScanValueType.Float, "3F800000", true, "0000803F")]   // 1.0f
    [InlineData(ScanValueType.Double, "0x3FF0000000000000", false, "000000000000F03F")]
    public void Hex_input_is_the_raw_bits(ScanValueType type, string text, bool hex, string expected)
    {
        Assert.Equal(expected, Hex(ValueParser.Parse(type, text, hex)));
    }

    [Theory]
    [InlineData(ScanValueType.UInt8, "256", "doesn't fit")]
    [InlineData(ScanValueType.Int8, "128", "doesn't fit")]
    [InlineData(ScanValueType.Int8, "-129", "doesn't fit")]
    [InlineData(ScanValueType.Int16, "40000", "doesn't fit")]
    [InlineData(ScanValueType.UInt32, "4294967296", "doesn't fit")]
    [InlineData(ScanValueType.UInt32, "-1", "can't be negative")]
    [InlineData(ScanValueType.UInt64, "18446744073709551616", "isn't a whole number")]
    [InlineData(ScanValueType.UInt32, "12abc", "isn't a whole number")]
    [InlineData(ScanValueType.UInt32, "1.5", "isn't a whole number")]
    [InlineData(ScanValueType.UInt32, "", "Enter a value")]
    [InlineData(ScanValueType.UInt32, "   ", "Enter a value")]
    [InlineData(ScanValueType.UInt8, "0x100", "doesn't fit")]
    [InlineData(ScanValueType.UInt32, "0x", "isn't a hex number")]
    [InlineData(ScanValueType.UInt32, "0xZZ", "isn't a hex number")]
    public void Bad_integers_are_refused_with_a_clear_message(ScanValueType type, string text, string message)
    {
        var e = Assert.Throws<FormatException>(() => ValueParser.Parse(type, text));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void Negative_hex_is_refused()
    {
        var e = Assert.Throws<FormatException>(() => ValueParser.Parse(ScanValueType.Int32, "-5", hex: true));
        Assert.Contains("can't be negative", e.Message);
    }

    [Theory]
    [InlineData(ScanValueType.Float, "1.5", "0000C03F")]
    [InlineData(ScanValueType.Float, "1,5", "0000C03F")]     // decimal comma
    [InlineData(ScanValueType.Float, "-2", "000000C0")]
    [InlineData(ScanValueType.Float, "1e3", "00007A44")]
    [InlineData(ScanValueType.Double, "0.1", "9A9999999999B93F")]
    [InlineData(ScanValueType.Double, "100", "0000000000005940")]
    public void Floats_parse_to_ieee_bytes(ScanValueType type, string text, string expected)
    {
        Assert.Equal(expected, Hex(ValueParser.Parse(type, text)));
    }

    [Theory]
    [InlineData(ScanValueType.Float, "NaN")]
    [InlineData(ScanValueType.Float, "Infinity")]
    [InlineData(ScanValueType.Float, "1e39")]            // too big for a float
    [InlineData(ScanValueType.Double, "1e309")]
    [InlineData(ScanValueType.Float, "abc")]
    [InlineData(ScanValueType.Float, "7FC00000")]        // not hex mode, so not a number
    public void Bad_floats_are_refused(ScanValueType type, string text)
    {
        Assert.Throws<FormatException>(() => ValueParser.Parse(type, text));
    }

    [Fact]
    public void Hex_float_bits_for_nan_are_refused()
    {
        var e = Assert.Throws<FormatException>(() => ValueParser.Parse(ScanValueType.Float, "7FC00000", hex: true));
        Assert.Contains("NaN", e.Message);
    }

    [Fact]
    public void Strings_are_utf8_and_ignore_hex()
    {
        Assert.Equal("4D6F6E6579", Hex(ValueParser.Parse(ScanValueType.String, "Money")));
        Assert.Equal("4142", Hex(ValueParser.Parse(ScanValueType.String, "AB", hex: true)));
        Assert.Equal("C3A9", Hex(ValueParser.Parse(ScanValueType.String, "é")));
        Assert.Null(ValueParser.Parse(ScanValueType.String, "x").Wildcards);
    }

    [Theory]
    [InlineData("12 34 56", "123456")]
    [InlineData("123456", "123456")]
    [InlineData("de AD  bE ef", "DEADBEEF")]
    public void Byte_arrays_parse_with_or_without_spaces(string text, string expected)
    {
        var p = ValueParser.Parse(ScanValueType.ByteArray, text);
        Assert.Equal(expected, Hex(p));
        Assert.False(p.HasWildcards);
    }

    [Theory]
    [InlineData("12 ?? 56")]
    [InlineData("12??56")]
    [InlineData("12 ? 56")]
    public void Byte_array_wildcards(string text)
    {
        var p = ValueParser.Parse(ScanValueType.ByteArray, text);
        Assert.Equal(3, p.Length);
        Assert.Equal([false, true, false], p.Wildcards!);
        Assert.True(p.Matches([0x12, 0x00, 0x56]));
        Assert.True(p.Matches([0x12, 0xFF, 0x56, 0x99]));
        Assert.False(p.Matches([0x12, 0x00, 0x57]));
    }

    [Theory]
    [InlineData("123", "pairs")]
    [InlineData("12 3", "pairs")]
    [InlineData("1? 34", "whole byte")]
    [InlineData("GG", "isn't a hex byte")]
    [InlineData("?? ??", "at least one byte")]
    [InlineData("??", "at least one byte")]
    public void Bad_byte_arrays_are_refused(string text, string message)
    {
        var e = Assert.Throws<FormatException>(() => ValueParser.Parse(ScanValueType.ByteArray, text));
        Assert.Contains(message, e.Message);
    }

    [Fact]
    public void TryParse_returns_the_error_instead_of_throwing()
    {
        Assert.False(ValueParser.TryParse(ScanValueType.UInt8, "300", false, out var p, out var error));
        Assert.Null(p);
        Assert.Contains("doesn't fit", error);
        Assert.True(ValueParser.TryParse(ScanValueType.UInt8, "30", false, out p, out error));
        Assert.Equal([30], p!.Bytes);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(ScanValueType.UInt8, "FF", false, "255")]
    [InlineData(ScanValueType.Int8, "FF", false, "-1")]
    [InlineData(ScanValueType.Int16, "FEFF", false, "-2")]
    [InlineData(ScanValueType.UInt32, "40E20100", false, "123456")]
    [InlineData(ScanValueType.UInt32, "40E20100", true, "0x0001E240")]
    [InlineData(ScanValueType.Int32, "C01DFEFF", false, "-123456")]
    [InlineData(ScanValueType.Int64, "FFFFFFFFFFFFFFFF", false, "-1")]
    [InlineData(ScanValueType.UInt64, "FFFFFFFFFFFFFFFF", true, "0xFFFFFFFFFFFFFFFF")]
    [InlineData(ScanValueType.Float, "0000C03F", false, "1.5")]
    [InlineData(ScanValueType.Float, "0000803F", true, "0x3F800000")]
    [InlineData(ScanValueType.Double, "9A9999999999B93F", false, "0.1")]
    [InlineData(ScanValueType.String, "4D6F0A79", false, "Mo.y")]     // control chars shown as '.'
    [InlineData(ScanValueType.ByteArray, "DEADBEEF", false, "DE AD BE EF")]
    public void Formatter_shows_values(ScanValueType type, string hexBytes, bool hex, string expected)
    {
        Assert.Equal(expected, ValueFormatter.Format(type, Convert.FromHexString(hexBytes), hex));
    }

    [Theory]
    [InlineData(ScanValueType.UInt8, "200")]
    [InlineData(ScanValueType.Int16, "-300")]
    [InlineData(ScanValueType.UInt32, "4000000000")]
    [InlineData(ScanValueType.Int64, "-42")]
    [InlineData(ScanValueType.Float, "3.25")]
    [InlineData(ScanValueType.Double, "-0.5")]
    [InlineData(ScanValueType.String, "Gold")]
    public void Format_round_trips_parse(ScanValueType type, string text)
    {
        Assert.Equal(text, ValueFormatter.Format(type, ValueParser.Parse(type, text).Bytes));
    }

    [Fact]
    public void Formatter_refuses_too_few_bytes()
    {
        Assert.Throws<ArgumentException>(() => ValueFormatter.Format(ScanValueType.UInt32, [1, 2]));
    }
}
