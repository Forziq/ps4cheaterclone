using System.Buffers.Binary;
using System.Diagnostics;
using PS4Mem.Core.Connection;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Scanning;
using PS4Mem.Core.Sections;
using PS4Mem.Core.Settings;
using PS4Mem.Core.Tests.Fakes;

namespace PS4Mem.Core.Tests;

/// <summary>Scans that read with several connections at once (T2.4b).</summary>
public sealed class ParallelScanTests : IDisposable
{
    private const int Pid = 77;
    private const ulong Base = 0x1_0000_0000;
    private const int Size = 0x40000;
    private const uint Money = 123_456;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ps4mem-parallel-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // Small chunks so there are many reads to spread over the readers.
    private static readonly PS4MemSettings Small = PS4MemSettings.Default with
    {
        ScanChunkBytes = 0x400,
        NextScanMaxGapBytes = 0x40,
    };

    private static Section Sec(string name, ulong start, ulong size) =>
        new(new MemoryMap(name, start, start + size, 0, 3), true, "test");

    /// <summary>Wraps a memory and sleeps a random 0–1 ms before each read, so readers finish out of order.</summary>
    private sealed class JitterReader(IProcessMemory inner, int seed) : IProcessMemory
    {
        private readonly Random _random = new(seed);
        public int Reads;
        public Func<int, Exception?>? FailOnRead { get; init; }

        public void ReadMemory(int pid, ulong address, Memory<byte> destination, CancellationToken cancel = default)
        {
            var n = Interlocked.Increment(ref Reads);
            if (FailOnRead?.Invoke(n) is { } error) throw error;
            int delay;
            lock (_random) delay = _random.Next(0, 2);
            if (delay > 0) Thread.Sleep(delay);
            inner.ReadMemory(pid, address, destination, cancel);
        }

        public void WriteMemory(int pid, ulong address, ReadOnlySpan<byte> data) =>
            throw new InvalidOperationException("Scans never write.");
    }

    /// <summary>Random memory with Money planted at a few places, including edges of chunks and sections.</summary>
    private static DenseProcessMemory Memory()
    {
        var mem = new DenseProcessMemory(Base, Size);
        new Random(42).NextBytes(mem.Data);
        foreach (var offset in new[] { 0x10, 0x3FC, 0x400, 0x1000, 0x7FFC, 0x8000, 0x12340, 0x3FFFC })
            BinaryPrimitives.WriteUInt32LittleEndian(mem.Data.AsSpan(offset), Money);
        return mem;
    }

    /// <summary>Sections of many sizes over the memory, with an empty one and a gap.</summary>
    private static List<Section> Sections() =>
    [
        Sec("a", Base, 0x1000),
        Sec("tiny", Base + 0x1000, 0x10),
        Sec("empty", Base + 0x1010, 0),
        Sec("b", Base + 0x1010, 0x6FF0),
        Sec("c", Base + 0x8000, 0x20000),
        Sec("d", Base + 0x30000, 0x10000),
    ];

    private static List<IProcessMemory> Readers(IProcessMemory inner, int count) =>
        Enumerable.Range(0, count).Select(i => (IProcessMemory)new JitterReader(inner, i)).ToList();

    private static void AssertSame(ScanResults expected, ScanResults actual)
    {
        Assert.Equal(expected.Addresses.ToArray(), actual.Addresses.ToArray());
        for (var i = 0; i < expected.Count; i++)
            Assert.Equal(expected.ValueAt(i).ToArray(), actual.ValueAt(i).ToArray());
        Assert.Equal(expected.Sections.Select(s => (s.Section, s.FirstResult, s.Hits)),
            actual.Sections.Select(s => (s.Section, s.FirstResult, s.Hits)));
    }

