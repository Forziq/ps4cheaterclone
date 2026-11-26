using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PS4Mem.Core.Scanning;

/// <summary>
/// A value typed by the user, as the bytes it has in PS4 memory (little-endian).
/// <see cref="Wildcards"/> is null unless a byte array has <c>??</c> bytes; then
/// <c>Wildcards[i]</c> is true where any byte matches.
/// </summary>
public sealed record ScanPattern(byte[] Bytes, bool[]? Wildcards = null)
{
    public int Length => Bytes.Length;

    public bool HasWildcards => Wildcards is not null;

    /// <summary>True when <paramref name="data"/> (at least <see cref="Length"/> bytes) matches, wildcards included.</summary>
    public bool Matches(ReadOnlySpan<byte> data)
    {
        if (Wildcards is null) return data[..Bytes.Length].SequenceEqual(Bytes);
        for (var i = 0; i < Bytes.Length; i++)
            if (!Wildcards[i] && data[i] != Bytes[i])
                return false;
        return true;
    }
}

/// <summary>
/// Turns what the user typed into the bytes to look for (PROMPT §5 "Scanner": all types, hex input).
/// Throws <see cref="FormatException"/> with a message that can be shown as is.
/// <list type="bullet">
/// <item>Integers: decimal (<c>-5</c>, <c>1000</c>), or hex with <c>0x</c> or when <c>hex</c> is on.
/// Hex is the raw bits, so <c>FF</c> as i8 is -1. Values out of range for the type are refused.</item>
/// <item>Float and double: decimal with <c>.</c> or <c>,</c> (<c>1.5</c>, <c>1e3</c>); with hex on,
/// the raw IEEE bits (<c>3F800000</c> is 1.0f). NaN and infinity are refused.</item>
/// <item>String: the text as UTF-8 (ASCII is the same bytes); hex has no effect.</item>
/// <item>Byte array: hex bytes, spaces optional (<c>12 34 ?? 56</c> or <c>1234??56</c>); <c>??</c> or a
/// lone <c>?</c> matches any byte. At least one byte must not be a wildcard.</item>
/// </list>
/// </summary>
public static class ValueParser
{
    public static ScanPattern Parse(ScanValueType type, string? text, bool hex = false)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new FormatException("Enter a value.");

