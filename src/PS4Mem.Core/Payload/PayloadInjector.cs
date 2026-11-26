using System.Diagnostics;
using System.Net.Sockets;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;

namespace PS4Mem.Core.Payload;

/// <summary>Why an injection stopped. The API turns these into status codes.</summary>
public enum PayloadErrorKind
{
    /// <summary>ps4debug already answers on the command port; a second copy can freeze the console.</summary>
    AlreadyRunning,

    /// <summary>The payload file is missing, empty or unreadable.</summary>
    FileProblem,

    /// <summary>The loader port refused the connection (BinLoader off, or already used this session).</summary>
    LoaderRefused,

    /// <summary>The loader port didn't answer at all (console off or not on this network).</summary>
    LoaderTimeout,

    /// <summary>The loader accepted the connection but sending failed.</summary>
    SendFailed,

    /// <summary>The payload was sent, but ps4debug never answered on the command port.</summary>
    NoAnswer,
}

public sealed class PayloadException(PayloadErrorKind kind, string message, Exception? inner = null)
    : Ps4ProtocolException(message, inner)
{
    public PayloadErrorKind Kind { get; } = kind;
}

/// <summary>What a successful injection did.</summary>
/// <param name="WaitedForStart">From the end of the send until ps4debug answered.</param>
public sealed record PayloadInjection(
    string Host, int LoaderPort, string PayloadPath, long BytesSent, int CommandPort, TimeSpan WaitedForStart);

/// <summary>
/// Sends the ps4debug payload to the console's payload loader (GoldHEN BinLoader, port 9090 by
/// default), then waits for ps4debug to answer on the command port. It never injects when the
/// command port already answers: the payload is running, and a second copy can freeze the console.
/// The loader takes one payload per session, so there is no retry of the send.
/// </summary>
public sealed class PayloadInjector(PS4MemSettings settings, DebugLog log)
{
    private const string Cat = "payload";

    /// <summary>Shown when the loader port refuses the connection (PROMPT §5).</summary>
    public const string RefusedHelp =
        "BinLoader is off or already used this session: toggle it in GoldHEN or restart the PS4.";

    /// <summary>
    /// Checks the command port is free, sends <paramref name="payloadPath"/> (default
    /// <see cref="PS4MemSettings.PayloadPath"/>) to <paramref name="loaderPort"/> (default
    /// <see cref="PS4MemSettings.PayloadPort"/>), and waits for ps4debug to answer. It does not
    /// connect: the caller does that once this returns.
    /// </summary>
    /// <param name="report">Short progress lines for the user (optional).</param>
    /// <exception cref="ArgumentException">Empty host or a port outside 1-65535.</exception>
    /// <exception cref="PayloadException">See <see cref="PayloadErrorKind"/>.</exception>
    public async Task<PayloadInjection> InjectAsync(
        string host, string? payloadPath = null, int? loaderPort = null,
        Action<string>? report = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException(ConsoleIpResolver.NotSetMessage);
        var port = loaderPort ?? settings.PayloadPort;
        if (port is < 1 or > 65535)
            throw new ArgumentException($"Loader port {port} must be 1-65535.");
        var path = ProjectPaths.ResolveFile(payloadPath ?? settings.PayloadPath);
        var commandPort = settings.CommandPort;

        log.Info(Cat, $"Inject {path} to {host}:{port}, then wait for ps4debug on {host}:{commandPort}");

        report?.Invoke($"Checking whether ps4debug already runs on {host}:{commandPort} ...");
        if (await PortAnswersAsync(host, commandPort, ct))
            throw Fail(PayloadErrorKind.AlreadyRunning,
                $"ps4debug already answers on {host}:{commandPort}, so the payload is running. " +
                "Connect instead: injecting a second copy can freeze the console.");

        var payload = ReadPayload(path);

        report?.Invoke($"Sending {Path.GetFileName(path)} ({payload.Length:N0} bytes) to {host}:{port} ...");
        await SendAsync(host, port, payload, ct);

        report?.Invoke($"Sent. Waiting up to {settings.PayloadStartTimeout.TotalSeconds:0} s for ps4debug on {host}:{commandPort} ...");
        var waited = await WaitForStartAsync(host, commandPort, ct);

        log.Info(Cat, $"ps4debug answered on {host}:{commandPort} after {waited.TotalSeconds:0.0} s");
        return new PayloadInjection(host, port, path, payload.Length, commandPort, waited);
    }

