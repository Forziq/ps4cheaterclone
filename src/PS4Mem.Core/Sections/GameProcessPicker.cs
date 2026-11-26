using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;

namespace PS4Mem.Core.Sections;

/// <summary>The picked process (null if none fits) and a short reason for the user and the log.</summary>
public sealed record ProcessPick(ProcessEntry? Process, string Reason);

/// <summary>
/// Picks the likely game from the process list (PROMPT.md §5 "Process picker"):
/// <c>--pid</c> or <c>--name</c> if given, else <c>eboot.bin</c>, else a <c>*.elf</c> that isn't a
/// system process. No process names are hard-coded here: they all come from <see cref="PS4MemSettings"/>.
/// </summary>
public static class GameProcessPicker
{
    private const string Cat = "picker";

    public static ProcessPick Pick(
        IReadOnlyList<ProcessEntry> processes,
        PS4MemSettings? settings = null,
        int? pid = null,
        string? name = null,
        DebugLog? log = null)
    {
        settings ??= PS4MemSettings.Default;
        log ??= DebugLog.Disabled;

        var pick = PickCore(processes, settings, pid, name);
        var who = pick.Process is { } p ? $"'{p.Name}' pid {p.Pid}" : "nothing";
        log.Info(Cat, $"Picked {who} from {processes.Count} processes: {pick.Reason}");
        return pick;
    }

    /// <summary>True for <c>*.elf</c> names that start with a configured system prefix.</summary>
    public static bool IsSystemProcess(string name, PS4MemSettings settings) =>
        settings.SystemProcessPrefixes.Any(prefix =>
            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static ProcessPick PickCore(
        IReadOnlyList<ProcessEntry> processes, PS4MemSettings settings, int? pid, string? name)
    {
        if (pid is { } wantedPid)
        {
            var byPid = processes.FirstOrDefault(p => p.Pid == wantedPid);
            return byPid is null
                ? new(null, $"--pid {wantedPid}: no process with that pid")
                : new(byPid, $"--pid {wantedPid}");
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            // Exact name first, then a unique "contains" match.
            var exact = processes.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count > 0)
                return Newest(exact, $"--name '{name}'");

            var partial = processes.Where(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
            return partial.Count switch
            {
                0 => new(null, $"--name '{name}': no process name matches"),
                1 => new(partial[0], $"--name '{name}' (partial match)"),
                _ => new(null, $"--name '{name}' matches {partial.Count} processes: " +
                               string.Join(", ", partial.Select(p => $"{p.Name} ({p.Pid})")) +
                               ". Use --pid."),
            };
        }

        var eboot = processes
            .Where(p => p.Name.Equals(settings.GameProcessName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (eboot.Count > 0)
            return Newest(eboot, $"name is {settings.GameProcessName}");

        var elfs = processes
            .Where(p => p.Name.EndsWith(".elf", StringComparison.OrdinalIgnoreCase)
                        && !IsSystemProcess(p.Name, settings))
            .ToList();
        if (elfs.Count > 0)
            return Newest(elfs, "*.elf that isn't a system process");

        return new(null, $"no {settings.GameProcessName} and no non-system *.elf. Is a game running? Use --pid or --name.");
    }

    /// <summary>Several matches: take the highest pid (the most recently started one) and say so.</summary>
    private static ProcessPick Newest(List<ProcessEntry> matches, string reason)
    {
        var best = matches.MaxBy(p => p.Pid)!;
        return matches.Count == 1
            ? new(best, reason)
            : new(best, $"{reason}; {matches.Count} matches, took the highest pid");
    }
}
