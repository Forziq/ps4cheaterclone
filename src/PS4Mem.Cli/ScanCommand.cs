using System.Buffers.Binary;
using System.Globalization;
using PS4Mem.Core;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Scanning;
using PS4Mem.Core.Sections;

namespace PS4Mem.Cli;

/// <summary>
/// Interactive <c>scan</c> command (T0.4): pick the process, show the section summary, choose
/// local or console, enter a value, then loop: a new value = next scan, <c>l</c> lists results,
/// <c>w &lt;n&gt; &lt;value&gt;</c> writes (the only way this command writes), <c>new</c> starts over,
/// <c>q</c> quits. Ctrl+C cancels a running scan.
/// </summary>
internal static class ScanCommand
{
    private const int ListCount = 20;

    public sealed record Options(int? Pid, string? Name, bool SelectAll, string? Method, bool Unaligned);

    public static int Run(PS4MemSettings settings, DebugLog log, Options options)
    {
        using var client = new PS4DebugClient(settings, log);
        using var cancelHandler = new ScanCancelHandler();
        try
        {
            Console.WriteLine($"Connecting to {settings.ConsoleIp}:{settings.CommandPort} ...");
            client.Connect();

            var processes = client.GetProcessList();
            var pick = ChooseProcess(processes, settings, log, options.Pid, options.Name);
            if (pick.Process is not { } process)
            {
                client.Disconnect();
                return Program.Fail($"No process chosen: {pick.Reason}");
            }

            var info = client.GetProcessInfo(process.Pid);
            var titleId = info.TitleId.Length > 0 ? info.TitleId : "unknown";
            Console.WriteLine($"Process: {process.Name} (pid {process.Pid}, title ID {titleId}), chosen by: {pick.Reason}");

            var selection = new SectionFilter(settings).Apply(client.GetProcessMaps(process.Pid), log);
            if (options.SelectAll)
                selection = SectionFilter.SelectAll(selection);
            var sections = selection.Selected.ToList();
            Console.WriteLine($"Sections: {selection.SelectedCount} / {selection.TotalCount} selected, " +
                              $"{Program.SizeText(selection.SelectedBytes)} / {Program.SizeText(selection.TotalBytes)} " +
                              "(run `sections` for the table).");
            if (sections.Count == 0)
            {
                client.Disconnect();
                return Program.Fail("No sections selected; try --filter \"\" or --all.");
            }

            var method = ChooseMethod(client, settings, log, options.Method);
            using var storage = new ScanStorage(settings.ScanDataDirectory, log);
            var engine = new ScanEngine(client, settings, log, method.Console, storage);
            var scanOptions = new ScanOptions
            {
                Method = method.Console is null ? ScanMethod.Local : ScanMethod.Console,
                Alignment = options.Unaligned ? 1 : settings.ScanAlignment,
            };
            var editor = new MemoryEditor(client, log);
            Console.WriteLine($"Scan: {(scanOptions.Method == ScanMethod.Local ? "local (on the Mac)" : "console (on the PS4)")}, " +
                              $"exact 4-byte value, alignment {scanOptions.Alignment}. Ctrl+C cancels a scan.");

            Loop(client, engine, editor, process.Pid, sections, scanOptions, cancelHandler, log);
            client.Disconnect();
            return 0;
        }
        catch (Ps4ProtocolException e)
        {
            log.Error("cli", "scan failed", e);
            return Program.Fail(e.Message);
        }
    }

    /// <summary>
    /// Shows only the suggested game (every process if there's no suggestion) and lets the user
    /// choose: Return takes the suggestion, <c>list</c> shows every process, or type a pid or a name.
    /// The picker only suggests. <c>--pid</c>/<c>--name</c> skip the question. Used by <c>scan</c>
    /// and <c>sections</c>.
    /// </summary>
    internal static ProcessPick ChooseProcess(IReadOnlyList<ProcessEntry> processes, PS4MemSettings settings,
        DebugLog log, int? pid = null, string? name = null)
    {
        if (pid is not null || name is not null)
            return GameProcessPicker.Pick(processes, settings, pid, name, log);

        var suggestion = GameProcessPicker.Pick(processes, settings, log: log);
        PrintProcesses(processes, suggestion, showAll: false);
        if (suggestion.Process is null)
            Console.WriteLine($"No suggestion: {suggestion.Reason}");

        while (true)
        {
            var hint = suggestion.Process is { } s
                ? $"Return = {s.Name} ({s.Pid}), {GameProcessPicker.ListCommand} = show all {processes.Count}, or type a pid or name"
                : "type a pid or name";
            Console.Write($"Process to scan ({hint})> ");
            var input = Console.ReadLine();
            if (input is null) return new ProcessPick(null, "no input");
            log.Info("cli", $"process input: '{input}'");
            if (GameProcessPicker.IsListCommand(input))
            {
                PrintProcesses(processes, suggestion, showAll: true);
                continue;
            }
            var pick = GameProcessPicker.Choose(processes, input, suggestion, settings, log);
            if (pick.Process is not null) return pick;
            Console.WriteLine(pick.Reason);
        }
    }

