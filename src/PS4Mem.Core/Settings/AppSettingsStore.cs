using System.Text.Json;
using System.Text.Json.Serialization;
using PS4Mem.Core.Logging;

namespace PS4Mem.Core.Settings;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON (camelCase, the same <c>consoleIp</c> key that
/// <see cref="ConsoleIpResolver"/> reads). Thread-safe. Saves go to a temp file first and then
/// replace the real one, so a crash never leaves half a file.
/// A missing file means defaults; a broken one is logged, kept as it is, and defaults are used
/// until the next save.
/// </summary>
public sealed class AppSettingsStore
{
    private const string Cat = "settings";

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly Lock _gate = new();
    private readonly DebugLogSwitch _logs;
    private AppSettings _current;

    /// <summary>Default location: <see cref="ConsoleIpResolver.DefaultAppSettingsPath"/>.</summary>
    public static string DefaultPath => ConsoleIpResolver.DefaultAppSettingsPath;

    public string FilePath { get; }

    /// <summary>Why the file couldn't be read at startup, or null.</summary>
    public string? LoadError { get; }

    public AppSettingsStore(string? filePath = null, DebugLogSwitch? logs = null)
    {
        FilePath = Path.GetFullPath(filePath ?? DefaultPath);
        _logs = logs ?? new DebugLogSwitch();
        (_current, LoadError) = Load(FilePath);
    }

    public AppSettings Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>
    /// Applies <paramref name="change"/>, validates and saves. Throws <see cref="ArgumentException"/>
    /// (with every problem) and keeps the old settings if the result isn't valid.
    /// </summary>
    public AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        lock (_gate)
        {
            var next = change(_current);
            var errors = next.Validate();
            if (errors.Count > 0)
                throw new ArgumentException(string.Join(" ", errors));
            if (next == _current) return _current;

            Save(next);
            _current = next;
            _logs.Current.Info(Cat, $"Saved {FilePath}: {Describe(next)}");
            return next;
        }
    }

    public AppSettings Update(AppSettingsUpdate update) => Update(update.ApplyTo);

    /// <summary>Remembers a successfully used IP (current IP and front of the recent list).</summary>
    public AppSettings RememberIp(string ip, int maxRecent) => Update(s => s.WithRecentIp(ip, maxRecent));

    private void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }

    private (AppSettings, string?) Load(string path)
    {
        if (!File.Exists(path))
        {
            _logs.Current.Info(Cat, $"No settings file at {path}; using defaults");
            return (AppSettings.Default, null);
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions)
                         ?? AppSettings.Default;
            // A list missing from the file comes back null despite the default.
            loaded = loaded with { RecentIps = loaded.RecentIps ?? [] };
            var errors = loaded.Validate();
            if (errors.Count > 0)
                return Broken(path, string.Join(" ", errors));
            _logs.Current.Info(Cat, $"Loaded {path}: {Describe(loaded)}");
            return (loaded, null);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return Broken(path, e.Message);
        }
    }

    private (AppSettings, string?) Broken(string path, string reason)
    {
        var message = $"Settings file {path} can't be used ({reason}). Using defaults; the next save replaces it.";
        _logs.Current.Warn(Cat, message);
        return (AppSettings.Default, message);
    }

    private static string Describe(AppSettings s) =>
        $"console {(s.ConsoleIp.Length > 0 ? s.ConsoleIp : "(IP not set)")}:{s.CommandPort}, " +
        $"{s.RecentIps.Count} recent IPs, loader port {s.LoaderPort}, payload '{s.PayloadPath}', " +
        $"debug logging {(s.DebugLogging ? "on" : "off")}";
}
