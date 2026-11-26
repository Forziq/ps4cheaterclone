using System.Net;
using System.Net.Sockets;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Payload;
using PS4Mem.Core.Tests.Fakes;

namespace PS4Mem.Core.Tests;

public sealed class PayloadInjectorTests : IDisposable
{
    private const string Host = "127.0.0.1";

    private readonly string _dir = Directory.CreateTempSubdirectory("ps4mem-payload-").FullName;
    private readonly StringWriter _logText = new();
    private readonly List<TcpListener> _listeners = [];
    private readonly byte[] _payload = [.. Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7))];
    private readonly string _payloadPath;

    public PayloadInjectorTests()
    {
        _payloadPath = Path.Combine(_dir, "ps4debug.bin");
        File.WriteAllBytes(_payloadPath, _payload);
    }

    public void Dispose()
    {
        foreach (var l in _listeners) l.Stop();
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Short timings so the timeout test takes about 3 s, with room for a busy test run.</summary>
    private PayloadInjector Injector(int loaderPort, int commandPort) =>
        new(PS4MemSettings.Default with
        {
            PayloadPort = loaderPort,
            CommandPort = commandPort,
            PayloadPath = _payloadPath,
            PayloadStartTimeout = TimeSpan.FromSeconds(3),
            PayloadPollInterval = TimeSpan.FromMilliseconds(50),
            PortProbeTimeout = TimeSpan.FromSeconds(3),
        }, new DebugLog(_logText));

    /// <summary>Something answering on the port, like a running ps4debug (accepts and drops connects).</summary>
    private void ListenOn(int port)
    {
        var l = new TcpListener(IPAddress.Loopback, port);
        l.Start();
        _listeners.Add(l);
        _ = Task.Run(async () =>
        {
            try { while (true) (await l.AcceptTcpClientAsync()).Dispose(); }
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { }
        });
    }

    [Fact]
    public async Task Sends_the_whole_file_and_returns_when_ps4debug_answers()
    {
        var commandPort = FakeLoader.FreePort();
        // ps4debug starts a little after the payload arrives, so the injector has to poll.
        using var loader = new FakeLoader(_ => Task.Delay(200).ContinueWith(_ => ListenOn(commandPort)));
        var injector = Injector(loader.Port, commandPort);

        var lines = new List<string>();
        var result = await injector.InjectAsync(Host, report: lines.Add);

        Assert.Equal(_payload, await loader.Received);
        Assert.Equal(_payload.Length, result.BytesSent);
        Assert.Equal(loader.Port, result.LoaderPort);
        Assert.Equal(commandPort, result.CommandPort);
        Assert.Equal(_payloadPath, result.PayloadPath);
        Assert.True(result.WaitedForStart >= TimeSpan.FromMilliseconds(150), $"waited {result.WaitedForStart}");
        Assert.Contains(lines, l => l.StartsWith("Sending ps4debug.bin (300,000 bytes)"));
        Assert.Contains("[payload] ps4debug answered", _logText.ToString());
        Assert.Contains("doesn't answer yet", _logText.ToString());
    }

    [Fact]
    public async Task Refuses_to_inject_when_ps4debug_already_answers()
    {
        var commandPort = FakeLoader.FreePort();
        ListenOn(commandPort);
        using var loader = new FakeLoader();

        var e = await Assert.ThrowsAsync<PayloadException>(() => Injector(loader.Port, commandPort).InjectAsync(Host));

        Assert.Equal(PayloadErrorKind.AlreadyRunning, e.Kind);
        Assert.Contains("already answers", e.Message);
        Assert.Contains("freeze the console", e.Message);
        Assert.False(loader.Contacted, "The payload must not be sent a second time.");
    }

    [Fact]
    public async Task Refused_loader_port_explains_binloader()
    {
        var e = await Assert.ThrowsAsync<PayloadException>(() =>
            Injector(FakeLoader.FreePort(), FakeLoader.FreePort()).InjectAsync(Host));

        Assert.Equal(PayloadErrorKind.LoaderRefused, e.Kind);
        Assert.Contains("Connection refused", e.Message);
        Assert.Contains(PayloadInjector.RefusedHelp, e.Message);
        Assert.Contains("[payload] LoaderRefused", _logText.ToString());
    }

    [Fact]
    public async Task Times_out_when_ps4debug_never_answers()
    {
        using var loader = new FakeLoader();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var e = await Assert.ThrowsAsync<PayloadException>(() =>
            Injector(loader.Port, FakeLoader.FreePort()).InjectAsync(Host));

        Assert.Equal(PayloadErrorKind.NoAnswer, e.Kind);
        Assert.Contains("within 3 s", e.Message);
        Assert.Equal(_payload, await loader.Received); // it was sent, just never started
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(2.9), $"gave up after {clock.Elapsed}");
    }

    [Fact]
    public async Task Missing_payload_file_is_reported_before_anything_is_sent()
    {
        using var loader = new FakeLoader();
        var missing = Path.Combine(_dir, "nope.bin");

        var e = await Assert.ThrowsAsync<PayloadException>(() =>
            Injector(loader.Port, FakeLoader.FreePort()).InjectAsync(Host, missing));

        Assert.Equal(PayloadErrorKind.FileProblem, e.Kind);
        Assert.Contains($"{missing} was not found", e.Message);
        Assert.False(loader.Contacted);
    }

    [Fact]
    public async Task Empty_payload_file_is_refused()
    {
        using var loader = new FakeLoader();
        var empty = Path.Combine(_dir, "empty.bin");
        File.WriteAllBytes(empty, []);

        var e = await Assert.ThrowsAsync<PayloadException>(() =>
            Injector(loader.Port, FakeLoader.FreePort()).InjectAsync(Host, empty));

        Assert.Equal(PayloadErrorKind.FileProblem, e.Kind);
        Assert.Contains("is empty", e.Message);
    }

    [Fact]
    public async Task Loader_port_argument_overrides_the_settings()
    {
        var commandPort = FakeLoader.FreePort();
        using var loader = new FakeLoader(_ => ListenOn(commandPort));
        var injector = Injector(FakeLoader.FreePort(), commandPort); // settings port refuses

        var result = await injector.InjectAsync(Host, loaderPort: loader.Port);

        Assert.Equal(loader.Port, result.LoaderPort);
        Assert.Equal(_payload, await loader.Received);
    }

    [Fact]
    public async Task Bad_port_or_missing_host_is_an_argument_error()
    {
        var injector = Injector(9090, 744);
        await Assert.ThrowsAsync<ArgumentException>(() => injector.InjectAsync(Host, loaderPort: 70000));
        await Assert.ThrowsAsync<ArgumentException>(() => injector.InjectAsync(" "));
    }

    [Fact]
    public async Task Cancel_stops_the_wait()
    {
        using var loader = new FakeLoader();
        using var cts = new CancellationTokenSource();
        var injector = new PayloadInjector(PS4MemSettings.Default with
        {
            PayloadPort = loader.Port,
            CommandPort = FakeLoader.FreePort(),
            PayloadPath = _payloadPath,
            PayloadStartTimeout = TimeSpan.FromMinutes(1),
            PayloadPollInterval = TimeSpan.FromMilliseconds(50),
        }, DebugLog.Disabled);

        var task = injector.InjectAsync(Host, ct: cts.Token);
        await loader.Received;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }
}