    /// <summary>True when a TCP connect to the port succeeds within <see cref="PS4MemSettings.PortProbeTimeout"/>.</summary>
    public async Task<bool> PortAnswersAsync(string host, int port, CancellationToken ct = default)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(settings.PortProbeTimeout);
        try
        {
            await socket.ConnectAsync(host, port, timeout.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // no answer within the probe timeout
        }
        catch (SocketException)
        {
            return false; // refused, unreachable, ...
        }
    }

    private byte[] ReadPayload(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            var reason = e is FileNotFoundException or DirectoryNotFoundException ? "was not found" : $"can't be read ({e.Message})";
            throw Fail(PayloadErrorKind.FileProblem, $"The payload file {path} {reason}. Choose the ps4debug .bin file.", e);
        }

        if (bytes.Length == 0)
            throw Fail(PayloadErrorKind.FileProblem, $"The payload file {path} is empty. Choose the ps4debug .bin file.");

        log.Info(Cat, $"Read {bytes.Length:N0} bytes from {path}");
        return bytes;
    }

    /// <summary>Connects to the loader, writes the whole file, then closes our side.</summary>
    private async Task SendAsync(string host, int port, byte[] payload, CancellationToken ct)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(settings.ConnectTimeout);
            try
            {
                await socket.ConnectAsync(host, port, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw Fail(PayloadErrorKind.LoaderTimeout,
                    $"No answer from the payload loader on {host}:{port} after {settings.ConnectTimeout.TotalSeconds:0} s. " +
                    "Is the PS4 on and on the same network as this Mac?");
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.ConnectionRefused)
            {
                throw Fail(PayloadErrorKind.LoaderRefused, $"Connection refused on {host}:{port}. {RefusedHelp}", e);
            }
            catch (SocketException e)
            {
                throw Fail(PayloadErrorKind.LoaderTimeout,
                    $"Can't reach the payload loader on {host}:{port}: {e.Message}. Is the PS4 on and on the same network?", e);
            }
        }

        log.Info(Cat, $"Connected to the loader on {host}:{port}, sending {payload.Length:N0} bytes");
        try
        {
            var sent = 0;
            while (sent < payload.Length)
                sent += await socket.SendAsync(payload.AsMemory(sent), SocketFlags.None, ct);
            socket.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException e)
        {
            throw Fail(PayloadErrorKind.SendFailed,
                $"The payload loader on {host}:{port} closed the connection while the payload was being sent: {e.Message}. " +
                "Restart the PS4 and try again.", e);
        }
        log.Info(Cat, $"Sent {payload.Length:N0} bytes to {host}:{port}");
    }

    /// <summary>Tries the command port right away, then every poll interval, until the start timeout.</summary>
    private async Task<TimeSpan> WaitForStartAsync(string host, int commandPort, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            if (await PortAnswersAsync(host, commandPort, ct))
                return clock.Elapsed;

            log.Info(Cat, $"Attempt {attempt}: {host}:{commandPort} doesn't answer yet ({clock.Elapsed.TotalSeconds:0.0} s)");
            if (clock.Elapsed + settings.PayloadPollInterval > settings.PayloadStartTimeout)
                break;
            await Task.Delay(settings.PayloadPollInterval, ct);
        }

        throw Fail(PayloadErrorKind.NoAnswer,
            $"The payload was sent, but ps4debug didn't answer on {host}:{commandPort} within " +
            $"{settings.PayloadStartTimeout.TotalSeconds:0} s. Try Connect in a moment; if that fails too, check the PS4 " +
            "for an error and restart it (BinLoader takes one payload per session).");
    }

    private PayloadException Fail(PayloadErrorKind kind, string message, Exception? inner = null)
    {
        log.Error(Cat, $"{kind}: {message}", inner);
        return new PayloadException(kind, message, inner);
    }
}