    // ---- ParallelReader ---------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public void Parallel_reader_hands_every_range_over_in_order(int readerCount)
    {
        var mem = Memory();
        var readers = Readers(mem, readerCount);
        var seen = new List<int>();

        ParallelReader.Run(readers, Pid, Enumerable.Range(0, 300), i => new ReadRange(Base + (ulong)i * 0x100, 0x100),
            (i, data) =>
            {
                seen.Add(i);
                Assert.Equal(mem.Data.AsSpan(i * 0x100, 0x100).ToArray(), data.ToArray());
            });

        Assert.Equal(Enumerable.Range(0, 300), seen);
        Assert.Equal(300, readers.Cast<JitterReader>().Sum(r => r.Reads));
        if (readerCount > 1)
            Assert.True(readers.Cast<JitterReader>().Count(r => r.Reads > 0) >= 2, "Only one reader did any work.");
    }

    [Fact]
    public void Parallel_reader_stays_at_most_two_ranges_per_reader_ahead()
    {
        var mem = Memory();
        var readers = Readers(mem, 3);
        var consumed = 0;
        var worst = 0;

        ParallelReader.Run(readers, Pid, Enumerable.Range(0, 200), i => new ReadRange(Base + (ulong)i * 16, 16),
            (_, _) =>
            {
                var started = readers.Cast<JitterReader>().Sum(r => Volatile.Read(ref r.Reads));
                worst = Math.Max(worst, started - consumed);
                consumed++;
            });

        Assert.InRange(worst, 1, 6);
    }

    [Fact]
    public void A_failed_read_fails_the_whole_run_and_nothing_is_replaced_with_zeros()
    {
        var mem = Memory();
        var readers = new List<IProcessMemory>
        {
            new JitterReader(mem, 1),
            new JitterReader(mem, 2) { FailOnRead = n => n == 5 ? new Ps4ProtocolException("connection lost") : null },
            new JitterReader(mem, 3),
        };
        var consumed = 0;

        var e = Assert.Throws<Ps4ProtocolException>(() =>
            ParallelReader.Run(readers, Pid, Enumerable.Range(0, 1000), i => new ReadRange(Base + (ulong)i * 16, 16),
                (_, _) => consumed++));

        Assert.Equal("connection lost", e.Message);
        Assert.True(consumed < 1000);
    }

    [Fact]
    public void A_consumer_error_stops_the_readers_and_is_rethrown()
    {
        var readers = Readers(Memory(), 4);

        Assert.Throws<InvalidOperationException>(() =>
            ParallelReader.Run(readers, Pid, Enumerable.Range(0, 1000), i => new ReadRange(Base + (ulong)i * 16, 16),
                (i, _) => { if (i == 10) throw new InvalidOperationException("bad"); }));

        Assert.True(readers.Cast<JitterReader>().Sum(r => r.Reads) < 100);
    }

