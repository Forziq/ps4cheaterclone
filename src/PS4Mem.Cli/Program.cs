using PS4Mem.Cli;
using PS4Mem.Core;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Payload;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Sections;

// PS4 Mem CLI.
// Global flags (work with every command):
//   --debug         write a debug log to logs/ (same as PS4MEM_DEBUG=1)
//   --ip <ip>       console IP (else PS4_IP, then ps4ip.local, then app settings)
//   --port <port>   ps4debug command port (default 744)
// Commands:
//   inject          send the ps4debug payload to the loader, wait for it, then check it answers
//                   --payload <file>      payload .bin (default PS4MemSettings.PayloadPath)
//                   --loader-port <port>  loader port (default 9090; 9020/9021 on other loaders)
//   info            ps4debug version and the process list (read-only)
//   sections        choose a process, then its memory maps with the default scan selection (read-only)
//                   --pid <pid> / --name <name>   pick the process yourself
//                   --filter a,b,c                section name filter ("" = any name)
//                   --include-readonly            also select read-only sections
//                   --all                         select every readable section
//   scan            interactive first/next scan for an exact 4-byte value, list and write
//                   (same process and section options as `sections`)
//                   --method local|console            skip the question
//                   --unaligned                       scan every byte, not only 4-byte aligned

var argList = args.ToList();
var debug = TakeFlag(argList, "--debug");
var ip = TakeOption(argList, "--ip");
var port = TakeOption(argList, "--port");
var pidText = TakeOption(argList, "--pid");
var processName = TakeOption(argList, "--name");
var filterText = TakeOption(argList, "--filter");
var includeReadOnly = TakeFlag(argList, "--include-readonly");
var selectAll = TakeFlag(argList, "--all");
var method = TakeOption(argList, "--method");
var unaligned = TakeFlag(argList, "--unaligned");
var payloadPath = TakeOption(argList, "--payload");
var loaderPortText = TakeOption(argList, "--loader-port");

var settings = PS4MemSettings.Default with { DebugLogging = debug };
if (port is not null)
{
    if (!int.TryParse(port, out var p) || p is < 1 or > 65535)
        return Fail($"Invalid --port '{port}'.");
    settings = settings with { CommandPort = p };
}

if (loaderPortText is not null)
{
    if (!int.TryParse(loaderPortText, out var lp) || lp is < 1 or > 65535)
        return Fail($"Invalid --loader-port '{loaderPortText}'.");
    settings = settings with { PayloadPort = lp };
}
if (payloadPath is not null)
{
    if (payloadPath.Length == 0)
        return Fail("--payload needs a file path.");
    settings = settings with { PayloadPath = payloadPath };
}

int? pid = null;
if (pidText is not null)
{
    if (!int.TryParse(pidText, out var parsedPid))
        return Fail($"Invalid --pid '{pidText}'.");
    pid = parsedPid;
}

if (filterText is not null)
{
    var keys = filterText.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    settings = settings with { SectionNameFilter = keys };
}
if (includeReadOnly)
    settings = settings with { SectionRequireWritable = false };

// No built-in IP: it's private to each user (ConsoleIpResolver explains the order).
string? ipSource = null;
try
{
    if (ConsoleIpResolver.Resolve(ip) is { } resolved)
    {
        settings = settings with { ConsoleIp = resolved.Ip };
        ipSource = resolved.Source;
    }
}
catch (ArgumentException e)
{
    return Fail(e.Message);
}

using var log = DebugLog.Create(settings);
if (log.IsEnabled)
    Console.WriteLine($"Debug log: {log.FilePath}");
log.Info("cli", $"Started with args: [{string.Join(' ', args)}]");
log.Info("cli", ipSource is null ? "Console IP not set" : $"Console IP from {ipSource}");

var command = argList.FirstOrDefault();
switch (command)
{
    case "inject":
        return await Inject(settings, log);
    case "info":
        return Info(settings, log);
    case "sections":
        return Sections(settings, log, pid, processName, selectAll);
    case "scan":
        if (settings.ConsoleIp.Length == 0)
            return Fail(ConsoleIpResolver.NotSetMessage);
        if (method is not (null or "local" or "console"))
            return Fail($"Invalid --method '{method}' (use local or console).");
        return ScanCommand.Run(settings, log, new ScanCommand.Options(pid, processName, selectAll, method, unaligned));
    case null or "help" or "--help" or "-h":
        PrintUsage(settings);
        return 0;
    default:
        PrintUsage(settings);
        return Fail($"Unknown command '{command}'.");
}

