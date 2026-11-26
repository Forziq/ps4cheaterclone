using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PS4Mem.Core.Protocol;

namespace PS4Mem.Core.Tests.Fakes;

/// <summary>
/// A fake ps4debug server on 127.0.0.1. It accepts one client per script, in order, and runs each
/// script on its connection: scripts read requests and replay the byte layouts from
/// docs/protocol.md. Await <see cref="Completion"/> to surface the scripts' assertion failures.
/// </summary>
public sealed class FakePs4Server : IDisposable
{
    private readonly TcpListener _listener;

    public int Port { get; }
    public Task Completion { get; }

    public FakePs4Server(params Action<FakeConsole>[] scripts) : this(0, scripts) { }

    /// <summary>Listens on <paramref name="port"/> (0 = any free port).</summary>
    public FakePs4Server(int port, params Action<FakeConsole>[] scripts)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Completion = Task.Run(() =>
        {
            foreach (var script in scripts)
            {
                using var client = _listener.AcceptTcpClient();
                client.NoDelay = true;
                using var stream = client.GetStream();
                script(new FakeConsole(client, stream));
            }
        });
    }

    /// <summary>Waits for the script to finish (fails the test if it threw or hangs).</summary>
    public void WaitForScript() =>
        Assert.True(Completion.Wait(TimeSpan.FromSeconds(10)), "Fake server script did not finish.");

    public void Dispose() => _listener.Stop();
}

public sealed record FakeRequest(uint Magic, Ps4Command Command, byte[] Body)
{
    public uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Body.AsSpan(offset));
    public int I32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(Body.AsSpan(offset));
    public ulong U64(int offset) => BinaryPrimitives.ReadUInt64LittleEndian(Body.AsSpan(offset));
}

/// <summary>The server side of one connection.</summary>
public sealed class FakeConsole(TcpClient client, NetworkStream stream)
{
    /// <summary>Send replies one byte at a time, to prove the client loops its receives.</summary>
    public bool Trickle { get; set; }

    /// <summary>Reads a 12-byte header and its datalen body (protocol.md §1).</summary>
    public FakeRequest ReadRequest()
    {
        var header = ReadBytes(Ps4Protocol.HeaderSize);
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var cmd = (Ps4Command)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
        var len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        return new FakeRequest(magic, cmd, ReadBytes(len));
    }

    public FakeRequest Expect(Ps4Command command, int bodyLength)
    {
        var req = ReadRequest();
        Assert.Equal(Ps4Protocol.PacketMagic, req.Magic);
        Assert.Equal(command, req.Command);
        Assert.Equal(bodyLength, req.Body.Length);
        return req;
    }

    public byte[] ReadBytes(int count)
    {
        var buf = new byte[count];
        stream.ReadExactly(buf);
        return buf;
    }

    /// <summary>True once the client has closed its side (read returns 0).</summary>
    public bool ClientClosed()
    {
        var b = new byte[1];
        return stream.Read(b) == 0;
    }

    public void Send(params byte[][] parts)
    {
        foreach (var part in parts)
        {
            if (!Trickle) { stream.Write(part); continue; }
            foreach (var b in part) { stream.WriteByte(b); stream.Flush(); }
        }
    }

    public void SendStatus(Ps4Status status) => Send(U32((uint)status));

    public void Close() => client.Close();

    // ---- Byte builders ----

    public static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); return b; }
    public static byte[] I32(int v) { var b = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(b, v); return b; }
    public static byte[] U64(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, v); return b; }
    public static byte[] U16(ushort v) { var b = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(b, v); return b; }

    /// <summary>Fixed-width, NUL-padded ASCII field (truncated to the width, like the server's char[N]).</summary>
    public static byte[] Fixed(string s, int width)
    {
        var b = new byte[width];
        var src = Encoding.ASCII.GetBytes(s);
        Array.Copy(src, b, Math.Min(src.Length, width));
        return b;
    }

    /// <summary>36-byte CMD_PROC_LIST entry (protocol.md §5).</summary>
    public static byte[] ProcEntry(string name, int pid) => [.. Fixed(name, 32), .. I32(pid)];

    /// <summary>58-byte CMD_PROC_MAPS entry (protocol.md §7).</summary>
    public static byte[] MapEntry(string name, ulong start, ulong end, ulong offset, ushort prot) =>
        [.. Fixed(name, 32), .. U64(start), .. U64(end), .. U64(offset), .. U16(prot)];

    /// <summary>188-byte CMD_PROC_INFO struct (protocol.md §6).</summary>
    public static byte[] ProcInfo(int pid, string name, string path, string titleId, string contentId) =>
        [.. I32(pid), .. Fixed(name, 40), .. Fixed(path, 64), .. Fixed(titleId, 16), .. Fixed(contentId, 64)];
}
