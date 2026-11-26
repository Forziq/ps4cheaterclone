using PS4Mem.Core;
using PS4Mem.Core.Logging;

// Global flags (work with every command):
//   --debug   write a debug log to logs/ (same as PS4MEM_DEBUG=1)
// Commands (info, sections, scan, write) arrive in T0.2–T0.4.
var debug = args.Contains("--debug");
var commandArgs = args.Where(a => a != "--debug").ToArray();

var settings = PS4MemSettings.Default with { DebugLogging = debug };
using var log = DebugLog.Create(settings);

if (log.IsEnabled)
    Console.WriteLine($"Debug log: {log.FilePath}");

log.Info("cli", $"Started with args: [{string.Join(' ', commandArgs)}]");

Console.WriteLine($"PS4 Mem CLI. Default console: {settings.ConsoleIp}:{settings.CommandPort}");
Console.WriteLine("No commands yet. Global flag: --debug (writes a log to logs/).");