    private static void PrintProcesses(IReadOnlyList<ProcessEntry> processes, ProcessPick suggestion, bool showAll)
    {
        var shown = GameProcessPicker.ProcessesToShow(processes, suggestion, showAll);
        var hidden = processes.Count - shown.Count;
        Console.WriteLine(hidden == 0
            ? $"{processes.Count} processes (→ = suggested):"
            : $"Suggested game ({hidden} other processes hidden; type `{GameProcessPicker.ListCommand}` to show them):");
        Console.WriteLine($"  {"PID",7}  NAME");
        foreach (var p in shown)
        {
            var mark = p == suggestion.Process ? $"   → suggested: {suggestion.Reason}" : "";
            Console.WriteLine($"  {p.Pid,7}  {p.Name}{mark}");
        }
    }

    private static void Loop(PS4DebugClient client, ScanEngine engine, MemoryEditor editor, int pid,
        List<Section> sections, ScanOptions scanOptions, ScanCancelHandler cancel, DebugLog log)
    {
        ScanResults? results = null;
        try
        {
            Loop(client, engine, editor, pid, sections, scanOptions, cancel, log, ref results);
        }
        finally
        {
            results?.Dispose(); // frees memory and deletes spilled files
        }
    }

    private static void Loop(PS4DebugClient client, ScanEngine engine, MemoryEditor editor, int pid,
        List<Section> sections, ScanOptions scanOptions, ScanCancelHandler cancel, DebugLog log, ref ScanResults? results)
    {
        Console.WriteLine();
        Console.WriteLine("Enter a value to scan for (decimal, or 0x hex). Commands: l = list, w <n> <value> = write, new, q = quit.");

        while (true)
        {
            Console.Write(results is null ? "first scan value> " : $"[{results.Count:N0} results] next value> ");
            var line = Console.ReadLine();
            if (line is null) return; // end of input
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;
            log.Info("cli", $"scan input: {line}");

            switch (parts[0].ToLowerInvariant())
            {
                case "q" or "quit" or "exit":
                    return;
                case "new":
                    results?.Dispose();
                    results = null;
                    Console.WriteLine("Results cleared. Enter a value for a new first scan.");
                    continue;
                case "l" or "list":
                    if (results is null) Console.WriteLine("No results yet.");
                    else List(client, pid, results);
                    continue;
                case "w" or "write":
                    Write(editor, pid, results, parts);
                    continue;
            }

            if (!TryParseU32(parts[0], out var value))
            {
                Console.WriteLine($"'{parts[0]}' isn't a 4-byte value or a command (l, w <n> <value>, new, q).");
                continue;
            }

            var previous = results;
            var next = cancel.RunScan(token => previous is null
                ? engine.FirstScan(pid, sections, value, WithScreen(scanOptions, firstScan: true), token)
                : engine.NextScan(previous, value, WithScreen(scanOptions, firstScan: false), token));
            if (next is null)
            {
                Console.WriteLine();
                Console.WriteLine(results is null ? "Scan cancelled." : "Scan cancelled; previous results kept.");
                continue;
            }

            results?.Dispose();
            results = next;
            Console.WriteLine();
            var withHits = results.Sections.Count(s => s.Hits > 0);
            Console.WriteLine($"Scan #{results.ScanNumber} ({results.Method}): {results.Count:N0} results in " +
                              $"{withHits} / {results.Sections.Count} sections, {results.Elapsed.TotalSeconds:0.0} s.");
            if (results.Count == 0)
                Console.WriteLine("No results. Type `new` to start again.");
            else if (results.Count <= ListCount)
                List(client, pid, results);
        }
    }

