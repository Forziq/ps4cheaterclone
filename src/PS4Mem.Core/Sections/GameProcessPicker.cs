using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;

namespace PS4Mem.Core.Sections;

/// <summary>The picked process (null if none fits) and a short reason for the user and the log.</summary>
public sealed record ProcessPick(ProcessEntry? Process, string Reason);

/// <summary>
/// Suggests the likely game from the process list (PROMPT.md §5 "Process picker"):
/// <c>--pid</c> or <c>--name</c> if given, else <c>eboot.bin</c>, else a <c>*.elf</c> that isn't a
/// system process. The suggestion is only that: the scan command lets the user choose
/// (<see cref="Choose"/>). No process names are hard-coded here: they all come from <see cref="PS4MemSettings"/>.
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

        var pick = PickCore(processes, settings, pid, name, interactive: false);
        Log(log, processes, pick);
        return pick;
    }

    /// <summary>
    /// The user's answer at the process prompt: empty = take <paramref name="suggestion"/>,
    /// a number = that pid, anything else = a process name (exact, or a unique part of it).
    /// The suggestion is only a suggestion; the user decides.
    /// </summary>
    public static ProcessPick Choose(
        IReadOnlyList<ProcessEntry> processes,
        string? input,
        ProcessPick suggestion,
        PS4MemSettings? settings = null,
        DebugLog? log = null)
    {
        settings ??= PS4MemSettings.Default;
        log ??= DebugLog.Disabled;
        input = input?.Trim();

        ProcessPick pick;
        if (string.IsNullOrEmpty(input))
            pick = suggestion.Process is null
                ? new(null, "there's no suggestion; type a pid or a name")
                : suggestion with { Reason = $"suggested ({suggestion.Reason})" };
        else if (int.TryParse(input, out var pid))
            pick = PickCore(processes, settings, pid, null, interactive: true);
        else
            pick = PickCore(processes, settings, null, input, interactive: true);

        Log(log, processes, pick);
        return pick;
    }

    /// <summary>Typed at the process prompt to show every process instead of only the suggested game.</summary>
    public const string ListCommand = "list";

    public static bool IsListCommand(string? input) =>
        string.Equals(input?.Trim(), ListCommand, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The processes to print at the prompt: only the suggested game, so the user isn't shown 60+
    /// system processes. Every process (by pid) when <paramref name="showAll"/> is set or there's
    /// no suggestion, since the user then has to pick one themselves.
    /// </summary>
    public static IReadOnlyList<ProcessEntry> ProcessesToShow(
        IReadOnlyList<ProcessEntry> processes, ProcessPick suggestion, bool showAll) =>
        showAll || suggestion.Process is null
            ? processes.OrderBy(p => p.Pid).ToList()
            : [suggestion.Process];

    private static void Log(DebugLog log, IReadOnlyList<ProcessEntry> processes, ProcessPick pick)
    {
        var who = pick.Process is { } p ? $"'{p.Name}' pid {p.Pid}" : "nothing";
        log.Info(Cat, $"Picked {who} from {processes.Count} processes: {pick.Reason}");
    }

    /// <summary>True for <c>*.elf</c> names that start with a configured system prefix.</summary>
    public static bool IsSystemProcess(string name, PS4MemSettings settings) =>
        settings.SystemProcessPrefixes.Any(prefix =>
            name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <param name="interactive">Typed at the prompt rather than given as --pid/--name (only changes the wording).</param>
    private static ProcessPick PickCore(
        IReadOnlyList<ProcessEntry> processes, PS4MemSettings settings, int? pid, string? name, bool interactive)
    {
        var (pidLabel, nameLabel, usePid) = interactive
            ? ("pid", "name", "Type the pid.")
            : ("--pid", "--name", "Use --pid.");

        if (pid is { } wantedPid)
        {
            var byPid = processes.FirstOrDefault(p => p.Pid == wantedPid);
            return byPid is null
                ? new(null, $"{pidLabel} {wantedPid}: no process with that pid")
                : new(byPid, $"{pidLabel} {wantedPid}");
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            // Exact name first, then a unique "contains" match.
            var exact = processes.Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count > 0)
                return Newest(exact, $"{nameLabel} '{name}'");

            var partial = processes.Where(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
            return partial.Count switch
            {
                0 => new(null, $"{nameLabel} '{name}': no process name matches"),
                1 => new(partial[0], $"{nameLabel} '{name}' (partial match)"),
                _ => new(null, $"{nameLabel} '{name}' matches {partial.Count} processes: " +
                               string.Join(", ", partial.Select(p => $"{p.Name} ({p.Pid})")) +
                               $". {usePid}"),
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
