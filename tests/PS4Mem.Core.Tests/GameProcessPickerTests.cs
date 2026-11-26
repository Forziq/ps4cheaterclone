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
}
