using System.Diagnostics;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Scanning;
using PS4Mem.Core.Sections;
using PS4Mem.Core.Tests.Fakes;

namespace PS4Mem.Core.Tests;

/// <summary>The background scan session behind /api/scan (T1.5).</summary>
public sealed class ScanSessionTests : IDisposable
{
    private const int Pid = 85;
    private const ulong Base = 0x2_0000_0000;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ps4mem-session-" + Guid.NewGuid().ToString("N"));
    private readonly ScanSession _session;
    private readonly FakeProcessMemory _memory = new();

    public ScanSessionTests()
    {
        _session = new ScanSession(Settings);
        // Two sections: three 1500s in the first, one in the second.
        _memory.SetU32(Base + 0x10, 1500);
        _memory.SetU32(Base + 0x20, 1500);
        _memory.SetU32(Base + 0x30, 1500);
        _memory.SetU32(Base + 0x1010, 1500);
    }

    public void Dispose()
    {
        _session.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private PS4MemSettings Settings => PS4MemSettings.Default with { ScanDataDirectory = _root, ScanChunkBytes = 0x400 };

    private static readonly Section[] Sections =
    [
        new(new MemoryMap("heap", Base, Base + 0x1000, 0, 3), true, "test"),
        new(new MemoryMap("", Base + 0x1000, Base + 0x2000, 0, 3), true, "test"),
    ];

    private ScanTarget Target(IProcessMemory? memory = null, Func<(ISectionScanner?, string)>? console = null,
        IProcessControl? control = null) =>
        new(memory ?? _memory, Settings, DebugLog.Disabled, console, control);

    private static readonly ScanRunOptions Paused = new() { PauseGame = true };

    private static ScanQuery Exact(uint value) => ScanQuery.ExactUInt32(value);

    private ScanStatus WaitForScan()
    {
        var watch = Stopwatch.StartNew();
        while (_session.Status.State == ScanState.Running)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "The scan did not finish.");
            Thread.Sleep(5);
        }
        return _session.Status;
    }

    [Fact]
    public void Idle_status_has_no_results()
    {
        var s = _session.Status;
        Assert.Equal(ScanState.Idle, s.State);
        Assert.Equal(0, s.ResultCount);
        Assert.Equal(0, s.ScanNumber);
        Assert.False(s.CanUndo);
        Assert.Null(s.Type);
    }

    [Fact]
    public void First_scan_runs_in_the_background_and_reports_hits_per_section()
    {
        var started = _session.StartFirst(Target(), Pid, Sections, Exact(1500));
        Assert.Equal("first", started.Kind);

        var s = WaitForScan();
        Assert.Equal(ScanState.Done, s.State);
        Assert.Equal(100, s.Percent);
        Assert.Equal(4, s.ResultCount);
        Assert.Equal(1, s.ScanNumber);
        Assert.Equal(Pid, s.Pid);
        Assert.Equal(ScanValueType.UInt32, s.Type);
        Assert.Equal("local", s.ResultMethod);
        Assert.Equal("exact u32 1500", s.Query);
        Assert.Equal([3L, 1L], s.Sections!.Select(x => x.Hits));
        Assert.Equal(0x2000, s.Total);
        Assert.Empty(_memory.Writes);
    }

    [Fact]
    public void Next_scan_narrows_the_results_and_undo_goes_back()
    {
        _session.StartFirst(Target(), Pid, Sections, Exact(1500));
        WaitForScan();
        _memory.SetU32(Base + 0x20, 1400);

        _session.StartNext(Target(), Exact(1500));
        var s = WaitForScan();
        Assert.Equal(ScanState.Done, s.State);
        Assert.Equal("next", s.Kind);
        Assert.Equal(3, s.ResultCount);
        Assert.Equal(2, s.ScanNumber);
        Assert.True(s.CanUndo);

        var undone = _session.Undo();
        Assert.Equal(ScanState.Idle, undone.State);
        Assert.Equal(4, undone.ResultCount);
        Assert.Equal(1, undone.ScanNumber);
        Assert.False(undone.CanUndo);
        Assert.Throws<ScanStateException>(() => _session.Undo());
    }

    [Fact]
    public void Next_scan_needs_results_and_the_same_type()
    {
        Assert.Throws<ScanStateException>(() => _session.StartNext(Target(), Exact(1)));

        _session.StartFirst(Target(), Pid, Sections, Exact(1500));
        WaitForScan();
        var e = Assert.Throws<ArgumentException>(() =>
            _session.StartNext(Target(), ScanQuery.Parse(ScanValueType.Float, ScanCompare.Exact, "1")));
        Assert.Contains("start a new scan", e.Message);
        Assert.Throws<ArgumentException>(() =>
            _session.StartNext(Target(), new ScanQuery(ScanValueType.UInt32, ScanCompare.UnknownInitial)));
    }

    [Fact]
    public void First_scan_checks_its_input_before_starting()
    {
        Assert.Throws<ArgumentException>(() => _session.StartFirst(Target(), Pid, [], Exact(1)));
        Assert.Throws<ArgumentException>(() =>
            _session.StartFirst(Target(), Pid, Sections, new ScanQuery(ScanValueType.UInt32, ScanCompare.Increased)));
        Assert.Equal(ScanState.Idle, _session.Status.State);
    }

    [Fact]
    public void Only_one_scan_runs_at_a_time_and_progress_shows_while_it_runs()
    {
        using var gate = new Gate(_memory, pass: 1);
        _session.StartFirst(Target(), Pid, Sections, Exact(1500));
        gate.WaitForRead(2); // the first chunk is done, the second is blocked

        var running = _session.Status;
        Assert.Equal(ScanState.Running, running.State);
        Assert.Null(running.Sections);
        Assert.Equal("heap", running.Section);
        Assert.Equal(0x400, running.Done);
        Assert.Equal(12.5, running.Percent);
        Assert.Equal(3, running.Hits);
        Assert.Throws<ScanStateException>(() => _session.StartFirst(Target(), Pid, Sections, Exact(1)));
        Assert.Throws<ScanStateException>(() => _session.Undo());

        gate.Open();
        Assert.Equal(ScanState.Done, WaitForScan().State);
    }

    [Fact]
    public void Cancel_keeps_the_previous_results()
    {
        _session.StartFirst(Target(), Pid, Sections, Exact(1500));
        WaitForScan();

        using var gate = new Gate(_memory, pass: 0);
        _session.StartNext(Target(), Exact(1500));
        gate.WaitForRead(1);
        _session.Cancel();
        gate.Open();

        var s = WaitForScan();
        Assert.Equal(ScanState.Cancelled, s.State);
        Assert.Contains("previous results are kept", s.Message);
        Assert.Equal(4, s.ResultCount);
        Assert.Equal(1, s.ScanNumber);
    }

    [Fact]
    public void Cancel_when_nothing_runs_is_fine()
    {
        Assert.Equal(ScanState.Idle, _session.Cancel().State);
    }

    [Fact]
    public async Task New_scan_cancels_the_running_scan_and_drops_every_result()
    {
        _session.StartFirst(Target(), Pid, Sections, Exact(1500));
        WaitForScan();

        using var gate = new Gate(_memory, pass: 0);
        _session.StartNext(Target(), Exact(1500));
        gate.WaitForRead(1);
        var newScan = _session.NewAsync();
        gate.Open();

        var s = await newScan.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ScanState.Idle, s.State);
        Assert.Equal(0, s.ResultCount);
        Assert.False(s.CanUndo);
    }

    [Fact]
    public void Release_during_a_scan_drops_its_results_when_it_stops()
    {
        _session.StartFirst(Target(), Pid, Sections, Exact(1500));
        WaitForScan();

        using var gate = new Gate(_memory, pass: 0);
        _session.StartNext(Target(), Exact(1500));
        gate.WaitForRead(1);
        _session.Release();
        gate.Open();

        var s = WaitForScan();
        Assert.Equal(ScanState.Cancelled, s.State);
        Assert.Contains("Disconnected", s.Message);
        Assert.Equal(0, s.ResultCount);
        Assert.False(s.CanUndo);
    }

    [Fact]
    public void Release_when_idle_drops_the_results()
    {
        _session.StartFirst(Target(), Pid, Sections, Exact(1500));
        WaitForScan();

        _session.Release();

        var s = _session.Status;
        Assert.Equal(ScanState.Idle, s.State);
        Assert.Equal(0, s.ResultCount);
    }

    [Fact]
    public void A_failing_read_fails_the_scan_with_its_message()
    {
        var broken = new FakeProcessMemory { OnRead = _ => throw new Ps4ProtocolException("The PS4 closed the connection.") };

        _session.StartFirst(Target(broken), Pid, Sections, Exact(1500));

        var s = WaitForScan();
        Assert.Equal(ScanState.Failed, s.State);
        Assert.Equal("The PS4 closed the connection.", s.Message);
    }

    [Fact]
    public void Console_method_without_a_console_scanner_scans_locally_with_a_note()
    {
        _session.StartFirst(Target(console: () => (null, "auth failed")), Pid, Sections, Exact(1500),
            new ScanRunOptions { Method = ScanMethod.Console });

        var s = WaitForScan();
        Assert.Equal(ScanState.Done, s.State);
        Assert.Equal(4, s.ResultCount);
        Assert.Equal("console", s.Method);
        Assert.Equal("local", s.ResultMethod);
        Assert.Contains("auth failed", s.Message);
    }

    [Fact]
    public void Unaligned_scan_uses_every_byte()
    {
        _memory.SetU32(Base + 0x41, 1500);

        _session.StartFirst(Target(), Pid, Sections, Exact(1500), new ScanRunOptions { Alignment = 1 });

        Assert.Equal(5, WaitForScan().ResultCount);
    }

    [Fact]
    public void Results_pages_show_address_value_and_section_and_the_total()
    {
        _session.StartFirst(Target(), Pid, Sections, Exact(1500));
        WaitForScan();

        var page = _session.GetResults(offset: 2, limit: 10);

        Assert.Equal(4, page.Total);
        Assert.Equal(1, page.ScanNumber);
        Assert.Equal(ScanValueType.UInt32, page.Type);
        Assert.Equal(4, page.ValueSize);
        Assert.Equal([2L, 3L], page.Rows.Select(r => r.Index));
        Assert.Equal([Base + 0x30, Base + 0x1010], page.Rows.Select(r => r.Address));
        Assert.Equal(["heap", ""], page.Rows.Select(r => r.Section.Name));
        Assert.All(page.Rows, r => Assert.Equal("1500", ValueFormatter.Format(ScanValueType.UInt32, r.Value)));
        Assert.Empty(_session.GetResults(offset: 10, limit: 10).Rows);
    }

    [Fact]
    public void Results_without_a_scan_are_an_empty_page()
    {
        var page = _session.GetResults(0, 5000);
        Assert.Equal(0, page.Total);
        Assert.Empty(page.Rows);
        Assert.Null(page.Type);
        Assert.Throws<ArgumentException>(() => _session.GetResults(-1, 10));
        Assert.Throws<ArgumentException>(() => _session.GetResults(0, 0));
    }

    [Fact]
    public void Current_values_are_read_for_the_visible_rows_only_in_batches()
    {
        _session.StartFirst(Target(), Pid, Sections, Exact(1500));
        WaitForScan();
        _memory.SetU32(Base + 0x20, 1499);
        _memory.SetU32(Base + 0x30, 1600);
        _memory.Reads.Clear();

        var page = _session.ReadCurrentValues(Target(), offset: 1, limit: 2);

        Assert.Equal(1, page.ScanNumber);
        Assert.Equal([1L, 2L], page.Values.Select(v => v.Index));
        Assert.Equal(["1499", "1600"], page.Values.Select(v => ValueFormatter.Format(ScanValueType.UInt32, v.Value)));
        Assert.Equal([(Base + 0x20, 0x14)], _memory.Reads);
        Assert.Empty(_memory.Writes);
        // The stored scan value doesn't change: it's the "previous value" column.
        Assert.Equal("1500", ValueFormatter.Format(ScanValueType.UInt32, _session.GetResults(1, 1).Rows[0].Value));
    }

    [Fact]
    public void Result_value_reader_splits_on_big_gaps_and_the_batch_limit()
    {
        var addresses = new ulong[] { 0x1000, 0x1004, 0x1100, 0x9000, 0x9008 };
        _memory.SetU32(0x9008, 7);

        var values = ResultValueReader.Read(_memory, Pid, addresses, 4, maxGap: 0x100, maxBatch: 0x80);

        Assert.Equal(5, values.Length);
        Assert.Equal(7u, BitConverter.ToUInt32(values[4]));
        Assert.Equal([(0x1000UL, 8), (0x1100UL, 4), (0x9000UL, 0xC)], _memory.Reads);
    }

    // ---- m3: pause the game during scans ----

    [Fact]
    public void Pause_game_stops_before_the_first_read_and_resumes_after_the_last()
    {
        var events = new List<string>();
        var control = new FakeControl(events);
        _memory.OnRead = a => { lock (events) events.Add("read"); };

        _session.StartFirst(Target(control: control), Pid, Sections, Exact(1500), Paused);
        var s = WaitForScan();

        Assert.Equal(ScanState.Done, s.State);
        Assert.False(s.GamePaused);
        Assert.Equal("stop 85", events[0]);
        Assert.Equal("resume 85", events[^1]);
        Assert.Equal(1, events.Count(e => e.StartsWith("resume")));
        Assert.All(events[1..^1], e => Assert.Equal("read", e));
    }

    [Fact]
    public void Status_shows_the_game_paused_while_the_scan_runs()
    {
        var control = new FakeControl([]);
        using var gate = new Gate(_memory, pass: 0);
        _session.StartFirst(Target(control: control), Pid, Sections, Exact(1500), Paused);
        gate.WaitForRead(1);

        Assert.True(_session.Status.GamePaused);
        gate.Open();
        Assert.False(WaitForScan().GamePaused);
    }

    [Fact]
    public void Next_scan_can_pause_the_game_too()
    {
        _session.StartFirst(Target(), Pid, Sections, Exact(1500));
        WaitForScan();
        var events = new List<string>();

        _session.StartNext(Target(control: new FakeControl(events)), Exact(1500), Paused);

        Assert.Equal(ScanState.Done, WaitForScan().State);
        Assert.Equal(["stop 85", "resume 85"], events);
    }

    [Fact]
    public void Game_is_resumed_after_a_cancel()
    {
        var events = new List<string>();
        using var gate = new Gate(_memory, pass: 0);
        _session.StartFirst(Target(control: new FakeControl(events)), Pid, Sections, Exact(1500), Paused);
        gate.WaitForRead(1);
        _session.Cancel();
        gate.Open();

        Assert.Equal(ScanState.Cancelled, WaitForScan().State);
        Assert.Equal(["stop 85", "resume 85"], events);
    }

    [Fact]
    public void Game_is_resumed_after_a_failed_read()
    {
        var events = new List<string>();
        var broken = new FakeProcessMemory { OnRead = _ => throw new Ps4ProtocolException("boom") };

        _session.StartFirst(Target(broken, control: new FakeControl(events)), Pid, Sections, Exact(1500), Paused);

        Assert.Equal(ScanState.Failed, WaitForScan().State);
        Assert.Equal(["stop 85", "resume 85"], events);
    }

    [Fact]
    public void Release_resumes_the_game_at_once_while_the_scan_is_still_stopping()
    {
        var events = new List<string>();
        using var gate = new Gate(_memory, pass: 0);
        _session.StartFirst(Target(control: new FakeControl(events)), Pid, Sections, Exact(1500), Paused);
        gate.WaitForRead(1);

        _session.Release();
        Assert.Equal(["stop 85", "resume 85"], events); // before the scan thread has stopped

        gate.Open();
        Assert.Equal(ScanState.Cancelled, WaitForScan().State);
        Assert.Equal(["stop 85", "resume 85"], events); // not resumed twice
    }

    [Fact]
    public void A_failed_pause_fails_the_scan_without_reading()
    {
        var control = new FakeControl([]) { FailStop = true };

        _session.StartFirst(Target(control: control), Pid, Sections, Exact(1500), Paused);

        var s = WaitForScan();
        Assert.Equal(ScanState.Failed, s.State);
        Assert.Contains("Couldn't pause the game", s.Message);
        Assert.Empty(_memory.Reads);
        Assert.False(s.GamePaused);
    }

    [Fact]
    public void A_failed_resume_is_reported_in_the_status()
    {
        var control = new FakeControl([]) { FailResume = true };

        _session.StartFirst(Target(control: control), Pid, Sections, Exact(1500), Paused);

        var s = WaitForScan();
        Assert.Equal(ScanState.Done, s.State);
        Assert.Contains("couldn't be resumed", s.Message);
        Assert.Contains("/api/processes/85/resume", s.Message);
    }

    [Fact]
    public void Pause_without_process_control_fails_clearly()
    {
        _session.StartFirst(Target(), Pid, Sections, Exact(1500), Paused);

        var s = WaitForScan();
        Assert.Equal(ScanState.Failed, s.State);
        Assert.Contains("can't pause the game", s.Message);
        Assert.Empty(_memory.Reads);
    }

    private sealed class FakeControl(List<string> events) : IProcessControl
    {
        public bool FailStop { get; init; }
        public bool FailResume { get; init; }

        public void StopProcess(int pid)
        {
            if (FailStop) throw new Ps4StatusException(Ps4Command.ProcessStop, Ps4Status.Error, true);
            lock (events) events.Add($"stop {pid}");
        }

        public void ResumeProcess(int pid)
        {
            if (FailResume) throw new Ps4ProtocolException("connection lost");
            lock (events) events.Add($"resume {pid}");
        }
    }

    /// <summary>Lets <c>pass</c> reads through, then blocks the next ones until <see cref="Open"/>, to look at a running scan.</summary>
    private sealed class Gate : IDisposable
    {
        private readonly ManualResetEventSlim _open = new();
        private readonly SemaphoreSlim _reads = new(0);
        private readonly FakeProcessMemory _memory;
        private int _count;

        public Gate(FakeProcessMemory memory, int pass)
        {
            _memory = memory;
            memory.OnRead = _ =>
            {
                _reads.Release();
                if (Interlocked.Increment(ref _count) > pass)
                    _open.Wait(TimeSpan.FromSeconds(10));
            };
        }

        /// <summary>Waits until read number <paramref name="n"/> has started.</summary>
        public void WaitForRead(int n)
        {
            for (var i = 0; i < n; i++)
                Assert.True(_reads.Wait(TimeSpan.FromSeconds(10)), "The scan did not read.");
        }

        public void Open() => _open.Set();

        public void Dispose()
        {
            Open();
            _memory.OnRead = null;
        }
    }
}
