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

    // ---- m2: process info, maps, read, write ----

    [Fact]
    public void Process_info_parses_title_id_and_fixed_width_fields()
    {
        FakeRequest? req = null;
        using var server = new FakePs4Server(c =>
        {
            req = c.Expect(Ps4Command.ProcInfo, 4);
            c.SendStatus(Ps4Status.Success);
            c.Send(ProcInfo(101, "ac3_engine_orbis_fn.elf", "/app0/ac3_engine_orbis_fn.elf",
                "CUSA11711", "EP0001-CUSA11711_00-AC3REMASTERED000"));
        });
        using var client = ConnectTo(server);

        var info = client.GetProcessInfo(101);
        server.WaitForScript();
        Assert.Equal(101, req!.I32(0));
        Assert.Equal(new ProcessInfo(101, "ac3_engine_orbis_fn.elf", "/app0/ac3_engine_orbis_fn.elf",
            "CUSA11711", "EP0001-CUSA11711_00-AC3REMASTERED000"), info);
    }

    [Fact]
    public void Process_info_error_keeps_the_connection_open()
    {
        using var server = new FakePs4Server(c =>
        {
            c.Expect(Ps4Command.ProcInfo, 4);
            c.SendStatus(Ps4Status.DataNull);
            c.Expect(Ps4Command.Version, 0);
            c.Send(U32(3), "1.3"u8.ToArray());
        });
        using var client = ConnectTo(server);

        var ex = Assert.Throws<Ps4StatusException>(() => client.GetProcessInfo(5));
        Assert.False(ex.ConnectionClosed);
        Assert.True(client.IsConnected);
        Assert.Equal("1.3", client.GetVersion());
        server.WaitForScript();
    }

    [Fact]
    public void Maps_are_parsed_and_broken_ones_skipped()
    {
        FakeRequest? req = null;
        using var server = new FakePs4Server(c =>
        {
            req = c.Expect(Ps4Command.ProcMaps, 4);
            c.SendStatus(Ps4Status.Success);
            c.Send(U32(4),
                MapEntry("executable", 0x400000, 0x1400000, 0, 0x5),
                MapEntry("anon:000a1b2c", 0x2000000000, 0x2010000000, 0x10, 0x3),
                MapEntry("broken_end_zero", 0x3000000000, 0, 0, 0x3),
                MapEntry("broken_equal", 0x4000, 0x4000, 0, 0x1));
        });
        var sw = new StringWriter();
        using var client = ConnectTo(server, new DebugLog(sw));

        var maps = client.GetProcessMaps(101);
        server.WaitForScript();
        Assert.Equal(101, req!.I32(0));
        Assert.Equal(
            [new("executable", 0x400000, 0x1400000, 0, 0x5),
             new("anon:000a1b2c", 0x2000000000, 0x2010000000, 0x10, 0x3)],
            maps);
        Assert.Equal("r-x", maps[0].ProtText);
        Assert.True(maps[1].IsWritable);
        Assert.Equal(0x10000000UL, maps[1].Size);
        Assert.Contains("[maps] skip 'broken_end_zero'", sw.ToString());
        Assert.Contains("4 maps received, 2 kept, 2 skipped", sw.ToString());
    }

    [Fact]
    public void Read_sends_pid_address_length_and_returns_raw_bytes()
    {
        FakeRequest? req = null;
        using var server = new FakePs4Server(c =>
        {
            req = c.Expect(Ps4Command.ProcRead, 16);
            c.SendStatus(Ps4Status.Success);
            c.Send([0xE8, 0x03, 0x00, 0x00, 0xAA]); // no length prefix (protocol.md §8)
        });
        using var client = ConnectTo(server);

        var data = client.ReadMemory(101, 0x2000001234, 5);
        server.WaitForScript();
        Assert.Equal(101, req!.I32(0));
        Assert.Equal(0x2000001234UL, req.U64(4));
        Assert.Equal(5U, req.U32(12));
        Assert.Equal(new byte[] { 0xE8, 0x03, 0x00, 0x00, 0xAA }, data);
    }

    [Fact]
    public void Large_reads_are_split_into_chunks()
    {
        var requests = new List<FakeRequest>();
        using var server = new FakePs4Server(c =>
        {
            for (var i = 0; i < 3; i++)
            {
                var r = c.Expect(Ps4Command.ProcRead, 16);
                requests.Add(r);
                c.SendStatus(Ps4Status.Success);
                c.Send(Enumerable.Range(0, (int)r.U32(12)).Select(b => (byte)(i + 1)).ToArray());
            }
        });
        using var client = ConnectTo(server, settings: PS4MemSettings.Default with { ReadChunkBytes = 4 });

        var data = client.ReadMemory(7, 0x1000, 10);
        server.WaitForScript();
        Assert.Equal([0x1000UL, 0x1004UL, 0x1008UL], requests.Select(r => r.U64(4)));
        Assert.Equal([4U, 4U, 2U], requests.Select(r => r.U32(12)));
        Assert.Equal(new byte[] { 1, 1, 1, 1, 2, 2, 2, 2, 3, 3 }, data);
    }

    [Fact]
    public void Read_can_be_cancelled_between_chunks()
    {
        using var server = new FakePs4Server(c => { });
        using var client = ConnectTo(server, settings: PS4MemSettings.Default with { ReadChunkBytes = 4 });

        Assert.Throws<OperationCanceledException>(
            () => client.ReadMemory(7, 0x1000, 10, new CancellationToken(canceled: true)));
        server.WaitForScript();
    }

    [Fact]
    public void Write_follows_ack_then_data_then_final_status()
    {
        FakeRequest? req = null;
        byte[]? payload = null;
        using var server = new FakePs4Server(c =>
        {
            req = c.Expect(Ps4Command.ProcWrite, 16);
            c.SendStatus(Ps4Status.Success); // ack
            payload = c.ReadBytes((int)req.U32(12));
            c.SendStatus(Ps4Status.Success); // final
        });
        using var client = ConnectTo(server);

        client.WriteMemory(101, 0x2000001234, [0x0F, 0x27, 0x00, 0x00]); // 9999
        server.WaitForScript();
        Assert.Equal(101, req!.I32(0));
        Assert.Equal(0x2000001234UL, req.U64(4));
        Assert.Equal(4U, req.U32(12));
        Assert.Equal(new byte[] { 0x0F, 0x27, 0x00, 0x00 }, payload);
    }

    [Fact]
    public void Write_final_error_throws_but_keeps_the_connection()
    {
        using var server = new FakePs4Server(c =>
        {
            var req = c.Expect(Ps4Command.ProcWrite, 16);
            c.SendStatus(Ps4Status.Success);
            c.ReadBytes((int)req.U32(12));
            c.SendStatus(Ps4Status.Error); // write failed or read-back mismatch (protocol.md §9)
            c.Expect(Ps4Command.Version, 0);
            c.Send(U32(3), "1.3"u8.ToArray());
        });
        using var client = ConnectTo(server);

        var ex = Assert.Throws<Ps4StatusException>(() => client.WriteMemory(1, 0x10, [1, 2]));
        Assert.Equal(Ps4Status.Error, ex.Status);
        Assert.False(ex.ConnectionClosed);
        Assert.Equal("1.3", client.GetVersion());
        server.WaitForScript();
    }

    [Fact]
    public void Write_ack_error_drops_the_connection_without_sending_data()
    {
        using var server = new FakePs4Server(c =>
        {
            c.Expect(Ps4Command.ProcWrite, 16);
            c.SendStatus(Ps4Status.DataNull);
            c.Close();
        });
        using var client = ConnectTo(server);

        var ex = Assert.Throws<Ps4StatusException>(() => client.WriteMemory(1, 0x10, [1, 2]));
        server.WaitForScript();
        Assert.True(ex.ConnectionClosed);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task Concurrent_commands_do_not_interleave_on_the_socket()
    {
        const int perThread = 50;
        using var server = new FakePs4Server(c =>
        {
            for (var i = 0; i < perThread * 2; i++)
            {
                var r = c.ReadRequest();
                if (r.Command == Ps4Command.Version)
                {
                    c.Send(U32(3), "1.3"u8.ToArray());
                }
                else
                {
                    Assert.Equal(Ps4Command.ProcRead, r.Command);
                    c.SendStatus(Ps4Status.Success);
                    c.Send(Enumerable.Repeat((byte)0x5A, (int)r.U32(12)).ToArray());
                }
            }
        });
        using var client = ConnectTo(server);

        var a = Task.Run(() => { for (var i = 0; i < perThread; i++) Assert.Equal("1.3", client.GetVersion()); });
        var b = Task.Run(() =>
        {
            for (var i = 0; i < perThread; i++) Assert.All(client.ReadMemory(1, 0x10, 64), x => Assert.Equal(0x5A, x));
        });
        await Task.WhenAll(a, b);
        server.WaitForScript();
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