static async Task<int> Inject(PS4MemSettings settings, DebugLog log)
{
    if (settings.ConsoleIp.Length == 0)
        return Fail(ConsoleIpResolver.NotSetMessage);

    using var cts = new CancellationTokenSource();
    ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
    Console.CancelKeyPress += onCancel;
    try
    {
        var injection = await new PayloadInjector(settings, log)
            .InjectAsync(settings.ConsoleIp, report: Console.WriteLine, ct: cts.Token);
        Console.WriteLine($"ps4debug answered after {injection.WaitedForStart.TotalSeconds:0.0} s.");

        using var client = new PS4DebugClient(settings, log);
        client.Connect();
        Console.WriteLine($"Connected: ps4debug version {client.GetVersion()}. Run `info` or `scan` next.");
        client.Disconnect();
        log.Info("cli", $"inject: {injection.BytesSent} bytes to port {injection.LoaderPort}, ps4debug answers");
        return 0;
    }
    catch (OperationCanceledException)
    {
        log.Warn("cli", "inject cancelled");
        return Fail("Cancelled.");
    }
    catch (Ps4ProtocolException e) // includes PayloadException
    {
        log.Error("cli", "inject failed", e);
        return Fail(e.Message);
    }
    finally
    {
        Console.CancelKeyPress -= onCancel;
    }
}

static int Info(PS4MemSettings settings, DebugLog log)
{
    if (settings.ConsoleIp.Length == 0)
        return Fail(ConsoleIpResolver.NotSetMessage);

    using var client = new PS4DebugClient(settings, log);
    try
    {
        Console.WriteLine($"Connecting to {settings.ConsoleIp}:{settings.CommandPort} ...");
        client.Connect();
        Console.WriteLine($"ps4debug version: {client.GetVersion()}");

        var processes = client.GetProcessList();
        Console.WriteLine($"{processes.Count} processes:");
        Console.WriteLine($"  {"PID",7}  NAME");
        foreach (var p in processes.OrderBy(p => p.Pid))
            Console.WriteLine($"  {p.Pid,7}  {p.Name}");

        client.Disconnect();
        log.Info("cli", $"info: {processes.Count} processes listed");
        return 0;
    }
    catch (Ps4ProtocolException e)
    {
        log.Error("cli", "info failed", e);
        return Fail(e.Message);
    }
}

static int Sections(PS4MemSettings settings, DebugLog log, int? pid, string? name, bool selectAll)
{
    if (settings.ConsoleIp.Length == 0)
        return Fail(ConsoleIpResolver.NotSetMessage);

    using var client = new PS4DebugClient(settings, log);
    try
    {
        Console.WriteLine($"Connecting to {settings.ConsoleIp}:{settings.CommandPort} ...");
        client.Connect();

        var processes = client.GetProcessList();
        var pick = ScanCommand.ChooseProcess(processes, settings, log, pid, name);
        if (pick.Process is not { } process)
        {
            client.Disconnect();
            return Fail($"No process chosen: {pick.Reason}");
        }

        var info = client.GetProcessInfo(process.Pid);
        var titleId = info.TitleId.Length > 0 ? info.TitleId : "unknown";
        Console.WriteLine($"Process: {process.Name} (pid {process.Pid}, title ID {titleId}), chosen by: {pick.Reason}");

        var maps = client.GetProcessMaps(process.Pid);
        client.Disconnect();

        var selection = new SectionFilter(settings).Apply(maps, log);
        if (selectAll)
            selection = SectionFilter.SelectAll(selection);

        PrintSections(selection);

        var nameRule = settings.SectionNameFilter.Count == 0
            ? "any name"
            : (settings.SectionIncludeUnnamed ? "unnamed, or " : "") +
              "name contains " + string.Join(", ", settings.SectionNameFilter);
        var rule = selectAll ? "all readable sections"
            : nameRule + (settings.SectionRequireWritable ? ", writable" : ", read-only too");
        Console.WriteLine();
        Console.WriteLine($"Selected {selection.SelectedCount} / {selection.TotalCount} sections, " +
                          $"{SizeText(selection.SelectedBytes)} / {SizeText(selection.TotalBytes)} ({rule}).");
        if (selection.SkippedMaps > 0)
            Console.WriteLine($"{selection.SkippedMaps} maps not listed (unreadable or broken; see the debug log).");

        log.Info("cli", $"sections: pid {process.Pid}, {selection.SelectedCount}/{selection.TotalCount} selected");
        return 0;
    }
    catch (Ps4ProtocolException e)
    {
        log.Error("cli", "sections failed", e);
        return Fail(e.Message);
    }
}

