namespace PS4Mem.Core.Scanning;

/// <summary>
/// What a scan looks for (PROMPT §5 "Scanner"). Numbers are little-endian, like the PS4.
/// The default is <see cref="UInt32"/>: most game values (money, ammo) are 32-bit integers.
/// </summary>
public enum ScanValueType
{
    UInt8, Int8, UInt16, Int16, UInt32, Int32, UInt64, Int64,
    Float, Double,
    /// <summary>Text; its length comes from the value typed in.</summary>
    String,
    /// <summary>Hex bytes with <c>??</c> wildcards; its length comes from the pattern typed in.</summary>
    ByteArray,
}

public static class ScanValueTypes
{
    /// <summary>Size in bytes, or null for <see cref="ScanValueType.String"/> and <see cref="ScanValueType.ByteArray"/>.</summary>
    public static int? FixedSize(this ScanValueType type) => type switch
    {
        ScanValueType.UInt8 or ScanValueType.Int8 => 1,
        ScanValueType.UInt16 or ScanValueType.Int16 => 2,
        ScanValueType.UInt32 or ScanValueType.Int32 or ScanValueType.Float => 4,
        ScanValueType.UInt64 or ScanValueType.Int64 or ScanValueType.Double => 8,
        _ => null,
    };

    public static bool IsInteger(this ScanValueType type) => type <= ScanValueType.Int64;

    public static bool IsFloat(this ScanValueType type) => type is ScanValueType.Float or ScanValueType.Double;

    public static bool IsNumber(this ScanValueType type) => type.IsInteger() || type.IsFloat();

    public static bool IsSigned(this ScanValueType type) =>
        type is ScanValueType.Int8 or ScanValueType.Int16 or ScanValueType.Int32 or ScanValueType.Int64
            or ScanValueType.Float or ScanValueType.Double;

    /// <summary>The integer type of <paramref name="size"/> bytes (1, 2, 4 or 8), for a signed/unsigned toggle.</summary>
    public static ScanValueType Integer(int size, bool signed) => (size, signed) switch
    {
        (1, false) => ScanValueType.UInt8,
        (1, true) => ScanValueType.Int8,
        (2, false) => ScanValueType.UInt16,
        (2, true) => ScanValueType.Int16,
        (4, false) => ScanValueType.UInt32,
        (4, true) => ScanValueType.Int32,
        (8, false) => ScanValueType.UInt64,
        (8, true) => ScanValueType.Int64,
        _ => throw new ArgumentOutOfRangeException(nameof(size), size, "Integer size is 1, 2, 4 or 8 bytes."),
    };

    /// <summary>Short name for logs and the UI, e.g. "u32", "f32", "bytes".</summary>
    public static string ShortName(this ScanValueType type) => type switch
    {
        ScanValueType.UInt8 => "u8",
        ScanValueType.Int8 => "i8",
        ScanValueType.UInt16 => "u16",
        ScanValueType.Int16 => "i16",
        ScanValueType.UInt32 => "u32",
        ScanValueType.Int32 => "i32",
        ScanValueType.UInt64 => "u64",
        ScanValueType.Int64 => "i64",
        ScanValueType.Float => "f32",
        ScanValueType.Double => "f64",
        ScanValueType.String => "string",
        ScanValueType.ByteArray => "bytes",
        _ => type.ToString(),
    };
}
