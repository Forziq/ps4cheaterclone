namespace PS4Mem.Core.Scanning;

/// <summary>
/// Parses the value type, compare and method names the API takes. Case, spaces, dashes and
/// underscores don't matter, and both the short names ("u32", "bigger than") and the enum
/// names ("UInt32", "biggerThan") work.
/// </summary>
public static class ScanNames
{
    public static ScanValueType ParseType(string? text, ScanValueType fallback = ScanValueType.UInt32) =>
        string.IsNullOrWhiteSpace(text) ? fallback : Parse(text, Enum.GetValues<ScanValueType>(), t => t.ShortName(), "value type");

    public static ScanCompare ParseCompare(string? text, ScanCompare fallback = ScanCompare.Exact) =>
        string.IsNullOrWhiteSpace(text) ? fallback : Parse(text, Enum.GetValues<ScanCompare>(), c => c.ShortName(), "compare");

    public static ScanMethod ParseMethod(string? text) =>
        string.IsNullOrWhiteSpace(text) ? ScanMethod.Local : Parse(text, Enum.GetValues<ScanMethod>(), m => m.ToString(), "scan method");

    /// <summary>"local" or "console".</summary>
    public static string Name(this ScanMethod method) => method.ToString().ToLowerInvariant();

    private static T Parse<T>(string text, T[] values, Func<T, string> shortName, string what) where T : struct, Enum
    {
        var key = Key(text);
        foreach (var v in values)
            if (Key(shortName(v)) == key || Key(v.ToString()) == key)
                return v;
        throw new ArgumentException($"Unknown {what} '{text}'. Use one of: {string.Join(", ", values.Select(shortName))}.");
    }

    private static string Key(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
