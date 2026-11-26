using System.Globalization;

namespace PS4Mem.Core;

/// <summary>
/// Addresses as the API and the cheat files show them: hex strings like <c>0x200000000</c>.
/// JSON numbers can't hold every 64-bit address exactly, so addresses are never plain numbers.
/// </summary>
public static class HexAddress
{
    /// <summary>Parses <c>0x2000</c> or <c>2000</c> (always hex).</summary>
    /// <exception cref="ArgumentException">Not a hex number that fits 64 bits.</exception>
    public static ulong Parse(string? text)
    {
        var t = text?.Trim() ?? "";
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        if (t.Length == 0 || !ulong.TryParse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var address))
            throw new ArgumentException($"'{text}' isn't a hex address like 0x200000000.");
        return address;
    }

    public static string Format(ulong address) => $"0x{address:X}";
}
