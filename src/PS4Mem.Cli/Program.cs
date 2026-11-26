using PS4Mem.Core;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;

// PS4 Mem CLI.
// Global flags (work with every command):
//   --debug         write a debug log to logs/ (same as PS4MEM_DEBUG=1)
//   --ip <ip>       console IP (default from PS4MemSettings)
//   --port <port>   ps4debug command port (default 744)
// Commands:
//   info            ps4debug version and the process list (read-only)
// More commands (sections, scan, write) arrive in T0.3–T0.4.

var argList = args.ToList();
var debug = TakeFlag(argList, "--debug");
var ip = TakeOption(argList, "--ip");
var port = TakeOption(argList, "--port");

var settings = PS4MemSettings.Default with { DebugLogging = debug };
if (ip is not null) settings = settings with { ConsoleIp = ip };
if (port is not null)
{
    if (!int.TryParse(port, out var p) || p is < 1 or > 65535)
        return Fail($"Invalid --port '{port}'.");
    settings = settings with { CommandPort = p };
}

using var log = DebugLog.Create(settings);
if (log.IsEnabled)
    Console.WriteLine($"Debug log: {log.FilePath}");
log.Info("cli", $"Started with args: [{string.Join(' ', args)}]");

var command = argList.FirstOrDefault();
switch (command)
{
    case "info":
        return Info(settings, log);
    case null or "help" or "--help" or "-h":
        PrintUsage(settings);
        return 0;
    default:
        PrintUsage(settings);
        return Fail($"Unknown command '{command}'.");
}

static int Info(PS4MemSettings settings, DebugLog log)
{
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

static void PrintUsage(PS4MemSettings s)
{
    Console.WriteLine($"""
        PS4 Mem CLI

        Usage: ps4mem <command> [--ip <ip>] [--port <port>] [--debug]

        Commands:
          info     Show the ps4debug version and the process list (read-only).

        Options:
          --ip     Console IP (default {s.ConsoleIp})
          --port   ps4debug command port (default {s.CommandPort})
          --debug  Write a debug log to {s.LogDirectory}/ (same as {DebugLog.EnvironmentVariable}=1)
        """);
}

static int Fail(string message)
{
    Console.Error.WriteLine($"Error: {message}");
    return 1;
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
