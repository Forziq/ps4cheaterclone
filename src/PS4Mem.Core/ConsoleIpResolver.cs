using System.Net;
using System.Text.Json;

namespace PS4Mem.Core;

/// <summary>Where the console IP came from, for messages and the debug log.</summary>
public sealed record ResolvedConsoleIp(string Ip, string Source);

/// <summary>
/// Finds the PS4's IP. There is no built-in default: the IP is private to each user.
/// Order: <c>--ip</c>, then the <c>PS4_IP</c> environment variable, then <c>ps4ip.local</c>
/// (in the working directory or any parent), then the app settings file.
/// </summary>
public static class ConsoleIpResolver
{
    public const string EnvironmentVariable = "PS4_IP";
    public const string LocalFileName = "ps4ip.local";

    /// <summary><c>~/Library/Application Support/PS4Mem/settings.json</c> (key <c>consoleIp</c>).</summary>
    public static string DefaultAppSettingsPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support", "PS4Mem", "settings.json");

    public static string NotSetMessage { get; } =
        "The PS4 IP is not set. Pass --ip <ip>, set the PS4_IP environment variable, " +
        "or copy ps4ip.example to ps4ip.local and put your PS4's IP in it.";

    /// <summary>Resolves from the real command line value, environment, working directory and app settings.</summary>
    public static ResolvedConsoleIp? Resolve(string? cliIp) => Resolve(
        cliIp,
        Environment.GetEnvironmentVariable(EnvironmentVariable),
        Directory.GetCurrentDirectory(),
        DefaultAppSettingsPath);

    /// <summary>
    /// Resolves from the given sources (all optional). Returns null when none is set.
    /// Throws <see cref="ArgumentException"/> when a source is set but isn't a valid IP.
    /// </summary>
    public static ResolvedConsoleIp? Resolve(string? cliIp, string? envIp, string? startDirectory, string? appSettingsPath)
    {
        if (!string.IsNullOrWhiteSpace(cliIp))
            return Checked(cliIp, "--ip");

        if (!string.IsNullOrWhiteSpace(envIp))
            return Checked(envIp, $"{EnvironmentVariable} environment variable");

        var localFile = FindLocalFile(startDirectory);
        if (localFile is not null && ReadLocalFile(localFile) is { } fileIp)
            return Checked(fileIp, localFile);

        if (appSettingsPath is not null && ReadAppSettings(appSettingsPath) is { } settingsIp)
            return Checked(settingsIp, appSettingsPath);

        return null;
    }

    /// <summary>The nearest <c>ps4ip.local</c> in <paramref name="startDirectory"/> or a parent.</summary>
    public static string? FindLocalFile(string? startDirectory)
    {
        for (var dir = startDirectory is null ? null : new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, LocalFileName);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>Reads <c>PS4_IP=...</c> from a ps4ip.local file. Blank lines and <c>#</c> comments are skipped.</summary>
    public static string? ReadLocalFile(string path)
    {
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq < 0 || line[..eq].Trim() != EnvironmentVariable) continue;
            var value = line[(eq + 1)..].Trim().Trim('"', '\'');
            return value.Length == 0 ? null : value;
        }
        return null;
    }

    private static string? ReadAppSettings(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name.Equals("consoleIp", StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(prop.Value.GetString()))
                    return prop.Value.GetString();
            }
        }
        catch (JsonException)
        {
            // A broken settings file shouldn't stop the CLI; the "not set" message explains what to do.
        }
        return null;
    }

    private static ResolvedConsoleIp Checked(string value, string source)
    {
        var ip = value.Trim();
        if (!IPAddress.TryParse(ip, out _))
            throw new ArgumentException($"'{ip}' from {source} is not a valid IP address.");
        return new ResolvedConsoleIp(ip, source);
    }
}
