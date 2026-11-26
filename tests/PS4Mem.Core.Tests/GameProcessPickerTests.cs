using PS4Mem.Core;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Sections;

namespace PS4Mem.Core.Tests;

public class GameProcessPickerTests
{
    // System processes seen on a real console, plus the usual game names.
    private static readonly ProcessEntry[] SystemOnly =
    [
        new("SceSysCore.elf", 1),
        new("mini-syscore.elf", 2),
        new("orbis_audiod.elf", 40),
        new("GnmCompositor.elf", 41),
        new("fs_cleaner.elf", 42),
        new("SceVdecProxy.elf", 43),
        new("SceShellUI", 50),
        new("syscore.elf", 51),
    ];

    [Fact]
    public void Picks_eboot_bin()
    {
        var processes = SystemOnly.Append(new("eboot.bin", 90)).Append(new("game.elf", 91)).ToList();
        var pick = GameProcessPicker.Pick(processes);
        Assert.Equal(90, pick.Process?.Pid);
        Assert.Contains("eboot.bin", pick.Reason);
    }

    [Fact]
    public void Picks_non_system_elf_when_no_eboot()
    {
        var processes = SystemOnly.Append(new("MyGame.elf", 77)).ToList();
        var pick = GameProcessPicker.Pick(processes);
        Assert.Equal("MyGame.elf", pick.Process?.Name);
    }

    [Fact]
    public void Picks_nothing_when_only_system_processes()
    {
        var pick = GameProcessPicker.Pick(SystemOnly);
        Assert.Null(pick.Process);
        Assert.Contains("Is a game running?", pick.Reason);
    }

    [Fact]
    public void System_prefixes_are_configurable()
    {
        var settings = PS4MemSettings.Default with { SystemProcessPrefixes = ["Sce"] };
        var pick = GameProcessPicker.Pick([new("SceSysCore.elf", 1), new("orbis_audiod.elf", 2)], settings);
        Assert.Equal("orbis_audiod.elf", pick.Process?.Name);
    }

    [Fact]
    public void Several_matches_take_the_highest_pid()
    {
        var pick = GameProcessPicker.Pick([new("eboot.bin", 80), new("eboot.bin", 95), new("eboot.bin", 85)]);
        Assert.Equal(95, pick.Process?.Pid);
        Assert.Contains("3 matches", pick.Reason);
    }

    [Fact]
    public void Pid_override_wins_over_eboot()
    {
        var processes = SystemOnly.Append(new("eboot.bin", 90)).ToList();
        Assert.Equal(40, GameProcessPicker.Pick(processes, pid: 40).Process?.Pid);
        Assert.Null(GameProcessPicker.Pick(processes, pid: 12345).Process);
    }

    [Fact]
    public void Name_override_exact_then_unique_partial()
    {
        var processes = SystemOnly.Append(new("eboot.bin", 90)).ToList();
        Assert.Equal(41, GameProcessPicker.Pick(processes, name: "gnmcompositor.elf").Process?.Pid);
        Assert.Equal(42, GameProcessPicker.Pick(processes, name: "cleaner").Process?.Pid);

        var ambiguous = GameProcessPicker.Pick(processes, name: "Sce");
        Assert.Null(ambiguous.Process);
        Assert.Contains("Use --pid", ambiguous.Reason);
    }

    [Fact]
    public void Logs_the_pick_and_why()
    {
        var sw = new StringWriter();
        GameProcessPicker.Pick([new("eboot.bin", 90)], log: new DebugLog(sw));
        Assert.Contains("[picker] Picked 'eboot.bin' pid 90 from 1 processes: name is eboot.bin", sw.ToString());
    }

    // ---- Choose: the user's answer at the process prompt ----

    private static readonly List<ProcessEntry> Ac3 =
        SystemOnly.Append(new("ac3_engine_orbis_fn.elf", 85)).Append(new("SceShellCore", 86)).ToList();

    [Fact]
    public void Choose_empty_input_takes_the_suggestion()
    {
        var suggestion = GameProcessPicker.Pick(Ac3);
        var pick = GameProcessPicker.Choose(Ac3, "  ", suggestion);
        Assert.Equal(85, pick.Process?.Pid);
        Assert.StartsWith("suggested (", pick.Reason);
    }

    [Fact]
    public void Choose_a_pid_or_name_overrides_the_suggestion()
    {
        var suggestion = GameProcessPicker.Pick(Ac3);
        Assert.Equal(50, GameProcessPicker.Choose(Ac3, "50", suggestion).Process?.Pid);
        Assert.Equal("pid 50", GameProcessPicker.Choose(Ac3, "50", suggestion).Reason);
        Assert.Equal(86, GameProcessPicker.Choose(Ac3, "ShellCore", suggestion).Process?.Pid);
    }

    [Fact]
    public void Choose_explains_bad_answers()
    {
        var suggestion = GameProcessPicker.Pick(Ac3);
        Assert.Equal("pid 999: no process with that pid", GameProcessPicker.Choose(Ac3, "999", suggestion).Reason);
        var ambiguous = GameProcessPicker.Choose(Ac3, "Sce", suggestion);
        Assert.Null(ambiguous.Process);
        Assert.EndsWith("Type the pid.", ambiguous.Reason);
        Assert.Null(GameProcessPicker.Choose(SystemOnly, "", GameProcessPicker.Pick(SystemOnly)).Process);
    }

    [Fact]
    public void Shows_only_the_suggested_game_by_default()
    {
        var suggestion = GameProcessPicker.Pick(Ac3);
        var shown = GameProcessPicker.ProcessesToShow(Ac3, suggestion, showAll: false);
        Assert.Equal([suggestion.Process!], shown);
    }

    [Fact]
    public void Shows_every_process_by_pid_when_asked_or_with_no_suggestion()
    {
        var suggestion = GameProcessPicker.Pick(Ac3);
        var all = GameProcessPicker.ProcessesToShow(Ac3, suggestion, showAll: true);
        Assert.Equal(Ac3.Count, all.Count);
        Assert.Equal(Ac3.OrderBy(p => p.Pid).Select(p => p.Pid), all.Select(p => p.Pid));

        var none = GameProcessPicker.Pick(SystemOnly);
        Assert.Equal(SystemOnly.Length, GameProcessPicker.ProcessesToShow(SystemOnly, none, showAll: false).Count);
    }

    [Theory]
    [InlineData("list", true)]
    [InlineData(" LIST ", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("85", false)]
    [InlineData("lis", false)]
    public void List_command_is_recognised(string? input, bool expected) =>
        Assert.Equal(expected, GameProcessPicker.IsListCommand(input));
}
