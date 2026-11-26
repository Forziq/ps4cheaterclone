using System.Runtime.InteropServices;
using System.Text;

namespace PS4Mem.Core.Logging;

/// <summary>
/// Optional debug log for testing sessions. Off by default; when off every call is a no-op.
/// When on, it writes timestamped lines (and hex dumps of protocol traffic) to
/// <c>logs/ps4mem-YYYYMMDD-HHMMSS.log</c>, flushing each line so a crash keeps the log.
///
/// Turn it on with <see cref="PS4MemSettings.DebugLogging"/>, the CLI's <c>--debug</c> flag,
/// or the <c>PS4MEM_DEBUG=1</c> environment variable.
/// </summary>
public sealed class DebugLog : IDisposable
{
    public const string EnvironmentVariable = "PS4MEM_DEBUG";

    private readonly TextWriter? _writer;
    private readonly int _wireDumpBytes;
    private readonly object _lock = new();

    /// <summary>A log that does nothing.</summary>
    public static DebugLog Disabled { get; } = new(null, null, 0);

    public bool IsEnabled => _writer is not null;

    /// <summary>Full path of the log file, or null when disabled or writing to a custom writer.</summary>
    public string? FilePath { get; }

    /// <summary>Writes to any TextWriter (used by tests).</summary>
    public DebugLog(TextWriter? writer, string? filePath = null, int wireDumpBytes = 256)
    {
        _writer = writer;
        FilePath = filePath;
        _wireDumpBytes = wireDumpBytes;
    }

    /// <summary>
    /// Returns an enabled file log if the settings or the environment ask for one,
    /// otherwise <see cref="Disabled"/>.
    /// </summary>
    public static DebugLog Create(PS4MemSettings settings)
    {
        if (!IsRequested(settings)) return Disabled;

        Directory.CreateDirectory(settings.LogDirectory);
        var fileName = $"ps4mem-{DateTime.Now:yyyyMMdd-HHmmss}.log";
        var path = Path.GetFullPath(Path.Combine(settings.LogDirectory, fileName));
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

        var log = new DebugLog(writer, path, settings.DebugWireDumpBytes);
        log.WriteHeader(settings);
        return log;
    }

    public static bool IsRequested(PS4MemSettings settings)
    {
        if (settings.DebugLogging) return true;
        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return env is "1" or "true" or "TRUE" or "yes";
    }

    public void Info(string category, string message) => Write("INFO ", category, message);

    public void Warn(string category, string message) => Write("WARN ", category, message);

    public void Error(string category, string message, Exception? ex = null) =>
        Write("ERROR", category, ex is null ? message : $"{message}\n{ex}");

    /// <summary>
    /// Logs bytes sent (<c>&gt;&gt;</c>) or received (<c>&lt;&lt;</c>) as a hex dump.
    /// Only the first <c>DebugWireDumpBytes</c> bytes are dumped; the total length is always shown.
    /// </summary>
    public void Wire(string category, bool sent, ReadOnlySpan<byte> data)
    {
        if (_writer is null) return;
        var shown = Math.Min(data.Length, _wireDumpBytes);
        var title = $"{(sent ? ">>" : "<<")} {data.Length} bytes" +
                    (shown < data.Length ? $" (first {shown} shown)" : "");
        Write("WIRE ", category, title + "\n" + HexDump(data[..shown]));
    }

    /// <summary>Classic 16-bytes-per-line hex dump with an ASCII column.</summary>
    public static string HexDump(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder();
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            var line = data.Slice(offset, Math.Min(16, data.Length - offset));
            sb.Append("    ").Append(offset.ToString("X4")).Append("  ");
            for (var i = 0; i < 16; i++)
            {
                sb.Append(i < line.Length ? line[i].ToString("X2") + " " : "   ");
                if (i == 7) sb.Append(' ');
            }
            sb.Append(' ');
            foreach (var b in line) sb.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            if (offset + 16 < data.Length) sb.Append('\n');
        }
        return sb.ToString();
    }

    private void WriteHeader(PS4MemSettings s)
    {
        Info("log", "PS4 Mem debug log started");
        Info("log", $"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), " +
                    $".NET {Environment.Version}");
        Info("log", $"Settings: console {(s.ConsoleIp.Length > 0 ? s.ConsoleIp : "(IP not set)")}:{s.CommandPort}, debugger port {s.DebuggerPort}, " +
                    $"loader port {s.PayloadPort}, payload '{s.PayloadPath}', " +
                    $"section filter [{string.Join(", ", s.SectionNameFilter)}]");
    }

    private void Write(string level, string category, string message)
    {
        if (_writer is null) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{category}] {message}";
        lock (_lock)
        {
            // Logging must never take the app down (disk full, log already closed ...).
            try { _writer.WriteLine(line); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        if (_writer is null) return;
        lock (_lock)
        {
            _writer.Dispose();
        }
    }
}
