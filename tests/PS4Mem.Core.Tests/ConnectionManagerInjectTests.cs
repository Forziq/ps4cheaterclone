using PS4Mem.Core.Connection;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Payload;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Settings;
using PS4Mem.Core.Tests.Fakes;
using static PS4Mem.Core.Tests.Fakes.FakeConsole;

namespace PS4Mem.Core.Tests;

public sealed class ConnectionManagerInjectTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ps4mem-inject-").FullName;
    private readonly StringWriter _logText = new();
    private readonly DebugLogSwitch _logs;
    private readonly AppSettingsStore _store;
    private readonly string _payloadPath;
    private readonly byte[] _payload = [1, 2, 3, 4, 5, 6, 7, 8];
    private FakePs4Server? _ps4debug;

    public ConnectionManagerInjectTests()
    {
        _logs = new DebugLogSwitch(new DebugLog(_logText));
        _store = new AppSettingsStore(Path.Combine(_dir, "settings.json"), _logs);
        _payloadPath = Path.Combine(_dir, "ps4debug.bin");
        File.WriteAllBytes(_payloadPath, _payload);
    }

    public void Dispose()
    {
        _ps4debug?.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private static readonly PS4MemSettings Fast = PS4MemSettings.Default with
    {
        PayloadStartTimeout = TimeSpan.FromSeconds(1),
        PayloadPollInterval = TimeSpan.FromMilliseconds(50),
        PortProbeTimeout = TimeSpan.FromMilliseconds(500),
    };

    private ConnectionManager Manager(int commandPort, int loaderPort)
    {
        _store.Update(new AppSettingsUpdate { CommandPort = commandPort, LoaderPort = loaderPort, PayloadPath = _payloadPath });
        return new ConnectionManager(_store, IpSources.None, _logs, Fast);
    }

    /// <summary>
    /// The fake ps4debug the payload "starts": the injector's port check (connect, close), then
    /// the manager's connection, which reads the version.
    /// </summary>
    private void StartPs4Debug(int port) => _ps4debug = new FakePs4Server(port,
        c => Assert.True(c.ClientClosed()),
        c =>
        {
            c.Expect(Ps4Command.Version, 0);
            c.Send(U32(3), "1.3"u8.ToArray());
        });

    [Fact]
    public async Task Inject_sends_the_payload_then_connects()
    {
        var commandPort = FakeLoader.FreePort();
        using var loader = new FakeLoader(_ => StartPs4Debug(commandPort));
        using var manager = Manager(commandPort, loader.Port);

        var lines = new List<string>();
        var result = await manager.InjectAsync("127.0.0.1", report: lines.Add);
        _ps4debug!.WaitForScript();

        Assert.Equal(_payload, await loader.Received);
        Assert.True(result.Status.Connected);
        Assert.Equal("1.3", result.Status.Version);
        Assert.Equal(commandPort, result.Status.Port);
        Assert.Equal(result.Status, manager.Status);
        Assert.Equal(_payload.Length, result.Injection.BytesSent);
        Assert.Contains(lines, l => l.StartsWith("ps4debug answered after"));
        Assert.Equal("127.0.0.1", _store.Current.ConsoleIp); // remembered like a connect
        Assert.Contains("[connection] Inject the payload into 127.0.0.1", _logText.ToString());
    }

    [Fact]
    public async Task Inject_saves_the_payload_path_and_loader_port_that_worked()
    {
        var commandPort = FakeLoader.FreePort();
        using var loader = new FakeLoader(_ => StartPs4Debug(commandPort));
        using var manager = Manager(commandPort, loaderPort: 9090);
        var otherPath = Path.Combine(_dir, "other.bin");
        File.WriteAllBytes(otherPath, _payload);

        await manager.InjectAsync("127.0.0.1", otherPath, loader.Port);

        Assert.Equal(otherPath, _store.Current.PayloadPath);
        Assert.Equal(loader.Port, _store.Current.LoaderPort);
    }

    [Fact]
    public async Task Refused_loader_keeps_the_settings_and_shows_the_error_in_status()
    {
        using var manager = Manager(FakeLoader.FreePort(), loaderPort: 9090);
        var refusedPort = FakeLoader.FreePort();

        var e = await Assert.ThrowsAsync<PayloadException>(() => manager.InjectAsync("127.0.0.1", loaderPort: refusedPort));

        Assert.Equal(PayloadErrorKind.LoaderRefused, e.Kind);
        Assert.Equal(9090, _store.Current.LoaderPort);
        Assert.False(manager.Status.Connected);
        Assert.Equal(e.Message, manager.Status.LastError);
    }

    [Fact]
    public async Task Inject_while_connected_is_refused()
    {
        using var server = new FakePs4Server(c =>
        {
            c.Expect(Ps4Command.Version, 0);
            c.Send(U32(3), "1.3"u8.ToArray());
        });
        using var loader = new FakeLoader();
        using var manager = Manager(server.Port, loader.Port);
        manager.Connect("127.0.0.1");
        server.WaitForScript();

        var e = await Assert.ThrowsAsync<PayloadException>(() => manager.InjectAsync());

        Assert.Equal(PayloadErrorKind.AlreadyRunning, e.Kind);
        Assert.Contains("Already connected", e.Message);
        Assert.False(loader.Contacted);
    }

    [Fact]
    public async Task Inject_without_an_ip_is_an_argument_error()
    {
        using var manager = new ConnectionManager(_store, IpSources.None, _logs, Fast);
        var e = await Assert.ThrowsAsync<ArgumentException>(() => manager.InjectAsync());
        Assert.Contains("PS4 IP is not set", e.Message);
    }
}
