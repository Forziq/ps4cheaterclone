using System.Text.Json;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Settings;

namespace PS4Mem.Core.Tests;

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ps4mem-settings-").FullName;
    private string SettingsPath => Path.Combine(_dir, "PS4Mem", "settings.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Missing_file_gives_the_defaults_from_PS4MemSettings()
    {
        var store = new AppSettingsStore(SettingsPath);

        var s = store.Current;
        Assert.Null(store.LoadError);
        Assert.Equal("", s.ConsoleIp);
        Assert.Empty(s.RecentIps);
        Assert.Equal(744, s.CommandPort);
        Assert.Equal(9090, s.LoaderPort);
        Assert.Equal("payloads/ps4debug-ng_v1.3.4_release_2026-11-05.bin", s.PayloadPath);
        Assert.False(s.DebugLogging);
        Assert.False(File.Exists(SettingsPath)); // nothing written until something changes
    }

    [Fact]
    public void Update_saves_camelCase_json_that_a_new_store_reads_back()
    {
        var store = new AppSettingsStore(SettingsPath);
        store.Update(new AppSettingsUpdate { ConsoleIp = "192.0.2.10", LoaderPort = 9021, DebugLogging = true });

        var json = File.ReadAllText(SettingsPath);
        Assert.Contains("\"consoleIp\": \"192.0.2.10\"", json); // the key ConsoleIpResolver reads
        Assert.Contains("\"loaderPort\": 9021", json);

        var reloaded = new AppSettingsStore(SettingsPath).Current;
        Assert.Equal("192.0.2.10", reloaded.ConsoleIp);
        Assert.Equal(9021, reloaded.LoaderPort);
        Assert.True(reloaded.DebugLogging);
        Assert.Equal(744, reloaded.CommandPort); // untouched field keeps its value
    }

    [Fact]
    public void Saved_file_is_found_by_ConsoleIpResolver()
    {
        new AppSettingsStore(SettingsPath).Update(new AppSettingsUpdate { ConsoleIp = "192.0.2.10" });

        var resolved = ConsoleIpResolver.Resolve(null, null, null, SettingsPath);
        Assert.Equal("192.0.2.10", resolved?.Ip);
    }

    [Fact]
    public void Invalid_update_lists_every_problem_and_changes_nothing()
    {
        var store = new AppSettingsStore(SettingsPath);
        store.Update(new AppSettingsUpdate { LoaderPort = 9020 });

        var e = Assert.Throws<ArgumentException>(() => store.Update(new AppSettingsUpdate
        {
            ConsoleIp = "not-an-ip", CommandPort = 0, PayloadPath = " ",
        }));
        Assert.Contains("consoleIp", e.Message);
        Assert.Contains("commandPort", e.Message);
        Assert.Contains("payloadPath", e.Message);

        Assert.Equal(9020, store.Current.LoaderPort);
        Assert.Equal("", store.Current.ConsoleIp);
        Assert.Equal(9020, new AppSettingsStore(SettingsPath).Current.LoaderPort);
    }

    [Fact]
    public void Empty_console_ip_is_allowed_to_clear_it()
    {
        var store = new AppSettingsStore(SettingsPath);
        store.Update(new AppSettingsUpdate { ConsoleIp = "192.0.2.10" });
        store.Update(new AppSettingsUpdate { ConsoleIp = "" });
        Assert.Equal("", store.Current.ConsoleIp);
    }

    [Fact]
    public void Broken_file_falls_back_to_defaults_and_says_why()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, "{ this is not json");

        var store = new AppSettingsStore(SettingsPath);

        Assert.NotNull(store.LoadError);
        Assert.Contains(SettingsPath, store.LoadError);
        Assert.Equal(AppSettings.Default, store.Current);
        Assert.Equal("{ this is not json", File.ReadAllText(SettingsPath)); // kept until the next save
    }

    [Fact]
    public void File_with_missing_and_unknown_keys_still_loads()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, """{ "consoleIp": "192.0.2.10", "somethingNew": 1 }""");

        var s = new AppSettingsStore(SettingsPath).Current;

        Assert.Equal("192.0.2.10", s.ConsoleIp);
        Assert.Empty(s.RecentIps);
        Assert.Equal(9090, s.LoaderPort);
    }

    [Fact]
    public void RememberIp_puts_the_ip_first_without_duplicates_and_caps_the_list()
    {
        var store = new AppSettingsStore(SettingsPath);
        foreach (var last in new[] { 1, 2, 3, 4 })
            store.RememberIp($"192.0.2.{last}", maxRecent: 3);
        store.RememberIp("192.0.2.3", maxRecent: 3);

        Assert.Equal(["192.0.2.3", "192.0.2.4", "192.0.2.2"], store.Current.RecentIps);
        Assert.Equal("192.0.2.3", store.Current.ConsoleIp);
    }

    [Fact]
    public void ApplyTo_maps_onto_the_core_settings()
    {
        var app = AppSettings.Default with
        {
            ConsoleIp = "192.0.2.10", CommandPort = 7440, LoaderPort = 9021, PayloadPath = "x.bin", DebugLogging = true,
        };

        var core = app.ApplyTo(PS4MemSettings.Default);

        Assert.Equal("192.0.2.10", core.ConsoleIp);
        Assert.Equal(7440, core.CommandPort);
        Assert.Equal(9021, core.PayloadPort);
        Assert.Equal("x.bin", core.PayloadPath);
        Assert.True(core.DebugLogging);
        Assert.Equal(755, core.DebuggerPort); // not an app setting: stays the default
    }

    [Fact]
    public void Update_and_load_write_to_the_debug_log()
    {
        var writer = new StringWriter();
        var logs = new DebugLogSwitch(new DebugLog(writer));
        var store = new AppSettingsStore(SettingsPath, logs);
        store.Update(new AppSettingsUpdate { LoaderPort = 9021 });

        var text = writer.ToString();
        Assert.Contains("[settings] No settings file", text);
        Assert.Contains("[settings] Saved", text);
        Assert.Contains("loader port 9021", text);
    }

    [Fact]
    public void DebugLogSwitch_opens_and_closes_a_log_file()
    {
        var logDir = Path.Combine(_dir, "logs");
        var settings = PS4MemSettings.Default with { LogDirectory = logDir };
        using var logs = new DebugLogSwitch();

        if (DebugLog.IsRequested(settings))
            return; // PS4MEM_DEBUG=1 in the environment forces logging on; nothing to test.

        Assert.False(logs.Apply(settings).IsEnabled);
        var on = logs.Apply(settings with { DebugLogging = true });
        Assert.True(on.IsEnabled);
        Assert.Same(on, logs.Apply(settings with { DebugLogging = true })); // no second file
        Assert.False(logs.Apply(settings).IsEnabled);
        Assert.Single(Directory.GetFiles(logDir));
    }

    [Fact]
    public void Json_options_use_camelCase()
    {
        var json = JsonSerializer.Serialize(AppSettings.Default, AppSettingsStore.JsonOptions);
        Assert.Contains("\"recentIps\"", json);
        Assert.Contains("\"debugLogging\"", json);
    }
}