    /// <summary>Adds an on-screen progress line and per-section lines to the scan options.</summary>
    private static ScanOptions WithScreen(ScanOptions options, bool firstScan)
    {
        var lastDraw = DateTime.MinValue;
        return options with
        {
            Progress = p =>
            {
                if (DateTime.UtcNow - lastDraw < TimeSpan.FromMilliseconds(200) && p.Done < p.Total) return;
                lastDraw = DateTime.UtcNow;
                var amount = firstScan
                    ? $"{Program.SizeText((ulong)p.Done)} / {Program.SizeText((ulong)p.Total)}"
                    : $"{p.Done:N0} / {p.Total:N0} results";
                Console.Write($"\r  {p.Fraction * 100,5:0.0}%  {amount}  {p.Hits:N0} hits  ".PadRight(60));
            },
            SectionDone = s =>
            {
                if (!firstScan && s.Hits == 0) return; // next scans: only sections that still have hits
                Console.Write("\r".PadRight(61) + "\r");
                Console.WriteLine($"  {s.Section.DisplayName,-32}  {s.Section.Start,12:X}  {Program.SizeText(s.Section.Size),10}  " +
                                  $"{s.Hits,9:N0} hits  {s.Elapsed.TotalMilliseconds,7:0} ms");
            },
        };
    }

    private static void List(PS4DebugClient client, int pid, ScanResults results)
    {
        var count = Math.Min(ListCount, results.Count);
        Console.WriteLine($"  {"#",4}  {"ADDRESS",12}  {"FOUND",12}  {"NOW",12}  SECTION");
        var now = new byte[4];
        for (var i = 0; i < count; i++)
        {
            client.ReadMemory(pid, results.AddressAt(i), now);
            var current = BinaryPrimitives.ReadUInt32LittleEndian(now);
            Console.WriteLine($"  {i,4}  {results.AddressAt(i),12:X}  {results.FormatValue(i),12}  {current,12}  {results.SectionOf(i).DisplayName}");
        }
        if (results.Count > count)
            Console.WriteLine($"  ... and {results.Count - count:N0} more.");
    }

    private static void Write(MemoryEditor editor, int pid, ScanResults? results, string[] parts)
    {
        if (results is null || results.Count == 0)
        {
            Console.WriteLine("Nothing to write to: scan first.");
            return;
        }
        if (parts.Length != 3 || !int.TryParse(parts[1], out var n) || !TryParseU32(parts[2], out var value))
        {
            Console.WriteLine("Usage: w <n> <value>   (n is the # from `l`, value is decimal or 0x hex)");
            return;
        }
        if (n < 0 || n >= results.Count)
        {
            Console.WriteLine($"No result #{n}; there are {results.Count:N0} (0 to {results.Count - 1}).");
            return;
        }

        var address = results.AddressAt(n);
        try
        {
            var old = editor.WriteUInt32(pid, address, value);
            Console.WriteLine($"Wrote {value} to 0x{address:X} (was {old}).");
        }
        catch (Ps4StatusException e) when (!e.ConnectionClosed)
        {
            Console.WriteLine($"Write failed: {e.Message}");
        }
    }

    private static (ISectionScanner? Console, string Reason) ChooseMethod(PS4DebugClient client,
        PS4MemSettings settings, DebugLog log, string? requested)
    {
        var method = requested?.ToLowerInvariant();
        while (method is not ("local" or "console"))
        {
            Console.Write("Scan on the [l]ocal Mac (default, reads memory over the network) or the [c]onsole? ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            method = answer switch
            {
                null or "" or "l" or "local" => "local",
                "c" or "console" => "console",
                _ => null,
            };
        }
        log.Info("cli", $"scan method requested: {method}");
        if (method == "local")
            return (null, "local requested");

        var (scanner, reason) = ConsoleSectionScanner.TryCreate(client, settings, log);
        Console.WriteLine(scanner is null
            ? $"Console scan isn't available ({reason}); using local scan."
            : $"Console scan: {reason}.");
        return (scanner, reason);
    }

    /// <summary>Decimal, or hex with a 0x prefix.</summary>
    private static bool TryParseU32(string text, out uint value)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Ctrl+C cancels the running scan instead of quitting; with no scan running it quits as usual.</summary>
    private sealed class ScanCancelHandler : IDisposable
    {
        private CancellationTokenSource? _running;

        public ScanCancelHandler() => Console.CancelKeyPress += OnCancel;

        public ScanResults? RunScan(Func<CancellationToken, ScanResults> scan)
        {
            using var cts = new CancellationTokenSource();
            _running = cts;
            try
            {
                return scan(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            finally
            {
                _running = null;
            }
        }

        private void OnCancel(object? sender, ConsoleCancelEventArgs e)
        {
            if (_running is not { } cts) return;
            e.Cancel = true;
            cts.Cancel();
        }

        public void Dispose() => Console.CancelKeyPress -= OnCancel;
    }
}
