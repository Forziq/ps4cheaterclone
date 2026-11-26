using System.Net;
using System.Net.Sockets;
using PS4Mem.Core.Connection;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Settings;
using PS4Mem.Core.Tests.Fakes;
using static PS4Mem.Core.Tests.Fakes.FakeConsole;

namespace PS4Mem.Core.Tests;

public sealed class ConnectionManagerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ps4mem-conn-").FullName;
    private readonly StringWriter _logText = new();
    private readonly DebugLogSwitch _logs;
    private readonly AppSettingsStore _store;

    public ConnectionManagerTests()
    {
        _logs = new DebugLogSwitch(new DebugLog(_logText));
        _store = new AppSettingsStore(Path.Combine(_dir, "settings.json"), _logs);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ConnectionManager Manager(int port)
    {
        _store.Update(new AppSettingsUpdate { CommandPort = port });
        return new ConnectionManager(_store, IpSources.None, _logs);
    }

    private static void AnswerVersion(FakeConsole c)
    {
        c.Expect(Ps4Command.Version, 0);
        c.Send(U32(3), "1.3"u8.ToArray());
    }

    [Fact]
    public void Connect_reads_the_version_and_remembers_the_ip()
    {
        using var server = new FakePs4Server(AnswerVersion);
        using var manager = Manager(server.Port);

        var status = manager.Connect("127.0.0.1");
        server.WaitForScript();

        Assert.True(status.Connected);
        Assert.Equal("127.0.0.1", status.Ip);
        Assert.Equal(server.Port, status.Port);
        Assert.Equal("1.3", status.Version);
        Assert.Equal("the request", status.DefaultIpSource);
        Assert.Equal(status, manager.Status);
        Assert.Equal("127.0.0.1", _store.Current.ConsoleIp);
        Assert.Equal(["127.0.0.1"], _store.Current.RecentIps);
        Assert.Contains("[connection] Connected to 127.0.0.1", _logText.ToString());
    }

    [Fact]
    public void Connect_without_an_ip_uses_the_saved_one()
    {
        using var server = new FakePs4Server(AnswerVersion);
        using var manager = Manager(server.Port);
        _store.Update(new AppSettingsUpdate { ConsoleIp = "127.0.0.1" });

        Assert.Equal("127.0.0.1", manager.Status.DefaultIp);
        Assert.Equal("saved settings", manager.Status.DefaultIpSource);
        var status = manager.Connect();
        server.WaitForScript();

        Assert.True(status.Connected);
        Assert.Equal("saved settings", status.DefaultIpSource);
    }

    [Fact]
    public void Environment_ip_comes_before_the_saved_one()
    {
        _store.Update(new AppSettingsUpdate { ConsoleIp = "192.0.2.10" });
        using var manager = new ConnectionManager(_store, new IpSources("192.0.2.11", null), _logs);

        var status = manager.Status;
        Assert.Equal("192.0.2.11", status.DefaultIp);
        Assert.Contains("PS4_IP", status.DefaultIpSource);
    }

    [Fact]
    public void Connect_with_no_ip_anywhere_says_how_to_set_one()
    {
        using var manager = new ConnectionManager(_store, IpSources.None, _logs);

        var e = Assert.Throws<ArgumentException>(() => manager.Connect());
        Assert.Contains("PS4 IP is not set", e.Message);
        Assert.Null(manager.Status.DefaultIp);
    }

    [Fact]
    public void Connect_with_an_invalid_ip_is_an_argument_error()
    {
        using var manager = new ConnectionManager(_store, IpSources.None, _logs);
        Assert.Throws<ArgumentException>(() => manager.Connect("not-an-ip"));
    }

    [Fact]
    public void Failed_connect_reports_the_error_and_saves_nothing()
    {
        var port = FreePort();
        using var manager = Manager(port);

        var e = Assert.Throws<Ps4ProtocolException>(() => manager.Connect("127.0.0.1"));

        var status = manager.Status;
        Assert.False(status.Connected);
        Assert.Equal(e.Message, status.LastError);
        Assert.Empty(_store.Current.RecentIps);
    }

    [Fact]
    public void Second_connect_while_connected_is_a_state_error()
    {
        using var server = new FakePs4Server(AnswerVersion);
        using var manager = Manager(server.Port);
        manager.Connect("127.0.0.1");
        server.WaitForScript();

        var e = Assert.Throws<ConnectionStateException>(() => manager.Connect("127.0.0.1"));
        Assert.Contains("Already connected", e.Message);
    }

    [Fact]
    public void Commands_need_a_connection()
    {
        using var manager = new ConnectionManager(_store, IpSources.None, _logs);

        Assert.Throws<ConnectionStateException>(() => manager.GetProcesses());
        Assert.Throws<ConnectionStateException>(() => manager.GetProcessInfo(85));
        Assert.Throws<ConnectionStateException>(() => manager.GetSections(85));
    }

    [Fact]
    public void Processes_lists_every_process_and_only_suggests_the_game()
    {
        using var server = new FakePs4Server(c =>
        {
            AnswerVersion(c);
            c.Expect(Ps4Command.ProcList, 0);
            c.SendStatus(Ps4Status.Success);
            c.Send(U32(3),
                ProcEntry("SceShellCore", 45),
                ProcEntry("orbis_audiod.elf", 50),
                ProcEntry("ac3_engine_orbis_fn.elf", 85));
        });
        using var manager = Manager(server.Port);
        manager.Connect("127.0.0.1");

        var listing = manager.GetProcesses();
        server.WaitForScript();

        Assert.Equal(3, listing.Processes.Count);
        Assert.Equal(85, listing.Suggestion.Process?.Pid);
        Assert.Contains("*.elf", listing.Suggestion.Reason);
    }

    [Fact]
    public void Process_info_returns_the_title_id()
    {
        using var server = new FakePs4Server(c =>
        {
            AnswerVersion(c);
            var req = c.Expect(Ps4Command.ProcInfo, 4);
            Assert.Equal(85, req.I32(0));
            c.SendStatus(Ps4Status.Success);
            c.Send(ProcInfo(85, "ac3_engine_orbis_fn.elf", "/app0/ac3.elf", "CUSA11711", "EP0001-CUSA11711_00-X"));
        });
        using var manager = Manager(server.Port);
        manager.Connect("127.0.0.1");

        var info = manager.GetProcessInfo(85);
        server.WaitForScript();

        Assert.Equal("CUSA11711", info.TitleId);
        Assert.Equal("ac3_engine_orbis_fn.elf", info.Name);
    }

    private static byte[][] SampleMaps() =>
    [
        MapEntry("", 0x1000, 0x5000, 0, Ps4Protocol.ProtRead | Ps4Protocol.ProtWrite),
        MapEntry("libSceLibcInternal", 0x6000, 0x7000, 0, Ps4Protocol.ProtRead | Ps4Protocol.ProtWrite),
        MapEntry("executable", 0x8000, 0x9000, 0, Ps4Protocol.ProtRead),
        MapEntry("broken", 0xA000, 0xA000, 0, Ps4Protocol.ProtRead),
    ];

    private static Action<FakeConsole> AnswerMaps(int times) => c =>
    {
        AnswerVersion(c);
        for (var i = 0; i < times; i++)
        {
            c.Expect(Ps4Command.ProcMaps, 4);
            c.SendStatus(Ps4Status.Success);
            var maps = SampleMaps();
            c.Send([U32((uint)maps.Length), .. maps]);
        }
    };

    [Fact]
    public void Sections_have_the_default_selection_with_reasons()
    {
        using var server = new FakePs4Server(AnswerMaps(1));
        using var manager = Manager(server.Port);
        manager.Connect("127.0.0.1");

        var sel = manager.GetSections(85);
        server.WaitForScript();

        // "broken" (end == start) is dropped by the client; the rest are listed.
        Assert.Equal(3, sel.TotalCount);
        var unnamed = sel.Sections[0];
        Assert.True(unnamed.Selected);
        Assert.Contains("unnamed", unnamed.Reason);
        Assert.False(sel.Sections[1].Selected); // libSceLibcInternal: no filter key
        Assert.False(sel.Sections[2].Selected); // executable: read-only
        Assert.Equal("not writable", sel.Sections[2].Reason);
        Assert.Contains("[maps]", _logText.ToString());
    }

    [Fact]
    public void Section_options_change_the_filter_read_only_and_select_all()
    {
        using var server = new FakePs4Server(AnswerMaps(2));
        using var manager = Manager(server.Port);
        manager.Connect("127.0.0.1");

        var custom = manager.GetSections(85, new SectionOptions(["Libc"], IncludeReadOnly: true));
        var all = manager.GetSections(85, new SectionOptions(SelectAll: true));
        server.WaitForScript();

        Assert.True(custom.Sections[1].Selected);
        Assert.Contains("'Libc'", custom.Sections[1].Reason);
        Assert.Equal(3, all.SelectedCount);
    }

    [Fact]
    public void Disconnect_ends_the_session_cleanly()
    {
        var ended = false;
        using var server = new FakePs4Server(c =>
        {
            AnswerVersion(c);
            c.Expect(Ps4Command.ConsoleEnd, 0);
            ended = c.ClientClosed();
        });
        using var manager = Manager(server.Port);
        manager.Connect("127.0.0.1");

        manager.Disconnect();
        server.WaitForScript();

        Assert.True(ended);
        var status = manager.Status;
        Assert.False(status.Connected);
        Assert.Null(status.LastError);
        manager.Disconnect(); // safe when already disconnected
    }

    [Fact]
    public void A_dropped_connection_shows_as_lost_and_allows_a_new_connect()
    {
        using var server = new FakePs4Server(
            c =>
            {
                AnswerVersion(c);
                c.Expect(Ps4Command.ProcList, 0);
                c.Close(); // console goes away mid-command
            },
            AnswerVersion);
        using var manager = Manager(server.Port);
        manager.Connect("127.0.0.1");

        Assert.Throws<Ps4ProtocolException>(() => manager.GetProcesses());

        var status = manager.Status;
        Assert.False(status.Connected);
        Assert.Contains("lost", status.LastError);
        Assert.Throws<ConnectionStateException>(() => manager.GetProcesses());

        Assert.True(manager.Connect("127.0.0.1").Connected);
        server.WaitForScript();
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
