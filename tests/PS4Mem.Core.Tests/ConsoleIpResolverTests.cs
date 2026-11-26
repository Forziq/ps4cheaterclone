using PS4Mem.Core;

namespace PS4Mem.Core.Tests;

public class ConsoleIpResolverTests : IDisposable
{
    // Documentation addresses (RFC 5737), never a real console.
    private const string CliIp = "192.0.2.10";
    private const string EnvIp = "192.0.2.11";
    private const string FileIp = "192.0.2.12";
    private const string SettingsIp = "192.0.2.13";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ps4mem-ip-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _child;
    private readonly string _settingsPath;

    public ConsoleIpResolverTests()
    {
        _child = Path.Combine(_root, "src", "PS4Mem.Cli");
        Directory.CreateDirectory(_child);
        _settingsPath = Path.Combine(_root, "settings.json");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void WriteLocalFile(string content) =>
        File.WriteAllText(Path.Combine(_root, ConsoleIpResolver.LocalFileName), content);

    private void WriteSettings(string json) => File.WriteAllText(_settingsPath, json);

    [Fact]
    public void Cli_value_wins_over_everything()
    {
        WriteLocalFile($"PS4_IP={FileIp}\n");
        WriteSettings($$"""{"consoleIp":"{{SettingsIp}}"}""");

        var r = ConsoleIpResolver.Resolve(CliIp, EnvIp, _child, _settingsPath);

        Assert.Equal(new ResolvedConsoleIp(CliIp, "--ip"), r);
    }

    [Fact]
    public void Environment_variable_comes_second()
    {
        WriteLocalFile($"PS4_IP={FileIp}\n");

        var r = ConsoleIpResolver.Resolve(null, EnvIp, _child, _settingsPath);

        Assert.Equal(EnvIp, r!.Ip);
        Assert.Contains("PS4_IP", r.Source);
    }

    [Fact]
    public void Local_file_is_found_in_a_parent_directory()
    {
        WriteLocalFile($"# my console\n\nPS4_IP = \"{FileIp}\"\n");
        WriteSettings($$"""{"consoleIp":"{{SettingsIp}}"}""");

        var r = ConsoleIpResolver.Resolve(null, "  ", _child, _settingsPath);

        Assert.Equal(FileIp, r!.Ip);
        Assert.Equal(Path.Combine(_root, ConsoleIpResolver.LocalFileName), r.Source);
    }

    [Fact]
    public void App_settings_come_last()
    {
        WriteSettings($$"""{"debugLogging":true,"ConsoleIp":"{{SettingsIp}}"}""");

        var r = ConsoleIpResolver.Resolve(null, null, _child, _settingsPath);

        Assert.Equal(new ResolvedConsoleIp(SettingsIp, _settingsPath), r);
    }

    [Fact]
    public void Local_file_without_a_value_falls_through_to_settings()
    {
        WriteLocalFile("# PS4_IP not filled in yet\nPS4_IP=\n");
        WriteSettings($$"""{"consoleIp":"{{SettingsIp}}"}""");

        var r = ConsoleIpResolver.Resolve(null, null, _child, _settingsPath);

        Assert.Equal(SettingsIp, r!.Ip);
    }

    [Fact]
    public void Returns_null_when_nothing_is_set()
    {
        WriteSettings("not json at all");

        Assert.Null(ConsoleIpResolver.Resolve(null, null, _child, _settingsPath));
        Assert.Null(ConsoleIpResolver.Resolve(null, null, null, null));
        Assert.Contains("ps4ip.local", ConsoleIpResolver.NotSetMessage);
    }

    [Fact]
    public void Example_placeholder_is_rejected_with_its_source()
    {
        WriteLocalFile("PS4_IP=192.168.x.x\n");

        var ex = Assert.Throws<ArgumentException>(() => ConsoleIpResolver.Resolve(null, null, _child, null));

        Assert.Contains("192.168.x.x", ex.Message);
        Assert.Contains(ConsoleIpResolver.LocalFileName, ex.Message);
    }
}