    [Fact]
    public void Cancel_stops_a_parallel_run()
    {
        using var cancel = new CancellationTokenSource();
        var readers = Readers(Memory(), 3);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            ParallelReader.Run(readers, Pid, Enumerable.Range(0, 100_000), i => new ReadRange(Base + (ulong)(i % 100) * 16, 16),
                (i, _) => { if (i == 50) cancel.Cancel(); }, cancel.Token));
    }

    // ---- Scans: identical results with 1 and N readers -----------------------------------------

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void First_and_next_scans_with_several_readers_match_the_serial_scan(int readerCount)
    {
        var mem = Memory();
        var serial = new ScanEngine(mem, Small);
        var parallel = new ScanEngine(mem, Small, readers: Readers(mem, readerCount));
        Assert.Equal(readerCount, parallel.ReaderCount);

        using var first1 = serial.FirstScan(Pid, Sections(), Money);
        using var firstN = parallel.FirstScan(Pid, Sections(), Money);
        AssertSame(first1, firstN);
        Assert.Equal(6, firstN.Sections.Count);
        Assert.True(firstN.Count >= 7);

        // Two of the values change; the next scan must drop them in both.
        BinaryPrimitives.WriteUInt32LittleEndian(mem.Data.AsSpan(0x400), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(mem.Data.AsSpan(0x12340), 2);
        using var next1 = serial.NextScan(first1, Money);
        using var nextN = parallel.NextScan(firstN, Money);
        AssertSame(next1, nextN);
        Assert.Equal(first1.Count - 2, nextN.Count);
    }

    [Fact]
    public void Unknown_initial_and_the_next_scan_with_several_readers_match_the_serial_scan()
    {
        var mem = Memory();
        using var storage = new ScanStorage(_root, DebugLog.Disabled);
        var serial = new ScanEngine(mem, Small, storage: storage);
        var parallel = new ScanEngine(mem, Small, storage: storage, readers: Readers(mem, 3));
        var unknown = new ScanQuery(ScanValueType.UInt32, ScanCompare.UnknownInitial, null, null);

        using var first1 = serial.FirstScan(Pid, Sections(), unknown);
        using var firstN = parallel.FirstScan(Pid, Sections(), unknown);
        Assert.Equal(first1.Count, firstN.Count);
        Assert.Equal(first1.Sections.Select(s => (s.FirstResult, s.Hits)), firstN.Sections.Select(s => (s.FirstResult, s.Hits)));

        // Lower every 64th u32, then "decreased" must find exactly those, both ways.
        for (var o = 0; o < Size; o += 256)
        {
            var v = BinaryPrimitives.ReadUInt32LittleEndian(mem.Data.AsSpan(o));
            if (v > 1) BinaryPrimitives.WriteUInt32LittleEndian(mem.Data.AsSpan(o), v - 1);
        }
        var decreased = new ScanQuery(ScanValueType.UInt32, ScanCompare.Decreased, null, null);
        using var next1 = serial.NextScan(first1, decreased);
        using var nextN = parallel.NextScan(firstN, decreased);
        AssertSame(next1, nextN);
        Assert.True(nextN.Count > 100);
    }

    [Fact]
    public void A_scan_whose_read_connection_fails_fails_instead_of_finding_zeros()
    {
        var mem = Memory();
        var readers = new List<IProcessMemory>
        {
            new JitterReader(mem, 1),
            new JitterReader(mem, 2) { FailOnRead = n => n == 3 ? new Ps4ProtocolException("lost") : null },
        };
        var engine = new ScanEngine(mem, Small, readers: readers);

        Assert.Throws<Ps4ProtocolException>(() => engine.FirstScan(Pid, Sections(), 0u));
    }

    [Fact]
    public void Scan_session_reads_with_the_targets_read_connections_not_the_main_one()
    {
        var main = new FakeProcessMemory();     // the main connection
        var console = new FakeProcessMemory();  // what the read connections see
        console.SetU32(Base + 0x10, Money);
        var readers = Readers(console, 3);
        using var session = new ScanSession(Small);
        var target = new ScanTarget(main, Small with { ScanDataDirectory = _root }, DebugLog.Disabled,
            Readers: _ => readers);

        session.StartFirst(target, Pid, [Sec("a", Base, 0x2000)], ScanQuery.ExactUInt32(Money));
        var watch = Stopwatch.StartNew();
        while (session.Status.State == ScanState.Running)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "The scan did not finish.");
            Thread.Sleep(5);
        }

        Assert.Equal(ScanState.Done, session.Status.State);
        Assert.Equal(1, session.Status.ResultCount);
        Assert.Empty(main.Reads); // the main connection stays free for locks and live values
        Assert.Equal(8, readers.Cast<JitterReader>().Sum(r => r.Reads));
    }

    // ---- ScanReaderPool -----------------------------------------------------------------------

    private sealed class FakeConnection(Func<string>? version = null, bool failConnect = false) : IReadableConnection
    {
        private readonly ManualResetEventSlim _aborted = new();
        public bool IsConnected { get; set; }
        public bool Disposed { get; private set; }
        public bool Aborted => _aborted.IsSet;

        public void Connect()
        {
            if (failConnect) throw new Ps4ProtocolException("refused");
            IsConnected = true;
        }

        /// <summary>A "hanging" version waits until Abort, like a socket that never answers.</summary>
        public string GetVersion() => version?.Invoke() ?? "1.0";

        public void WaitForAbort()
        {
            _aborted.Wait(TimeSpan.FromSeconds(10));
            throw new Ps4ProtocolException("aborted");
        }

        public void Abort()
        {
            IsConnected = false;
            _aborted.Set();
        }

        public void Dispose()
        {
            IsConnected = false;
            Disposed = true;
        }

        public void ReadMemory(int pid, ulong address, Memory<byte> destination, CancellationToken cancel = default) { }
        public void WriteMemory(int pid, ulong address, ReadOnlySpan<byte> data) => throw new InvalidOperationException();
    }

    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Pool_opens_its_connections_on_first_use_and_keeps_them()
    {
        var made = new List<FakeConnection>();
        using var pool = new ScanReaderPool(() => { var c = new FakeConnection(); lock (made) made.Add(c); return c; },
            3, DebugLog.Disabled, OpenTimeout);
        Assert.Empty(made); // nothing opened until a scan asks

        var first = pool.Acquire();
        var second = pool.Acquire();

        Assert.Equal(3, first.Count);
        Assert.Equal(first, second);
        Assert.Equal(3, made.Count);
    }

    [Fact]
    public void Pool_skips_connections_it_cannot_open_and_retries_them_next_time()
    {
        var calls = 0;
        using var pool = new ScanReaderPool(() => new FakeConnection(failConnect: Interlocked.Increment(ref calls) <= 2),
            3, DebugLog.Disabled, OpenTimeout);

        Assert.Single(pool.Acquire());
        Assert.Equal(3, pool.Acquire().Count);
    }

    [Fact]
    public void Pool_replaces_lost_connections()
    {
        var made = new List<FakeConnection>();
        using var pool = new ScanReaderPool(() => { var c = new FakeConnection(); lock (made) made.Add(c); return c; },
            2, DebugLog.Disabled, OpenTimeout);
        pool.Acquire();
        made[0].IsConnected = false;

        var again = pool.Acquire();

        Assert.Equal(2, again.Count);
        Assert.True(made[0].Disposed);
        Assert.DoesNotContain(made[0], again);
        Assert.Equal(3, made.Count);
    }

    [Fact]
    public void Pool_gives_up_on_a_connection_that_never_answers()
    {
        FakeConnection? hanging = null;
        var calls = 0;
        using var pool = new ScanReaderPool(() =>
        {
            if (Interlocked.Increment(ref calls) > 1) return new FakeConnection();
            FakeConnection c = null!;
            c = new FakeConnection(() => { c.WaitForAbort(); return ""; });
            return hanging = c;
        }, 2, DebugLog.Disabled, TimeSpan.FromMilliseconds(200));

        var watch = Stopwatch.StartNew();
        var readers = pool.Acquire();

        Assert.Single(readers);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(hanging!.Aborted);
        SpinWait.SpinUntil(() => hanging.Disposed, TimeSpan.FromSeconds(5));
        Assert.True(hanging.Disposed);
    }

    [Fact]
    public void Pool_of_size_0_never_opens_anything_and_dispose_closes_everything()
    {
        using (var none = new ScanReaderPool(() => throw new InvalidOperationException("must not open"), 0,
                   DebugLog.Disabled, OpenTimeout))
            Assert.Empty(none.Acquire());

        var made = new List<FakeConnection>();
        var pool = new ScanReaderPool(() => { var c = new FakeConnection(); lock (made) made.Add(c); return c; },
            2, DebugLog.Disabled, OpenTimeout);
        pool.Acquire();
        pool.Dispose();

        Assert.All(made, c => Assert.True(c.Disposed));
        Assert.Throws<ObjectDisposedException>(() => pool.Acquire());
    }

    // ---- Real sockets -------------------------------------------------------------------------

    /// <summary>
    /// A fake ps4debug that, like the real one, serves each client on its own thread with at most
    /// <paramref name="maxClients"/> slots (a client past that is accepted and closed at once). It
    /// answers CMD_VERSION and CMD_PROC_READ from <paramref name="memory"/>, CMD_CONSOLE_END closes.
    /// </summary>
    private sealed class ThreadedFakeConsole : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
        private readonly DenseProcessMemory _memory;
        private readonly int _maxClients;
        private int _clients, _reading;
        public int Port { get; }
        public int PeakConcurrentReads;
        public int Accepted;

        public ThreadedFakeConsole(DenseProcessMemory memory, int maxClients)
        {
            _memory = memory;
            _maxClients = maxClients;
            _listener.Start();
            Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
            new Thread(AcceptLoop) { IsBackground = true }.Start();
        }

        private void AcceptLoop()
        {
            while (true)
            {
                System.Net.Sockets.TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch (Exception) { return; }
                Interlocked.Increment(ref Accepted);
                if (Interlocked.Increment(ref _clients) > _maxClients)
                {
                    Interlocked.Decrement(ref _clients);
                    client.Close(); // a full server closes without a word (server.c:357-363)
                    continue;
                }
                new Thread(() => Serve(client)) { IsBackground = true }.Start();
            }
        }

        private void Serve(System.Net.Sockets.TcpClient client)
        {
            try
            {
                using var stream = client.GetStream();
                var c = new FakeConsole(client, stream);
                while (true)
                {
                    var req = c.ReadRequest();
                    if (req.Command == Ps4Command.Version)
                        c.Send(FakeConsole.U32(3), "1.3"u8.ToArray());
                    else if (req.Command == Ps4Command.ProcRead)
                    {
                        var now = Interlocked.Increment(ref _reading);
                        InterlockedMax(ref PeakConcurrentReads, now);
                        var data = new byte[req.U32(12)];
                        _memory.ReadMemory(req.I32(0), req.U64(4), data);
                        Thread.Sleep(2); // like the console's 64 KB steps: a read takes a while
                        c.SendStatus(Ps4Status.Success);
                        c.Send(data);
                        Interlocked.Decrement(ref _reading);
                    }
                    else return; // CMD_CONSOLE_END or anything else: close
                }
            }
            catch (Exception)
            {
                // the client went away
            }
            finally
            {
                client.Close();
                Interlocked.Decrement(ref _clients);
            }
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
        }

        public void Dispose() => _listener.Stop();
    }

    [Fact]
    public void Pool_of_real_clients_scans_in_parallel_and_skips_a_connection_the_full_server_closes()
    {
        var mem = Memory();
        using var console = new ThreadedFakeConsole(mem, maxClients: 3); // the main connection + 2
        var settings = Small with { ConsoleIp = "127.0.0.1", CommandPort = console.Port };
        using var main = new PS4DebugClient(settings);
        main.Connect();
        Assert.Equal("1.3", main.GetVersion());

        using var pool = new ScanReaderPool(() => new PS4DebugClient(settings), 3, DebugLog.Disabled, OpenTimeout);
        var readers = pool.Acquire();
        Assert.Equal(2, readers.Count); // the third was accepted, then closed: skipped, not fatal

        using var expected = new ScanEngine(mem, Small).FirstScan(Pid, Sections(), Money);
        using var actual = new ScanEngine(main, settings, readers: readers).FirstScan(Pid, Sections(), Money);

        AssertSame(expected, actual);
        Assert.Equal(2, console.PeakConcurrentReads);
        Assert.True(main.IsConnected);
    }

    // ---- Settings -----------------------------------------------------------------------------

    [Fact]
    public void Read_connections_default_to_3_and_the_app_setting_overrides_or_falls_back()
    {
        Assert.Equal(3, PS4MemSettings.Default.ScanReadConnections);
        var baseSettings = PS4MemSettings.Default with { ScanReadConnections = 0 };
        Assert.Equal(0, AppSettings.Default.ApplyTo(baseSettings).ScanReadConnections);
        Assert.Equal(5, (AppSettings.Default with { ScanReadConnections = 5 }).ApplyTo(baseSettings).ScanReadConnections);
        Assert.Empty((AppSettings.Default with { ScanReadConnections = 6 }).Validate());
        Assert.Single((AppSettings.Default with { ScanReadConnections = 7 }).Validate());
        Assert.Single((AppSettings.Default with { ScanReadConnections = -1 }).Validate());
        Assert.Equal(4, new AppSettingsUpdate { ScanReadConnections = 4 }.ApplyTo(AppSettings.Default).ScanReadConnections);
    }
}
