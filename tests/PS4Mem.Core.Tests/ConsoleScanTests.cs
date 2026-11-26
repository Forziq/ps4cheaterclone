using PS4Mem.Core;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Scanning;
using PS4Mem.Core.Sections;
using PS4Mem.Core.Tests.Fakes;
using static PS4Mem.Core.Tests.Fakes.FakeConsole;

namespace PS4Mem.Core.Tests;

public class ConsoleScanTests
{
    private const int Pid = 77;
    private const uint Money = 123_456;

    private static Section Sec(string name, ulong start, ulong size) =>
        new(new MemoryMap(name, start, start + size, 0, 3), true, "test");

    private static PS4DebugClient ConnectTo(FakePs4Server server)
    {
        var client = new PS4DebugClient(PS4MemSettings.Default with { ConsoleIp = "127.0.0.1", CommandPort = server.Port });
        client.Connect();
        return client;
    }

    /// <summary>Server side of a successful CMD_PROC_AUTH (protocol.md §12).</summary>
    private static void AcceptAuth(FakeConsole c)
    {
        var req = c.Expect(Ps4Command.ProcAuth, 8);
        Assert.Equal(Ps4Auth.Magic, req.U32(0));
        Assert.Equal(Ps4Auth.ScanBit, req.U32(4));
        var challenge = Enumerable.Range(0, 64).Select(i => (byte)(255 - i)).ToArray();
        c.SendStatus(Ps4Status.Success);
        c.Send(U16(64), challenge);
        Assert.Equal(Ps4Auth.Respond(challenge), c.ReadBytes(64));
        c.SendStatus(Ps4Status.Success);
    }

    /// <summary>One batch frame: u64 payloadLen, then records {u32 offset; u32 value}.</summary>
    private static byte[] Batch(params (uint Offset, uint Value)[] records) =>
        [.. U64((ulong)records.Length * 8), .. records.SelectMany(r => U32(r.Offset).Concat(U32(r.Value)))];

    private static readonly byte[] Sentinel = U64(ulong.MaxValue);

    [Fact]
    public void TryCreate_uses_turbo_when_caps_answers()
    {
        using var server = new FakePs4Server(c =>
        {
            AcceptAuth(c);
            c.Expect(Ps4Command.TurboCaps, 0);
            c.SendStatus(Ps4Status.Success);
            c.Send(U32(1), U32(0xFFF), U32(4), U32(0));
        });
        using var client = ConnectTo(server);

        var (scanner, reason) = ConsoleSectionScanner.TryCreate(client, PS4MemSettings.Default);
        server.WaitForScript();

        Assert.NotNull(scanner);
        Assert.True(scanner.Turbo);
        Assert.Contains("Turbo Scan (version 1, engines 0xFFF)", reason);
    }

    [Fact]
    public void TryCreate_falls_back_to_the_trio_and_reconnects_when_caps_fails()
    {
        using var server = new FakePs4Server(
            c =>
            {
                AcceptAuth(c);
                c.Expect(Ps4Command.TurboCaps, 0);
                c.SendStatus(Ps4Status.Error); // NG without turbo: unknown command, then close
                c.Close();
            },
            c => { c.Expect(Ps4Command.ConsoleEnd, 0); Assert.True(c.ClientClosed()); }); // the reconnect
        using var client = ConnectTo(server);

        var (scanner, _) = ConsoleSectionScanner.TryCreate(client, PS4MemSettings.Default);

        Assert.NotNull(scanner);
        Assert.False(scanner.Turbo);
        Assert.True(client.IsConnected);
        client.Disconnect();
        server.WaitForScript();
    }

    [Fact]
    public void TryCreate_returns_null_and_reconnects_on_stock_ps4debug()
    {
        using var server = new FakePs4Server(
            c =>
            {
                c.Expect(Ps4Command.ProcAuth, 8);
                c.Close(); // stock ps4debug: unknown command, no status, connection closed
            },
            c => { c.Expect(Ps4Command.ConsoleEnd, 0); Assert.True(c.ClientClosed()); });
        using var client = ConnectTo(server);

        var (scanner, reason) = ConsoleSectionScanner.TryCreate(client, PS4MemSettings.Default);

        Assert.Null(scanner);
        Assert.Contains("needs ps4debug-NG", reason);
        Assert.True(client.IsConnected);
        client.Disconnect();
        server.WaitForScript();
    }

