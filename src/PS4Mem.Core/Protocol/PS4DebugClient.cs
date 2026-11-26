using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using PS4Mem.Core.Logging;

namespace PS4Mem.Core.Protocol;

/// <summary>
/// Client for the ps4debug(-NG) command port (744). A port of libdebug's PS4DBG
/// (ref/libdebug/PS4DBG/PS4DBG.cs, PS4DBG.Proc.cs) with the client bugs listed in
/// docs/protocol.md §2 fixed: every receive loops until the full count arrives, a closed
/// connection throws instead of spinning, and fixed-width strings stop at their width.
///
/// One lock guards the socket, so each command's request and reply never interleave with
/// another thread's. There is a connect timeout and no read timeout (scans take minutes).
/// </summary>
public sealed class PS4DebugClient : IDisposable
{
    private const string Cat = "proto";

    private readonly DebugLog _log;
    private readonly PS4MemSettings _settings;
    private readonly Lock _gate = new();
    private Socket? _socket;

    public PS4DebugClient(PS4MemSettings? settings = null, DebugLog? log = null)
    {
        _settings = settings ?? PS4MemSettings.Default;
        _log = log ?? DebugLog.Disabled;
    }

    public bool IsConnected => _socket is not null;

    /// <summary>"ip:port" of the current connection, for messages.</summary>
    public string? Endpoint { get; private set; }

    /// <summary>Connects to the settings' console IP and command port.</summary>
    public void Connect() => Connect(_settings.ConsoleIp, _settings.CommandPort);

