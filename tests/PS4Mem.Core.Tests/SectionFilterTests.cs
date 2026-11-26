using PS4Mem.Core;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Sections;

namespace PS4Mem.Core.Tests;

public class SectionFilterTests
{
    private const ushort R = 1, RW = 3, RX = 5, None = 0;

    // A mix like a real game's maps, deliberately out of order.
    private static readonly MemoryMap[] Sample =
    [
        new("SceLibcInternal", 0x9000_0000, 0x9010_0000, 0, RW),   // writable, no matching name
        new("executable", 0x40_0000, 0x80_0000, 0, RX),            // name matches, not writable
        new("executable", 0x80_0000, 0x90_0000, 0, RW),            // selected (exe)
        new("anon:0x123", 0x2_0000_0000, 0x2_0400_0000, 0, RW),    // selected (anon)
        new("broken", 0x5000, 0, 0, RW),                           // end = 0: skipped
        new("empty", 0x6000, 0x6000, 0, RW),                       // end == start: skipped
        new("guard", 0x7000, 0x8000, 0, None),                     // not readable: skipped
        new("SmallBlockAllocator", 0x1_0000_0000, 0x1_0010_0000, 0, RW), // selected
        new("game_data", 0x3_0000_0000, 0x3_0001_0000, 0, R),      // read-only
    ];

    [Fact]
    public void Default_selection_is_readable_writable_and_name_match()
    {
        var result = new SectionFilter().Apply(Sample);

        Assert.Equal(6, result.TotalCount);       // readable, valid maps
        Assert.Equal(3, result.SkippedMaps);      // end=0, end==start, not readable
        Assert.Equal(["executable", "SmallBlockAllocator", "anon:0x123"],
            result.Selected.Select(s => s.Name).ToArray());
        Assert.Equal(0x10_0000UL + 0x10_0000UL + 0x400_0000UL, result.SelectedBytes);
    }

    [Fact]
    public void Sections_are_sorted_by_start()
    {
        var starts = new SectionFilter().Apply(Sample).Sections.Select(s => s.Start).ToList();
        Assert.Equal(starts.OrderBy(s => s), starts);
    }

    [Fact]
    public void End_zero_map_does_not_crash_and_is_not_listed()
    {
        var result = new SectionFilter().Apply([new MemoryMap("x", 0xFFFF_0000, 0, 0, RW)]);
        Assert.Empty(result.Sections);
        Assert.Equal(1, result.SkippedMaps);
        Assert.Equal(0UL, result.TotalBytes);
    }

    [Fact]
    public void Name_match_ignores_case()
    {
        var result = new SectionFilter().Apply([new MemoryMap("mygame.heap", 0x1000, 0x2000, 0, RW)]);
        Assert.True(result.Sections.Single().Selected);  // "Game" key
    }

    [Fact]
    public void Read_only_selected_when_writable_not_required()
    {
        var filter = new SectionFilter { RequireWritable = false, NameFilter = ["game", "exe"] };
        var selected = filter.Apply(Sample).Selected.Select(s => s.Start).ToList();
        Assert.Equal([0x40_0000UL, 0x80_0000UL, 0x3_0000_0000UL], selected);
    }

    [Fact]
    public void Empty_name_filter_selects_every_writable_section()
    {
        var result = new SectionFilter { NameFilter = [] }.Apply(Sample);
        Assert.Equal(4, result.SelectedCount);
    }

    [Fact]
    public void Huge_and_duplicate_maps_are_skipped()
    {
        var result = new SectionFilter().Apply(
        [
            new MemoryMap("anon", 0x1000, 0x2000, 0, RW),
            new MemoryMap("anon", 0x1000, 0x2000, 0, RW),
            new MemoryMap("anon", 0x10_0000, 0x10_0000 + 0x10_0000_0001UL, 0, RW),
        ]);
        Assert.Single(result.Sections);
        Assert.Equal(2, result.SkippedMaps);
    }

    [Fact]
    public void Select_all_selects_every_listed_section()
    {
        var all = SectionFilter.SelectAll(new SectionFilter().Apply(Sample));
        Assert.Equal(all.TotalCount, all.SelectedCount);
        Assert.Equal(all.TotalBytes, all.SelectedBytes);
    }

    [Fact]
    public void Logs_every_map_with_kept_or_skipped_and_why()
    {
        var sw = new StringWriter();
        new SectionFilter().Apply(Sample, new DebugLog(sw));
        var text = sw.ToString();

        Assert.Contains("[maps] skip 'broken' 0x5000-0x0 rw- (0x3): end <= start", text);
        Assert.Contains("[maps] skip 'guard' 0x7000-0x8000 --- (0x0): not readable", text);
        Assert.Contains("[maps] keep 'anon:0x123' 0x200000000-0x204000000 rw- (0x3): selected, name contains 'anon'", text);
        Assert.Contains("[maps] keep 'game_data' 0x300000000-0x300010000 r-- (0x1): not selected, not writable", text);
        Assert.Contains("3/6 sections selected", text);
        Assert.Equal(Sample.Length, text.Split('\n').Count(l => l.Contains("[maps] keep") || l.Contains("[maps] skip")));
    }
}
