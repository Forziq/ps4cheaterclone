using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;

namespace PS4Mem.Core.Sections;

/// <summary>One readable map in the sections list, with whether it's selected for scanning and why.</summary>
public sealed record Section(MemoryMap Map, bool Selected, string Reason)
{
    public string Name => Map.Name;
    public ulong Start => Map.Start;
    public ulong End => Map.End;
    public ulong Size => Map.Size;

    /// <summary>The name, or "(unnamed)" for maps without one (the game's heap is usually unnamed).</summary>
    public string DisplayName => Name.Length > 0 ? Name : "(unnamed)";
}

/// <summary>The sections list (readable maps, sorted by start) plus selected/total counts and bytes.</summary>
public sealed record SectionSelection(IReadOnlyList<Section> Sections, int SkippedMaps)
{
    public int TotalCount => Sections.Count;
    public int SelectedCount => Sections.Count(s => s.Selected);
    public ulong TotalBytes => Sum(Sections);
    public ulong SelectedBytes => Sum(Sections.Where(s => s.Selected));
    public IEnumerable<Section> Selected => Sections.Where(s => s.Selected);

    private static ulong Sum(IEnumerable<Section> sections) =>
        sections.Aggregate(0UL, (total, s) => total + s.Size);
}

/// <summary>
/// Builds the sections list from process maps and picks the default selection (PROMPT.md §3.3,
/// protocol.md §14):
/// <list type="bullet">
/// <item>listed: readable maps (<c>prot &amp; 0x1</c>), like PS4Cheater (ProcessManager.cs:69);</item>
/// <item>skipped: <c>end &lt;= start</c>, over <see cref="PS4MemSettings.MaxSectionBytes"/>, duplicates
/// of the previous map (PS4CheaterNeo SectionTool.cs:296-311), and unreadable maps;</item>
/// <item>selected: writable too (unless <see cref="RequireWritable"/> is off) and either unnamed
/// (anonymous memory, where games keep most of their data; <see cref="IncludeUnnamed"/>) or the
/// name contains any of <see cref="NameFilter"/>, ignoring case. An empty filter selects every name.</item>
/// </list>
/// Every map is logged as kept or skipped, with the reason (category <c>maps</c>).
/// </summary>
public sealed class SectionFilter
{
    private const string Cat = "maps";

    public IReadOnlyList<string> NameFilter { get; init; }
    public bool RequireWritable { get; init; }
    public bool IncludeUnnamed { get; init; }
    public ulong MaxSectionBytes { get; init; }

    public SectionFilter(PS4MemSettings? settings = null)
    {
        settings ??= PS4MemSettings.Default;
        NameFilter = settings.SectionNameFilter;
        RequireWritable = settings.SectionRequireWritable;
        IncludeUnnamed = settings.SectionIncludeUnnamed;
        MaxSectionBytes = settings.MaxSectionBytes;
    }

    public SectionSelection Apply(IEnumerable<MemoryMap> maps, DebugLog? log = null)
    {
        log ??= DebugLog.Disabled;
        var filterText = NameFilter.Count == 0 ? "(any name)" : string.Join(",", NameFilter);
        log.Info(Cat, $"Filter: name contains {filterText}, unnamed included: {IncludeUnnamed}, writable required: {RequireWritable}");

        var sections = new List<Section>();
        var skipped = 0;
        MemoryMap? previous = null;

        foreach (var map in maps.OrderBy(m => m.Start))
        {
            var skipReason = SkipReason(map, previous);
            previous = map;
            if (skipReason is not null)
            {
                skipped++;
                log.Info(Cat, $"skip {Describe(map)}: {skipReason}");
                continue;
            }

            var (selected, reason) = Select(map);
            sections.Add(new Section(map, selected, reason));
            log.Info(Cat, $"keep {Describe(map)}: {(selected ? "selected" : "not selected")}, {reason}");
        }

        var result = new SectionSelection(sections, skipped);
        log.Info(Cat, $"{result.SelectedCount}/{result.TotalCount} sections selected, " +
                      $"{result.SelectedBytes}/{result.TotalBytes} bytes, {skipped} maps skipped");
        return result;
    }

    /// <summary>Same list with every section selected ("select all").</summary>
    public static SectionSelection SelectAll(SectionSelection selection) =>
        selection with
        {
            Sections = selection.Sections.Select(s => s with { Selected = true, Reason = "select all" }).ToList(),
        };

    private string? SkipReason(MemoryMap map, MemoryMap? previous)
    {
        if (map.End <= map.Start) return "end <= start";
        if (map.Size > MaxSectionBytes) return $"size over {MaxSectionBytes} bytes";
        if (previous is not null && previous.Start == map.Start && previous.End == map.End)
            return "same range as the previous map";
        if (!map.IsReadable) return "not readable";
        return null;
    }

    private (bool Selected, string Reason) Select(MemoryMap map)
    {
        if (RequireWritable && !map.IsWritable)
            return (false, "not writable");

        if (NameFilter.Count == 0)
            return (true, "no name filter");

        if (IncludeUnnamed && string.IsNullOrWhiteSpace(map.Name))
            return (true, "unnamed (anonymous memory)");

        var match = NameFilter.FirstOrDefault(key =>
            key.Length > 0 && map.Name.Contains(key, StringComparison.OrdinalIgnoreCase));
        return match is null
            ? (false, "name matches no filter key")
            : (true, $"name contains '{match}'");
    }

    private static string Describe(MemoryMap m) =>
        $"'{m.Name}' 0x{m.Start:X}-0x{m.End:X} {m.ProtText} (0x{m.Prot:X})";
}