    /// <summary>
    /// Opens the TCP connection. ps4debug sends no greeting (protocol.md §2), so this only
    /// connects. Socket options follow libdebug (LD/PS4DBG.cs:161-176) minus its 10 s read timeout.
    /// </summary>
    public void Connect(string host, int port)
    {
        lock (_gate)
        {
            if (_socket is not null)
                throw new InvalidOperationException($"Already connected to {Endpoint}.");

            _log.Info(Cat, $"Connecting to {host}:{port} (connect timeout {_settings.ConnectTimeout.TotalSeconds:0.#} s)");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
                ReceiveTimeout = 0, // no read timeout, on purpose
                SendTimeout = 0,
            };

            try
            {
                using var cts = new CancellationTokenSource(_settings.ConnectTimeout);
                socket.ConnectAsync(host, port, cts.Token).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                socket.Dispose();
                var msg = $"Timed out after {_settings.ConnectTimeout.TotalSeconds:0.#} s connecting to {host}:{port}. " +
                          "Is the PS4 on, on the same network, and is the ps4debug payload running?";
                _log.Error(Cat, msg);
                throw new Ps4ProtocolException(msg);
            }
            catch (SocketException e)
            {
                socket.Dispose();
                var msg = $"Could not connect to {host}:{port}: {e.Message}. Is the ps4debug payload running?";
                _log.Error(Cat, msg, e);
                throw new Ps4ProtocolException(msg, e);
            }

            _socket = socket;
            Endpoint = $"{host}:{port}";
            _log.Info(Cat, $"Connected to {Endpoint}");
        }
    }

    /// <summary>
    /// Clean disconnect: CMD_CONSOLE_END (header only, no reply), then shut down and close
    /// (protocol.md §2, LD/PS4DBG.cs:181-193). The server then frees this client's debugger
    /// and scan state. Safe to call when not connected.
    /// </summary>
    public void Disconnect()
    {
        lock (_gate)
        {
            if (_socket is null) return;
            _log.Info(Cat, $"Disconnecting from {Endpoint}");
            try
            {
                SendCommand(Ps4Command.ConsoleEnd, ReadOnlySpan<byte>.Empty);
                _socket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception e) when (e is SocketException or IOException or Ps4ProtocolException or ObjectDisposedException)
            {
                _log.Warn(Cat, $"Disconnect: connection was already gone ({e.Message})");
            }
            finally
            {
                CloseSocket();
            }
        }
    }

    public void Dispose() => Disconnect();

    // ---- Commands --------------------------------------------------------------------------

    /// <summary>
    /// CMD_VERSION (protocol.md §4): header only; reply is u32 len + len ASCII bytes, no status word.
    /// </summary>
    public string GetVersion() => Run(Ps4Command.Version, () =>
    {
        SendCommand(Ps4Command.Version, ReadOnlySpan<byte>.Empty);
        var length = ReceiveCount("version length", max: 4096);
        var version = Ascii(Receive(length, "version"));
        return (version, $"\"{version}\"");
    });

    /// <summary>
    /// CMD_PROC_LIST (protocol.md §5): header only; reply is status, u32 count, count × 36-byte
    /// entries {char[32] name; i32 pid}. On error the server closes the connection.
    /// Port of LD/PS4DBG.Proc.cs:37-61.
    /// </summary>
    public IReadOnlyList<ProcessEntry> GetProcessList() => Run(Ps4Command.ProcList, () =>
    {
        SendCommand(Ps4Command.ProcList, ReadOnlySpan<byte>.Empty);
        ExpectSuccess(Ps4Command.ProcList, closesOnError: true);
        var count = ReceiveCount("process count", max: 100_000);
        var data = Receive(count * Ps4Protocol.ProcListEntrySize, "process list");

        var list = new List<ProcessEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = data.AsSpan(i * Ps4Protocol.ProcListEntrySize, Ps4Protocol.ProcListEntrySize);
            list.Add(new ProcessEntry(Ascii(entry[..32]), BinaryPrimitives.ReadInt32LittleEndian(entry[32..])));
        }
        return (list, $"{list.Count} processes: " + string.Join(", ", list.Select(p => $"{p.Name}({p.Pid})")));
    });

    // ---- Plumbing --------------------------------------------------------------------------

    /// <summary>Runs one command under the socket lock, logging the command and its result or error.</summary>
    private T Run<T>(Ps4Command command, Func<(T Result, string Summary)> body, string? args = null)
    {
        lock (_gate)
        {
            if (_socket is null)
                throw new InvalidOperationException("Not connected to the PS4.");

            _log.Info(Cat, $"{command}{(args is null ? "" : " " + args)}");
            try
            {
                var (result, summary) = body();
                _log.Info(Cat, $"{command} ok: {summary}");
                return result;
            }
            catch (Exception e)
            {
                _log.Error(Cat, $"{command} failed", e);
                if (e is Ps4StatusException { ConnectionClosed: true } or IOException or SocketException
                    || e is Ps4ProtocolException and not Ps4StatusException)
                {
                    // The stream is closed or out of sync; it can't be used again.
                    CloseSocket();
                }
                if (e is SocketException se)
                    throw new Ps4ProtocolException($"{command} failed: {se.Message}", se);
                throw;
            }
        }
    }

    /// <summary>Sends the 12-byte header and the body as one message (protocol.md §1).</summary>
    private void SendCommand(Ps4Command command, ReadOnlySpan<byte> body)
    {
        var packet = new byte[Ps4Protocol.HeaderSize + body.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0), Ps4Protocol.PacketMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), (uint)command);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), (uint)body.Length);
        body.CopyTo(packet.AsSpan(Ps4Protocol.HeaderSize));
        SendRaw(packet);
    }

    private void SendRaw(ReadOnlySpan<byte> data)
    {
        _log.Wire(Cat, sent: true, data);
        var socket = _socket!;
        while (!data.IsEmpty)
        {
            var sent = socket.Send(data);
            data = data[sent..];
        }
    }

    /// <summary>Receives exactly <paramref name="length"/> bytes; a 0-byte receive means the console closed.</summary>
    private byte[] Receive(int length, string what)
    {
        var buffer = new byte[length];
        ReceiveInto(buffer, what);
        return buffer;
    }

    private void ReceiveInto(Span<byte> buffer, string what)
    {
        var socket = _socket!;
        var total = 0;
        while (total < buffer.Length)
        {
            var got = socket.Receive(buffer[total..]);
            if (got == 0)
                throw new Ps4ProtocolException(
                    $"The PS4 closed the connection while sending {what} ({total} of {buffer.Length} bytes received).");
            total += got;
        }
        _log.Wire(Cat, sent: false, buffer);
    }

    private uint ReceiveU32(string what)
    {
        Span<byte> b = stackalloc byte[4];
        ReceiveInto(b, what);
        return BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    /// <summary>Reads a u32 count/length and rejects values that can't be real (stream out of sync).</summary>
    private int ReceiveCount(string what, int max)
    {
        var value = ReceiveU32(what);
        if (value > (uint)max)
            throw new Ps4ProtocolException($"Unexpected {what} {value} from the PS4 (max {max}); the reply is out of sync.");
        return (int)value;
    }

    private void ExpectSuccess(Ps4Command command, bool closesOnError)
    {
        var status = (Ps4Status)ReceiveU32($"{command} status");
        if (status != Ps4Status.Success)
            throw new Ps4StatusException(command, status, closesOnError);
    }

    /// <summary>
    /// ASCII up to the first NUL, never past the field (fixes LD/PS4DBG.cs:83-90, protocol.md §2).
    /// </summary>
    private static string Ascii(ReadOnlySpan<byte> field)
    {
        var nul = field.IndexOf((byte)0);
        return Encoding.ASCII.GetString(nul < 0 ? field : field[..nul]);
    }

    private void CloseSocket()
    {
        if (_socket is null) return;
        try { _socket.Dispose(); } catch (ObjectDisposedException) { }
        _socket = null;
        _log.Info(Cat, $"Connection to {Endpoint} closed");
    }
}
