namespace PS4Mem.Core.Scanning;

/// <summary>How a scan compares memory (PROMPT §5 "Scanner").</summary>
public enum ScanCompare
{
    /// <summary>Equals the value.</summary>
    Exact,
    /// <summary>Greater than the value.</summary>
    BiggerThan,
    /// <summary>Less than the value.</summary>
    SmallerThan,
    /// <summary>Between two values, both included.</summary>
    Between,
    /// <summary>Greater than the previous scan's value (next scan only).</summary>
    Increased,
    /// <summary>Previous value plus the value (next scan only).</summary>
    IncreasedBy,
    /// <summary>Less than the previous scan's value (next scan only).</summary>
    Decreased,
    /// <summary>Previous value minus the value (next scan only).</summary>
    DecreasedBy,
    /// <summary>Different from the previous scan's value (next scan only).</summary>
    Changed,
    /// <summary>Same as the previous scan's value (next scan only).</summary>
    Unchanged,
    /// <summary>Keep every address and its value (first scan only); narrow it down with next scans.</summary>
    UnknownInitial,
}

public static class ScanCompares
{
    /// <summary>How many values the user types: 0, 1 or 2 (<see cref="ScanCompare.Between"/>).</summary>
    public static int ValueCount(this ScanCompare compare) => compare switch
    {
        ScanCompare.Between => 2,
        ScanCompare.Increased or ScanCompare.Decreased or ScanCompare.Changed or ScanCompare.Unchanged
            or ScanCompare.UnknownInitial => 0,
        _ => 1,
    };

    /// <summary>True when the compare looks at the previous scan's value, so it needs a next scan.</summary>
    public static bool NeedsPrevious(this ScanCompare compare) => compare is ScanCompare.Increased
        or ScanCompare.IncreasedBy or ScanCompare.Decreased or ScanCompare.DecreasedBy
        or ScanCompare.Changed or ScanCompare.Unchanged;

    public static bool AllowedOnFirstScan(this ScanCompare compare) => !compare.NeedsPrevious();

    public static bool AllowedOnNextScan(this ScanCompare compare) => compare != ScanCompare.UnknownInitial;

    /// <summary>
    /// Strings and byte arrays have no order, so they only support exact, changed and unchanged.
    /// Unknown initial value needs a number type, since it has no typed value to give the length.
    /// </summary>
    public static bool AllowedFor(this ScanCompare compare, ScanValueType type) =>
        type.IsNumber() || compare is ScanCompare.Exact or ScanCompare.Changed or ScanCompare.Unchanged;

    /// <summary>Short name for logs, e.g. "exact", "increased by".</summary>
    public static string ShortName(this ScanCompare compare) => compare switch
    {
        ScanCompare.Exact => "exact",
        ScanCompare.BiggerThan => "bigger than",
        ScanCompare.SmallerThan => "smaller than",
        ScanCompare.Between => "between",
        ScanCompare.Increased => "increased",
        ScanCompare.IncreasedBy => "increased by",
        ScanCompare.Decreased => "decreased",
        ScanCompare.DecreasedBy => "decreased by",
        ScanCompare.Changed => "changed",
        ScanCompare.Unchanged => "unchanged",
        ScanCompare.UnknownInitial => "unknown initial value",
        _ => compare.ToString(),
    };
}