    [Fact]
    public void Turbo_scan_sends_the_27_byte_start_and_reads_batches_until_the_sentinel()
    {
        FakeRequest? start = null;
        byte[]? pattern = null;
        using var server = new FakePs4Server(c =>
        {
            start = c.Expect(Ps4Command.TurboStart, 27);
            c.SendStatus(Ps4Status.Success);
            pattern = c.ReadBytes(4);
            c.SendStatus(Ps4Status.Success);
            c.Send(Batch((0x40, Money), (0x10, Money)), Batch((0x1FC, Money)), Sentinel);
            c.SendStatus(Ps4Status.Success);
        });
        using var client = ConnectTo(server);
        var engine = new ScanEngine(client, consoleScanner: new ConsoleSectionScanner(client, turbo: true));

        var results = engine.FirstScan(Pid, [Sec("anon:a", 0x2000, 0x200)], Money,
            new ScanOptions { Method = ScanMethod.Console });
        server.WaitForScript();

        Assert.Equal(Pid, start!.I32(0));
        Assert.Equal(0x2000UL, start.U64(4));
        Assert.Equal(0x200U, start.U32(12));          // whole section (smaller than one piece)
        Assert.Equal((byte)Ps4ScanValueType.UInt32, start.Body[16]);
        Assert.Equal((byte)Ps4ScanCompareType.ExactValue, start.Body[17]);
        Assert.Equal(4, start.Body[18]);              // alignment
        Assert.Equal(4U, start.U32(19));              // lenData = value only
        Assert.Equal(0U, start.U32(23));              // flags: stream, not server-resident
        Assert.Equal(U32(Money), pattern);
        Assert.Equal([0x2010UL, 0x2040, 0x21FC], results.Addresses); // absolute and sorted
        Assert.Equal("console", results.Method);
        Assert.Equal(3, results.Sections[0].Hits);
    }

    [Fact]
    public void Trio_scan_sends_the_23_byte_start()
    {
        FakeRequest? start = null;
        using var server = new FakePs4Server(c =>
        {
            start = c.Expect(Ps4Command.ProcScanStart, 23);
            c.SendStatus(Ps4Status.Success);
            c.ReadBytes(4);
            c.SendStatus(Ps4Status.Success);
            c.Send(Batch((8, Money)), Sentinel);
            c.SendStatus(Ps4Status.Success);
        });
        using var client = ConnectTo(server);
        var hits = new ScanHitBuffer();

        new ConsoleSectionScanner(client, turbo: false)
            .ScanSection(Pid, Sec("anon:a", 0x5000, 0x100), Money, 1, hits, _ => { }, default);
        server.WaitForScript();

        Assert.Equal(1, start!.Body[18]);
        Assert.Equal(1, hits.Count);
        Assert.Equal(0x5008UL, hits.AddressAt(0));
    }

    [Fact]
    public void Auth_refused_before_streaming_closes_the_connection()
    {
        using var server = new FakePs4Server(c =>
        {
            c.Expect(Ps4Command.TurboStart, 27);
            c.SendStatus(Ps4Status.DataNull); // not authenticated
            c.Close();
        });
        using var client = ConnectTo(server);

        var e = Assert.Throws<Ps4StatusException>(() => new ConsoleSectionScanner(client, turbo: true)
            .ScanSection(Pid, Sec("anon:a", 0x5000, 0x100), Money, 4, new ScanHitBuffer(), _ => { }, default));

        Assert.Equal(Ps4Status.DataNull, e.Status);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void A_batch_length_that_is_not_whole_records_is_rejected()
    {
        using var server = new FakePs4Server(c =>
        {
            c.Expect(Ps4Command.TurboStart, 27);
            c.SendStatus(Ps4Status.Success);
            c.ReadBytes(4);
            c.SendStatus(Ps4Status.Success);
            c.Send(U64(7));
        });
        using var client = ConnectTo(server);

        Assert.Throws<Ps4ProtocolException>(() => new ConsoleSectionScanner(client, turbo: true)
            .ScanSection(Pid, Sec("anon:a", 0x5000, 0x100), Money, 4, new ScanHitBuffer(), _ => { }, default));
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void Sections_over_one_piece_are_split_with_a_3_byte_overlap_and_edge_hits_counted_once()
    {
        // A fake client-side scan of a 1 GB + 0x100 section: two pieces.
        var requests = new List<FakeRequest>();
        const ulong start = 0x1_0000_0000;
        using var server = new FakePs4Server(c =>
        {
            for (var i = 0; i < 2; i++)
            {
                var r = c.Expect(Ps4Command.TurboStart, 27);
                requests.Add(r);
                c.SendStatus(Ps4Status.Success);
                c.ReadBytes(4);
                c.SendStatus(Ps4Status.Success);
                if (i == 0)
                    c.Send(Batch((0x4000_0000, Money)), Sentinel);      // in the overlap: the next piece reports it
                else
                    c.Send(Batch((0, Money), (0x80, Money)), Sentinel);
                c.SendStatus(Ps4Status.Success);
            }
        });
        using var client = ConnectTo(server);
        var hits = new ScanHitBuffer();

        new ConsoleSectionScanner(client, turbo: true)
            .ScanSection(Pid, Sec("anon:big", start, 0x4000_0100), Money, 1, hits, _ => { }, default);
        server.WaitForScript();

        Assert.Equal(start, requests[0].U64(4));
        Assert.Equal(0x4000_0003U, requests[0].U32(12));
        Assert.Equal(start + 0x4000_0000, requests[1].U64(4));
        Assert.Equal(0x100U, requests[1].U32(12));
        Assert.Equal(2, hits.Count);
        Assert.Equal(start + 0x4000_0000, hits.AddressAt(0));
        Assert.Equal(start + 0x4000_0080, hits.AddressAt(1));
    }
}
