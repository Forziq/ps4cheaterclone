using System.Net;
using System.Net.Sockets;
using System.Text;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Tests.Fakes;
using static PS4Mem.Core.Tests.Fakes.FakeConsole;

namespace PS4Mem.Core.Tests;

public class PS4DebugClientTests
{
    private static PS4DebugClient ConnectTo(FakePs4Server server, DebugLog? log = null, PS4MemSettings? settings = null)
    {
        var client = new PS4DebugClient(settings, log);
        client.Connect("127.0.0.1", server.Port);
        return client;
    }

    // ---- m1: connect, version, process list, disconnect ----

    [Fact]
    public void Version_sends_exact_header_and_reads_length_prefixed_string()
    {
        byte[]? raw = null;
        using var server = new FakePs4Server(c =>
        {
            raw = c.ReadBytes(12);
            c.Send(U32(3), "1.3"u8.ToArray());
        });
        using var client = ConnectTo(server);

        Assert.Equal("1.3", client.GetVersion());
        server.WaitForScript();
        // magic 0xFFAABBCC, cmd 0xBD000001, datalen 0, all little-endian (protocol.md §1, §4).
        Assert.Equal(new byte[] { 0xCC, 0xBB, 0xAA, 0xFF, 0x01, 0x00, 0x00, 0xBD, 0, 0, 0, 0 }, raw);
    }

    [Fact]
    public void Replies_split_into_single_bytes_are_still_read_whole()
    {
        using var server = new FakePs4Server(c =>
        {
            c.Trickle = true;
            c.Expect(Ps4Command.Version, 0);
            c.Send(U32(3), "1.3"u8.ToArray());
            c.Expect(Ps4Command.ProcList, 0);
            c.SendStatus(Ps4Status.Success);
            c.Send(U32(1), ProcEntry("eboot.bin", 77));
        });
        using var client = ConnectTo(server);

        Assert.Equal("1.3", client.GetVersion());
        var list = client.GetProcessList();
        Assert.Equal(new ProcessEntry("eboot.bin", 77), Assert.Single(list));
        server.WaitForScript();
    }

    [Fact]
    public void Process_list_parses_names_and_pids()
    {
        var fullWidth = new string('x', 32); // no NUL: the name must not run into the pid bytes
        using var server = new FakePs4Server(c =>
        {
            c.Expect(Ps4Command.ProcList, 0);
            c.SendStatus(Ps4Status.Success);
            c.Send(U32(3),
                ProcEntry("SceShellCore", 45),
                ProcEntry("ac3_engine_orbis_fn.elf", 101),
                ProcEntry(fullWidth, 0x41424344));
        });
        using var client = ConnectTo(server);

        var list = client.GetProcessList();
        server.WaitForScript();
        Assert.Equal(
            [new("SceShellCore", 45), new("ac3_engine_orbis_fn.elf", 101), new(fullWidth, 0x41424344)],
            list);
    }

    [Fact]
    public void Process_list_error_status_throws_and_drops_the_connection()
    {
        using var server = new FakePs4Server(c =>
        {
            c.Expect(Ps4Command.ProcList, 0);
            c.SendStatus(Ps4Status.DataNull);
            c.Close(); // the server closes after a failed handler (protocol.md §2, §5)
        });
        using var client = ConnectTo(server);

        var ex = Assert.Throws<Ps4StatusException>(() => client.GetProcessList());
        server.WaitForScript();
        Assert.Equal(Ps4Status.DataNull, ex.Status);
        Assert.True(ex.ConnectionClosed);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Console_closing_mid_reply_throws_instead_of_hanging()
    {
        using var server = new FakePs4Server(c =>
        {
            c.Expect(Ps4Command.ProcList, 0);
            c.SendStatus(Ps4Status.Success);
            c.Send(U32(2), ProcEntry("eboot.bin", 1)); // promises 2 entries, sends 1
            c.Close();
        });
        using var client = ConnectTo(server);

        var ex = Assert.Throws<Ps4ProtocolException>(() => client.GetProcessList());
        server.WaitForScript();
        Assert.Contains("closed the connection", ex.Message);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Absurd_count_is_rejected_as_out_of_sync()
    {
        using var server = new FakePs4Server(c =>
        {
            c.Expect(Ps4Command.ProcList, 0);
            c.SendStatus(Ps4Status.Success);
            c.Send(U32(0xFFFFFFF0));
        });
        using var client = ConnectTo(server);

        Assert.Throws<Ps4ProtocolException>(() => client.GetProcessList());
        server.WaitForScript();
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Disconnect_sends_console_end_then_closes()
    {
        var closed = false;
        using var server = new FakePs4Server(c =>
        {
            c.Expect(Ps4Command.ConsoleEnd, 0); // header only, no reply (protocol.md §2)
            closed = c.ClientClosed();
        });
        var client = ConnectTo(server);

        client.Disconnect();
        server.WaitForScript();
        Assert.True(closed);
        Assert.False(client.IsConnected);
        client.Disconnect(); // second call is a no-op
    }

    [Fact]
    public void Connect_refused_gives_a_clear_error()
    {
        // Grab a free port, then close it so nothing listens there.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var client = new PS4DebugClient();
        var ex = Assert.Throws<Ps4ProtocolException>(() => client.Connect("127.0.0.1", port));
        Assert.Contains($"127.0.0.1:{port}", ex.Message);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Commands_before_connect_throw()
    {
        var client = new PS4DebugClient();
        Assert.Throws<InvalidOperationException>(() => client.GetVersion());
    }

    [Fact]
    public void Debug_log_records_wire_traffic_and_command_results()
    {
        var sw = new StringWriter();
        var log = new DebugLog(sw);
        using var server = new FakePs4Server(c =>
        {
            c.Expect(Ps4Command.Version, 0);
            c.Send(U32(3), "1.3"u8.ToArray());
            c.Expect(Ps4Command.ProcList, 0);
            c.SendStatus(Ps4Status.DataNull);
            c.Close();
        });
        using var client = ConnectTo(server, log);

        client.GetVersion();
        Assert.Throws<Ps4StatusException>(() => client.GetProcessList());
        server.WaitForScript();

        var text = sw.ToString();
        Assert.Contains("[WIRE ] [proto] >> 12 bytes", text);
        Assert.Contains("CC BB AA FF 01 00 00 BD", text);
        Assert.Contains("[WIRE ] [proto] << 3 bytes", text);
        Assert.Contains("[INFO ] [proto] Version ok: \"1.3\"", text);
        Assert.Contains("[ERROR] [proto] ProcList failed", text);
        Assert.Contains("DataNull (0xF0000003)", text);
    }
}