static void PrintSections(SectionSelection selection)
{
    Console.WriteLine($"  {"",1} {"#",4}  {"NAME",-32}  {"START",12}  {"END",12}  {"SIZE",10}  PROT");
    for (var i = 0; i < selection.Sections.Count; i++)
    {
        var s = selection.Sections[i];
        var mark = s.Selected ? "✓" : " ";
        Console.WriteLine($"  {mark,1} {i,4}  {s.DisplayName,-32}  {s.Start,12:X}  {s.End,12:X}  {SizeText(s.Size),10}  {s.Map.ProtText}");
    }
}

static void PrintUsage(PS4MemSettings s)
{
    Console.WriteLine($"""
        PS4 Mem CLI

        Usage: ps4mem <command> [--ip <ip>] [--port <port>] [--debug] [options]

        Commands:
          inject    Send the ps4debug payload to the PS4's payload loader (GoldHEN BinLoader), wait
                    up to {s.PayloadStartTimeout.TotalSeconds:0} s for ps4debug to answer, then check it does. Refuses if
                    ps4debug already runs (a second copy can freeze the console). BinLoader takes
                    one payload per session: toggle it in GoldHEN or restart the PS4 to send again.
          info      Show the ps4debug version and the process list (read-only).
          sections  Choose a process (the likely game is suggested) and list its memory sections;
                    ✓ marks the ones a scan would use (read-only).
          scan      Interactive scan for an exact 4-byte value (like money). Shows the suggested
                    game and asks which process to scan: Return takes it, `list` shows every
                    process, or type a pid or name (or use --pid/--name). After the first
                    scan, type the new value for a next scan; `l` lists the first 20 results,
                    `w <n> <value>` writes result #n (the only command that writes), `new`
                    starts again, `q` quits. Ctrl+C cancels a running scan.

        Sections and scan options:
          --pid <pid>         Use this process instead of the auto-picked game
          --name <name>       Use the process with this name (exact, or a unique part of it)
          --filter a,b,c      Section name filter (default {string.Join(",", s.SectionNameFilter)}; "" = any name)
          --include-readonly  Also select sections that aren't writable
          --all               Select every readable section

        Inject options:
          --payload <file>      Payload .bin (default {s.PayloadPath})
          --loader-port <port>  Loader port (default {s.PayloadPort}; others: {string.Join(", ", s.LoaderPortOptions.Skip(1))})

        Scan options:
          --method local|console  Compare on the Mac (default) or on the PS4 (ps4debug-NG)
          --unaligned             Check every byte, not only 4-byte aligned addresses

        Options:
          --ip     Console IP. If not given: the {ConsoleIpResolver.EnvironmentVariable} environment variable,
                   then {ConsoleIpResolver.LocalFileName} (copy ps4ip.example), then the app settings.
          --port   ps4debug command port (default {s.CommandPort})
          --debug  Write a debug log to {s.LogDirectory}/ (same as {DebugLog.EnvironmentVariable}=1)
        """);
}

static bool TakeFlag(List<string> list, string name) => list.Remove(name);

static string? TakeOption(List<string> list, string name)
{
    var i = list.IndexOf(name);
    if (i < 0) return null;
    if (i + 1 >= list.Count)
    {
        list.RemoveAt(i);
        return "";
    }
    var value = list[i + 1];
    list.RemoveRange(i, 2);
    return value;
}

internal static partial class Program
{
    internal static string SizeText(ulong bytes) => bytes switch
    {
        >= 1UL << 30 => $"{bytes / (double)(1UL << 30):0.##} GB",
        >= 1UL << 20 => $"{bytes / (double)(1UL << 20):0.##} MB",
        >= 1UL << 10 => $"{bytes / (double)(1UL << 10):0.##} KB",
        _ => $"{bytes} B",
    };

    internal static int Fail(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        return 1;
    }
}
