using System.Net;
using System.Net.Sockets;

namespace PS4Mem.Core.Tests.Fakes;

/// <summary>
/// A fake payload loader (BinLoader) on 127.0.0.1: it accepts one connection, reads until the
/// client closes, and then runs <c>onReceived</c> with the bytes (e.g. to start a fake ps4debug).
/// </summary>
public sealed class FakeLoader : IDisposable
{
    private readonly TcpListener _listener;
    private volatile bool _contacted;

    public int Port { get; }

    /// <summary>The bytes the client sent; completes once it has closed its side.</summary>
    public Task<byte[]> Received { get; }

    public FakeLoader(Action<byte[]>? onReceived = null)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Received = Task.Run(async () =>
        {
            using var client = await _listener.AcceptTcpClientAsync();
            _contacted = true;
            using var stream = client.GetStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            var bytes = buffer.ToArray();
            onReceived?.Invoke(bytes);
            return bytes;
        });
    }

    /// <summary>True once a client has connected.</summary>
    public bool Contacted => _contacted;

    public void Dispose() => _listener.Stop();

    /// <summary>A loopback port with nothing listening on it (connects are refused).</summary>
    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