        return type switch
        {
            ScanValueType.String => new ScanPattern(Encoding.UTF8.GetBytes(text)),
            ScanValueType.ByteArray => ParseBytes(text),
            ScanValueType.Float or ScanValueType.Double => ParseFloat(type, text.Trim(), hex),
            _ => ParseInteger(type, text.Trim(), hex),
        };
    }

    public static bool TryParse(ScanValueType type, string? text, bool hex, out ScanPattern? pattern, out string? error)
    {
        try
        {
            pattern = Parse(type, text, hex);
            error = null;
            return true;
        }
        catch (FormatException e)
        {
            pattern = null;
            error = e.Message;
            return false;
        }
    }

    private static ScanPattern ParseInteger(ScanValueType type, string text, bool hex)
    {
        var size = type.FixedSize()!.Value;
        var name = type.ShortName();
        ulong bits;

        if (hex || HasHexPrefix(text))
        {
            var digits = HasHexPrefix(text) ? text[2..] : text;
            bits = ParseHexBits(digits, size, name);
        }
        else if (type.IsSigned())
        {
            if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v))
                throw new FormatException($"'{text}' isn't a whole number.");
            var bitsWide = size * 8;
            var min = long.MinValue >> (64 - bitsWide);
            var max = long.MaxValue >> (64 - bitsWide);
            if (v < min || v > max)
                throw new FormatException($"{v} doesn't fit in {name} ({min} to {max}).");
            bits = unchecked((ulong)v);
        }
        else
        {
            if (text.StartsWith('-'))
                throw new FormatException($"{name} can't be negative; use the signed type for '{text}'.");
            if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var v))
                throw new FormatException($"'{text}' isn't a whole number.");
            var max = ulong.MaxValue >> (64 - size * 8);
            if (v > max)
                throw new FormatException($"{v} doesn't fit in {name} (0 to {max}).");
            bits = v;
        }

        return new ScanPattern(LittleEndian(bits, size));
    }

    private static ScanPattern ParseFloat(ScanValueType type, string text, bool hex)
    {
        var size = type.FixedSize()!.Value;
        if (hex || HasHexPrefix(text))
        {
            var digits = HasHexPrefix(text) ? text[2..] : text;
            var bits = ParseHexBits(digits, size, type.ShortName());
            var value = size == 4 ? BitConverter.Int32BitsToSingle((int)bits) : BitConverter.Int64BitsToDouble((long)bits);
            if (!double.IsFinite(value))
                throw new FormatException($"0x{digits} is NaN or infinity, which can't be scanned for.");
            return new ScanPattern(LittleEndian(bits, size));
        }

        // Accept a decimal comma too ("1,5"), since the Mac may use one.
        var normal = text.Contains('.') ? text : text.Replace(',', '.');
        if (!double.TryParse(normal, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            throw new FormatException($"'{text}' isn't a number.");
        if (type == ScanValueType.Float)
        {
            var f = (float)d;
            if (!float.IsFinite(f))
                throw new FormatException($"'{text}' is NaN, infinity or too big for a float.");
            return new ScanPattern(LittleEndian((uint)BitConverter.SingleToInt32Bits(f), 4));
        }
        if (!double.IsFinite(d))
            throw new FormatException($"'{text}' is NaN, infinity or too big for a double.");
        return new ScanPattern(LittleEndian((ulong)BitConverter.DoubleToInt64Bits(d), 8));
    }

    private static ScanPattern ParseBytes(string text)
    {
        // Lone "?" tokens become "??", then spaces go: "12 ? 34" -> "12??34".
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t == "?" ? "??" : t);
        var compact = string.Concat(tokens);
        if (compact.Length % 2 != 0)
            throw new FormatException("Byte arrays are pairs of hex digits, like '12 34 ?? 56'.");

        var count = compact.Length / 2;
        var bytes = new byte[count];
        var wildcards = new bool[count];
        var any = false;
        for (var i = 0; i < count; i++)
        {
            var pair = compact.AsSpan(i * 2, 2);
            if (pair is "??")
            {
                wildcards[i] = true;
                any = true;
                continue;
            }
            if (pair.Contains('?'))
                throw new FormatException($"'{pair}': a wildcard covers a whole byte, write '??'.");
            if (!byte.TryParse(pair, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bytes[i]))
                throw new FormatException($"'{pair}' isn't a hex byte.");
        }

        if (wildcards.All(w => w))
            throw new FormatException("A byte array needs at least one byte that isn't '??'.");
        return new ScanPattern(bytes, any ? wildcards : null);
    }

    private static bool HasHexPrefix(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);

    private static ulong ParseHexBits(string digits, int size, string name)
    {
        if (digits.StartsWith('-'))
            throw new FormatException("Hex values can't be negative; type the bits, e.g. FF for -1 as i8.");
        if (digits.Length == 0
            || !ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var bits))
            throw new FormatException($"'{digits}' isn't a hex number.");
        var max = ulong.MaxValue >> (64 - size * 8);
        if (bits > max)
            throw new FormatException($"0x{digits} doesn't fit in {name} ({size * 2} hex digits at most).");
        return bits;
    }

    private static byte[] LittleEndian(ulong bits, int size)
    {
        Span<byte> all = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(all, bits);
        return all[..size].ToArray();
    }
}

/// <summary>Shows a value read from memory: decimal or hex for integers, shortest form for floats.</summary>
public static class ValueFormatter
{
    public static string Format(ScanValueType type, ReadOnlySpan<byte> bytes, bool hex = false)
    {
        var inv = CultureInfo.InvariantCulture;
        if (type.FixedSize() is { } size && bytes.Length < size)
            throw new ArgumentException($"{type.ShortName()} needs {size} bytes, got {bytes.Length}.", nameof(bytes));

        if (hex && type.IsNumber())
        {
            var size2 = type.FixedSize()!.Value;
            Span<byte> all = stackalloc byte[8];
            all.Clear();
            bytes[..size2].CopyTo(all);
            return "0x" + BinaryPrimitives.ReadUInt64LittleEndian(all).ToString("X" + size2 * 2, inv);
        }

        return type switch
        {
            ScanValueType.UInt8 => bytes[0].ToString(inv),
            ScanValueType.Int8 => ((sbyte)bytes[0]).ToString(inv),
            ScanValueType.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(bytes).ToString(inv),
            ScanValueType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(bytes).ToString(inv),
            ScanValueType.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(bytes).ToString(inv),
            ScanValueType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(bytes).ToString(inv),
            ScanValueType.UInt64 => BinaryPrimitives.ReadUInt64LittleEndian(bytes).ToString(inv),
            ScanValueType.Int64 => BinaryPrimitives.ReadInt64LittleEndian(bytes).ToString(inv),
            ScanValueType.Float => BinaryPrimitives.ReadSingleLittleEndian(bytes).ToString(inv),
            ScanValueType.Double => BinaryPrimitives.ReadDoubleLittleEndian(bytes).ToString(inv),
            ScanValueType.String => Printable(Encoding.UTF8.GetString(bytes)),
            _ => string.Join(' ', Convert.ToHexString(bytes).Chunk(2).Select(c => new string(c))),
        };
    }

    /// <summary>Control characters become '.', so a string from memory can't break a terminal line.</summary>
    private static string Printable(string s) =>
        string.Create(s.Length, s, (dst, src) =>
        {
            for (var i = 0; i < src.Length; i++)
                dst[i] = char.IsControl(src[i]) ? '.' : src[i];
        });
}
