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
    public void Connect()
    {
        if (string.IsNullOrWhiteSpace(_settings.ConsoleIp))
            throw new Ps4ProtocolException(ConsoleIpResolver.NotSetMessage);
        Connect(_settings.ConsoleIp, _settings.CommandPort);
    }

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

    /// <summary>
    /// CMD_PROC_INFO (protocol.md §6): body u32 pid; reply status + 188-byte struct
    /// {u32 pid; char[40] name; char[64] path; char[16] titleid; char[64] contentid}.
    /// An error keeps the connection open. Port of LD/PS4DBG.Proc.cs:458-466.
    /// </summary>
    public ProcessInfo GetProcessInfo(int pid) => Run(Ps4Command.ProcInfo, () =>
    {
        SendCommand(Ps4Command.ProcInfo, PidBody(pid));
        ExpectSuccess(Ps4Command.ProcInfo, closesOnError: false);
        var d = Receive(Ps4Protocol.ProcInfoSize, "process info").AsSpan();
        var info = new ProcessInfo(
            Pid: BinaryPrimitives.ReadInt32LittleEndian(d),
            Name: Ascii(d.Slice(4, 40)),
            Path: Ascii(d.Slice(44, 64)),
            TitleId: Ascii(d.Slice(108, 16)),
            ContentId: Ascii(d.Slice(124, 64)));
        return (info, $"pid {info.Pid}, name '{info.Name}', title ID '{info.TitleId}', path '{info.Path}'");
    }, $"pid {pid}");

    /// <summary>
    /// CMD_PROC_MAPS (protocol.md §7): body u32 pid; reply status, u32 count, count × 58-byte
    /// entries {char[32] name; u64 start; u64 end; u64 offset; u16 prot}. Maps with
    /// <c>end &lt;= start</c> are skipped (and logged), not treated as errors.
    /// On error the server closes the connection. Port of LD/PS4DBG.Proc.cs:98-127.
    /// </summary>
    public IReadOnlyList<MemoryMap> GetProcessMaps(int pid) => Run(Ps4Command.ProcMaps, () =>
    {
        SendCommand(Ps4Command.ProcMaps, PidBody(pid));
        ExpectSuccess(Ps4Command.ProcMaps, closesOnError: true);
        var count = ReceiveCount("map count", max: 1_000_000);
        var data = Receive(count * Ps4Protocol.ProcMapEntrySize, "process maps");

        var maps = new List<MemoryMap>(count);
        var skipped = 0;
        for (var i = 0; i < count; i++)
        {
            var e = data.AsSpan(i * Ps4Protocol.ProcMapEntrySize, Ps4Protocol.ProcMapEntrySize);
            var map = new MemoryMap(
                Name: Ascii(e[..32]),
                Start: BinaryPrimitives.ReadUInt64LittleEndian(e[32..]),
                End: BinaryPrimitives.ReadUInt64LittleEndian(e[40..]),
                Offset: BinaryPrimitives.ReadUInt64LittleEndian(e[48..]),
                Prot: BinaryPrimitives.ReadUInt16LittleEndian(e[56..]));

            if (map.End <= map.Start)
            {
                skipped++;
                _log.Warn(Cat, $"Skipping map '{map.Name}' 0x{map.Start:X}-0x{map.End:X} {map.ProtText}: end <= start");
                continue;
            }
            maps.Add(map);
        }
        return (maps, $"{count} maps received, {maps.Count} kept, {skipped} skipped (end <= start)");
    }, $"pid {pid}");

    /// <summary>
    /// Reads process memory. Requests larger than <see cref="PS4MemSettings.ReadChunkBytes"/> are
    /// split into several CMD_PROC_READ calls; the lock is released between chunks so other
    /// commands (lock re-writes) can run during a long scan. Unreadable memory comes back as
    /// zeros: the server can't report it (protocol.md §8).
    /// </summary>
    public byte[] ReadMemory(int pid, ulong address, int length, CancellationToken cancel = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var buffer = new byte[length];
        ReadMemory(pid, address, buffer, cancel);
        return buffer;
    }

    /// <inheritdoc cref="ReadMemory(int, ulong, int, CancellationToken)"/>
    public void ReadMemory(int pid, ulong address, Memory<byte> destination, CancellationToken cancel = default)
    {
        var chunkSize = Math.Max(1, _settings.ReadChunkBytes);
        for (var done = 0; done < destination.Length; done += chunkSize)
        {
            cancel.ThrowIfCancellationRequested();
            var chunk = destination.Slice(done, Math.Min(chunkSize, destination.Length - done));
            ReadChunk(pid, address + (ulong)done, chunk);
        }
    }

    /// <summary>
    /// One CMD_PROC_READ (protocol.md §8): body {u32 pid; u64 address; u32 length};
    /// reply status, then exactly length raw bytes. On error the server closes the connection.
    /// Port of LD/PS4DBG.Proc.cs:70-76.
    /// </summary>
    private void ReadChunk(int pid, ulong address, Memory<byte> chunk) => Run(Ps4Command.ProcRead, () =>
    {
        SendCommand(Ps4Command.ProcRead, ReadWriteBody(pid, address, chunk.Length));
        ExpectSuccess(Ps4Command.ProcRead, closesOnError: true);
        ReceiveInto(chunk.Span, $"memory at 0x{address:X}");
        return (true, $"{chunk.Length} bytes");
    }, $"pid {pid}, 0x{address:X}, {chunk.Length} bytes");

    /// <summary>
    /// CMD_PROC_WRITE (protocol.md §9): send body {u32 pid; u64 address; u32 length}, read the
    /// ack status, send the bytes, read the final status. A failed ack closes the connection;
    /// a failed final status (bad request, write failed, read-back mismatch) keeps it open.
    /// Only call this when the user asked for a write (CLAUDE.md §7).
    /// Port of LD/PS4DBG.Proc.cs:84-91.
    /// </summary>
    public void WriteMemory(int pid, ulong address, ReadOnlySpan<byte> data)
    {
        var bytes = data.ToArray(); // the lambda can't capture a span
        Run(Ps4Command.ProcWrite, () =>
        {
            SendCommand(Ps4Command.ProcWrite, ReadWriteBody(pid, address, bytes.Length));
            ExpectSuccess(Ps4Command.ProcWrite, closesOnError: true);
            SendRaw(bytes);
            ExpectSuccess(Ps4Command.ProcWrite, closesOnError: false);
            return (true, $"{bytes.Length} bytes written");
        }, $"pid {pid}, 0x{address:X}, {bytes.Length} bytes: {Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 16)))}" +
           (bytes.Length > 16 ? "..." : ""));
    }

    private static byte[] PidBody(int pid)
    {
        var body = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(body, pid);
        return body;
    }

    /// <summary>{u32 pid; u64 address; u32 length}, packed, LE (protocol.md §8).</summary>
    private static byte[] ReadWriteBody(int pid, ulong address, int length)
    {
        var body = new byte[Ps4Protocol.ProcReadWriteBodySize];
        BinaryPrimitives.WriteInt32LittleEndian(body, pid);
        BinaryPrimitives.WriteUInt64LittleEndian(body.AsSpan(4), address);
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(12), length);
        return body;
    }

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
