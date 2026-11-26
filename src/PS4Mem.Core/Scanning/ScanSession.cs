using System.Diagnostics;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Sections;

namespace PS4Mem.Core.Scanning;

public enum ScanState
{
    /// <summary>Nothing has run since the last new scan, undo or disconnect.</summary>
    Idle,
    Running,
    Done,
    Cancelled,
    Failed,
}

/// <summary>A scan is running, or there's nothing to undo. The API answers with 409 Conflict.</summary>
public sealed class ScanStateException(string message) : InvalidOperationException(message);

/// <summary>How to run one scan.</summary>
public sealed record ScanRunOptions
{
    /// <summary>First scan only: compare on the Mac (default) or on the PS4. Next scans always re-read on the Mac.</summary>
    public ScanMethod Method { get; init; } = ScanMethod.Local;

    /// <summary>Scan step; null uses <see cref="PS4MemSettings.ScanAlignment"/>, 1 = every byte.</summary>
    public int? Alignment { get; init; }

    /// <summary>
    /// Stop the game while the scan reads its memory, so values can't change mid-scan. The game
    /// is always resumed afterwards: done, failed, cancelled or disconnected.
    /// </summary>
    public bool PauseGame { get; init; }
}

/// <summary>
/// What <c>GET /api/scan/progress</c> and the scan calls report: the running or last scan
/// (state, progress, time, message) and the current results (count, type, per-section hits).
/// </summary>
public sealed record ScanStatus
{
    public ScanState State { get; init; }

    /// <summary>"first" or "next": the running or last scan. Null when idle.</summary>
    public string? Kind { get; init; }

    /// <summary>The running or last scan's query, e.g. "exact u32 1500".</summary>
    public string? Query { get; init; }

    /// <summary>The method asked for ("local" or "console").</summary>
    public string? Method { get; init; }

    /// <summary>0 to 100.</summary>
    public double Percent { get; init; }

    /// <summary>The section being scanned (or last scanned).</summary>
    public string? Section { get; init; }

    /// <summary>Bytes (first scan, next after unknown initial) or previous results (next scan) done.</summary>
    public long Done { get; init; }

    public long Total { get; init; }

    /// <summary>Hits so far.</summary>
    public long Hits { get; init; }

    public double ElapsedSeconds { get; init; }

    /// <summary>Why the scan failed or was cancelled, or a note such as a console scan fallback.</summary>
    public string? Message { get; init; }

    /// <summary>True while this scan has the game stopped ("pause the game during scans").</summary>
    public bool GamePaused { get; init; }

    // ---- The current results (what the results table shows) ----

    public int? Pid { get; init; }

    /// <summary>1 after the first scan, 2 after the first next scan ...; 0 = no results.</summary>
    public int ScanNumber { get; init; }

    public long ResultCount { get; init; }

    /// <summary>The results' value type, e.g. "u32"; a next scan must use the same.</summary>
    public ScanValueType? Type { get; init; }

    public int ValueSize { get; init; }

    /// <summary>"local" or "console": how the current results were found.</summary>
    public string? ResultMethod { get; init; }

    public bool CanUndo { get; init; }

    /// <summary>Hits per section of the current results; null while a scan runs.</summary>
    public IReadOnlyList<SectionScanStats>? Sections { get; init; }
}

/// <summary>One row of the results table: the value as the last scan found it, and its section.</summary>
public sealed record ScanResultRow(long Index, ulong Address, byte[] Value, Section Section);

/// <summary>A page of the current results. <see cref="Total"/> is every result, not just this page.</summary>
public sealed record ScanResultsPage(int ScanNumber, ScanValueType? Type, int ValueSize, long Total, long Offset,
    IReadOnlyList<ScanResultRow> Rows);

/// <summary>A result's value read from the PS4 just now.</summary>
public sealed record ScanCurrentValue(long Index, ulong Address, byte[] Value);

/// <summary>Current values of a page of results (the visible rows).</summary>
public sealed record ScanValuesPage(int ScanNumber, ScanValueType? Type, long Offset, IReadOnlyList<ScanCurrentValue> Values);

/// <summary>
/// The app's scan session (one per backend): first, next, new scan, undo and cancel, with
/// scans running in the background so the caller can poll <see cref="Status"/> for progress.
/// One scan at a time. Results live in a <see cref="ScanHistory"/>; big ones spill to a
/// <see cref="ScanStorage"/> folder. <see cref="Release"/> (disconnect) cancels the scan and
/// drops every result. Each scan logs its time and per-section counts (category <c>scan</c>).
/// </summary>
public sealed class ScanSession : IDisposable
{
    private const string Cat = "scan";

    private readonly Lock _lock = new();
    private readonly PS4MemSettings _defaults;
    private ScanHistory _history;
    private ScanStorage? _storage;
    private DebugLog _log = DebugLog.Disabled;

    private Run? _run;                     // the running or last scan; null when idle
    private Task? _task;
    private CancellationTokenSource? _cancel;
    private long _generation;              // bumped by Release: a scan started before it is dropped
    private bool _disposed;

    public ScanSession(PS4MemSettings? defaults = null)
    {
        _defaults = defaults ?? PS4MemSettings.Default;
        _history = new ScanHistory(_defaults.ScanUndoLevels);
    }

    public ScanStatus Status
    {
        get { lock (_lock) return StatusLocked(); }
    }

    /// <summary>The current results' value type, or null when there are none.</summary>
    public ScanValueType? CurrentType
    {
        get { lock (_lock) return _history.Current?.Type; }
    }

    /// <summary>
    /// Results <c>[offset, offset + limit)</c> of the current results (fewer at the end). Works
    /// while a scan runs: that shows the results the running scan started from.
    /// </summary>
    public ScanResultsPage GetResults(long offset, int limit)
    {
        CheckPage(offset, limit);
        lock (_lock)
        {
            var current = _history.Current;
            if (current is null)
                return new ScanResultsPage(0, null, 0, 0, offset, []);
            var end = Math.Min(current.Count, offset + limit);
            var rows = new List<ScanResultRow>((int)Math.Max(0, end - offset));
            for (var i = offset; i < end; i++)
                rows.Add(new ScanResultRow(i, current.AddressAt(i), current.ValueAt(i).ToArray(), current.SectionOf(i)));
            return new ScanResultsPage(current.ScanNumber, current.Type, current.ValueSize, current.Count, offset, rows);
        }
    }

    /// <summary>
    /// Reads the current value of results <c>[offset, offset + limit)</c> from the PS4 (the
    /// visible rows), batching nearby addresses like a next scan. Reads only; never writes.
    /// </summary>
    public ScanValuesPage ReadCurrentValues(ScanTarget target, long offset, int limit, CancellationToken cancel = default)
    {
        CheckPage(offset, limit);
        int pid, size, scanNumber;
        ScanValueType type;
        ulong[] addresses;
        lock (_lock)
        {
            var current = _history.Current;
            if (current is null)
                return new ScanValuesPage(0, null, offset, []);
            var end = Math.Min(current.Count, offset + limit);
            addresses = new ulong[Math.Max(0, end - offset)];
            for (var i = 0; i < addresses.Length; i++)
                addresses[i] = current.AddressAt(offset + i);
            (pid, size, scanNumber, type) = (current.Pid, current.ValueSize, current.ScanNumber, current.Type);
        }

        // Outside the lock: network reads shouldn't hold up status calls.
        var values = ResultValueReader.Read(target.Memory, pid, addresses, size,
            target.Settings.NextScanMaxGapBytes, target.Settings.ScanChunkBytes, cancel);
        return new ScanValuesPage(scanNumber, type, offset,
            addresses.Select((a, i) => new ScanCurrentValue(offset + i, a, values[i])).ToList());
    }

    private static void CheckPage(long offset, int limit)
    {
        if (offset < 0) throw new ArgumentException("offset can't be negative.");
        if (limit < 1) throw new ArgumentException("limit must be at least 1.");
    }

    /// <summary>Starts a first scan of <paramref name="sections"/> in the background.</summary>
    /// <exception cref="ArgumentException">No sections, or a compare that needs a previous scan.</exception>
    /// <exception cref="ScanStateException">A scan is running.</exception>
    public ScanStatus StartFirst(ScanTarget target, int pid, IReadOnlyList<Section> sections, ScanQuery query,
        ScanRunOptions? options = null)
    {
        options ??= new ScanRunOptions();
        if (!query.Compare.AllowedOnFirstScan())
            throw new ArgumentException($"'{query.Compare.ShortName()}' needs a previous scan: use it for a next scan.");
        if (sections.Count == 0)
            throw new ArgumentException("No sections to scan. Select at least one section.");

        lock (_lock)
        {
            ThrowIfBusy();
            if (_history.UndoLevels != target.Settings.ScanUndoLevels && _history.Current is null)
                _history = new ScanHistory(target.Settings.ScanUndoLevels);
            var list = sections.ToList();
            return Start(target, new Run("first", query, options.Method, pid),
                (engine, o, ct) => engine.FirstScan(pid, list, query, o, ct), options);
        }
    }

    /// <summary>Starts a next scan of the current results in the background.</summary>
    /// <exception cref="ArgumentException">Wrong type, or a first-scan-only compare.</exception>
    /// <exception cref="ScanStateException">A scan is running, or there are no results yet.</exception>
    public ScanStatus StartNext(ScanTarget target, ScanQuery query, ScanRunOptions? options = null)
    {
        options ??= new ScanRunOptions();
        if (!query.Compare.AllowedOnNextScan())
            throw new ArgumentException($"'{query.Compare.ShortName()}' is for a first scan only.");

        lock (_lock)
        {
            ThrowIfBusy();
            var previous = _history.Current
                ?? throw new ScanStateException("There are no scan results yet. Run a first scan.");
            if (query.Type != previous.Type)
                throw new ArgumentException($"The previous scan was {previous.Type.ShortName()}, not {query.Type.ShortName()}: " +
                                            "start a new scan to change the type.");
            return Start(target, new Run("next", query, ScanMethod.Local, previous.Pid),
                (engine, o, ct) => engine.NextScan(previous, query, o, ct),
                new ScanRunOptions { PauseGame = options.PauseGame });
        }
    }

    /// <summary>Asks the running scan to stop (it stops at the next chunk). Fine when nothing runs.</summary>
    public ScanStatus Cancel()
    {
        lock (_lock)
        {
            if (_run?.State == ScanState.Running && _cancel is { IsCancellationRequested: false } cancel)
            {
                _log.Info(Cat, $"Cancel requested for the {_run.Kind} scan");
                cancel.Cancel();
            }
            return StatusLocked();
        }
    }

    /// <summary>New scan: cancels a running scan, waits for it to stop, then drops every result.</summary>
    public async Task<ScanStatus> NewAsync()
    {
        Task? task;
        lock (_lock)
        {
            _cancel?.Cancel();
            task = _task;
        }
        if (task is not null) await task;

        lock (_lock)
        {
            ThrowIfBusy(); // another scan started while we waited
            _history.Clear();
            _run = null;
            _log.Info(Cat, "New scan: results cleared");
            return StatusLocked();
        }
    }

    /// <summary>Goes back to the results before the last next scan.</summary>
    /// <exception cref="ScanStateException">A scan is running, or there's nothing to undo.</exception>
    public ScanStatus Undo()
    {
        lock (_lock)
        {
            ThrowIfBusy();
            if (!_history.Undo())
                throw new ScanStateException("Nothing to undo.");
            _run = null;
            _log.Info(Cat, $"Undo: back to scan #{_history.Current!.ScanNumber}, {_history.Current.Count} results");
            return StatusLocked();
        }
    }

    /// <summary>
    /// The connection is going away (disconnect or lost): cancels the scan and drops every result.
    /// A running scan's results are dropped when it stops. Doesn't wait.
    /// </summary>
    public void Release()
    {
        lock (_lock)
        {
            _generation++;
            if (_run?.State == ScanState.Running)
            {
                _log.Info(Cat, "Disconnect: cancelling the running scan; its results will be dropped");
                _cancel?.Cancel();
                // Resume now: after this the connection is gone and the scan thread can't.
                _run.Pause?.Resume();
            }
            else
            {
                if (_history.Current is not null)
                    _log.Info(Cat, "Disconnect: scan results dropped");
                _history.Clear();
                _run = null;
            }
        }
    }

    public void Dispose()
    {
        Task? task;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            _cancel?.Cancel();
            _run?.Pause?.Resume();
            task = _task;
        }
        // Scans stop at the next chunk; a console scan piece can take a while, so wait generously.
        task?.Wait(TimeSpan.FromSeconds(30));
        lock (_lock)
        {
            _history.Dispose();
            _storage?.Dispose();
            _storage = null;
        }
    }

    // ---- Running a scan ----------------------------------------------------------------------

    private delegate ScanResults ScanCall(ScanEngine engine, ScanOptions options, CancellationToken cancel);

    /// <summary>The running or last scan. Progress is written by the scan thread, read by status.</summary>
    private sealed class Run(string kind, ScanQuery query, ScanMethod method, int pid)
    {
        public string Kind { get; } = kind;
        public ScanQuery Query { get; } = query;
        public ScanMethod Method { get; } = method;
        public int Pid { get; } = pid;
        public Stopwatch Watch { get; } = Stopwatch.StartNew();
        public volatile ScanProgress? Progress;
        public volatile string? Note;
        public volatile GamePause? Pause;
        public ScanState State = ScanState.Running;
        public string? Message;
        public TimeSpan? Elapsed;
    }

    /// <summary>Call with <see cref="_lock"/> held.</summary>
    private ScanStatus Start(ScanTarget target, Run run, ScanCall scan, ScanRunOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _log = target.Log;
        _storage ??= new ScanStorage(target.Settings.ScanDataDirectory, target.Log);
        var storage = _storage;
        var cancel = new CancellationTokenSource();
        var generation = _generation;
        _run = run;
        _cancel = cancel;
        _log.Info(Cat, $"Starting {run.Kind} scan: pid {run.Pid}, {run.Query}, method {run.Method.Name()}" +
                       (options.PauseGame ? ", game paused during the scan" : ""));
        // The task can't finish before this lock is released, since Finish takes it.
        _task = Task.Factory.StartNew(() => Execute(target, run, scan, options, storage, generation, cancel.Token),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return StatusLocked();
    }

    /// <summary>The scan thread. Never throws: the outcome goes into the run's state.</summary>
    private void Execute(ScanTarget target, Run run, ScanCall scan, ScanRunOptions options, ScanStorage storage,
        long generation, CancellationToken cancel)
    {
        ScanResults? results = null;
        Exception? error = null;
        try
        {
            ISectionScanner? console = null;
            if (run.Method == ScanMethod.Console)
            {
                var (scanner, reason) = target.ConsoleScanner?.Invoke() ?? (null, "this connection has no console scanner");
                console = scanner;
                if (scanner is null)
                {
                    run.Note = $"Console scan isn't available ({reason}), so this scan runs on the Mac.";
                    target.Log.Warn(Cat, run.Note);
                }
            }

            if (options.PauseGame)
                PauseGame(target, run, generation);

            var engine = new ScanEngine(target.Memory, target.Settings, target.Log, console, storage);
            results = scan(engine, new ScanOptions
            {
                Method = console is null ? ScanMethod.Local : ScanMethod.Console,
                Alignment = options.Alignment,
                Progress = p => run.Progress = p,
            }, cancel);
        }
        catch (Exception e)
        {
            error = e;
        }
        finally
        {
            run.Pause?.Resume();
        }
        Finish(target.Log, run, results, error, generation);
    }

    /// <summary>
    /// Stops the game. Under the session lock, so <see cref="Release"/> either runs first (and the
    /// game is never stopped) or sees the pause and resumes it while the connection is still open.
    /// </summary>
    private void PauseGame(ScanTarget target, Run run, long generation)
    {
        lock (_lock)
        {
            if (generation != _generation) throw new OperationCanceledException();
            var control = target.Control
                ?? throw new InvalidOperationException("This connection can't pause the game.");
            try
            {
                control.StopProcess(run.Pid);
            }
            catch (Ps4ProtocolException e)
            {
                throw new Ps4ProtocolException(
                    $"Couldn't pause the game ({e.Message}). Pausing needs ps4debug-NG; scan without \"pause game\".", e);
            }
            run.Pause = new GamePause(control, run.Pid, target.Log);
            target.Log.Info(Cat, $"Game paused for the scan (pid {run.Pid})");
        }
    }

    /// <summary>A stopped game. <see cref="Resume"/> runs once, whoever calls it first.</summary>
    private sealed class GamePause(IProcessControl control, int pid, DebugLog log)
    {
        private int _resumed;

        public bool Resumed => Volatile.Read(ref _resumed) == 1;

        /// <summary>Why the resume failed, for the status message.</summary>
        public string? Error { get; private set; }

        public void Resume()
        {
            if (Interlocked.Exchange(ref _resumed, 1) == 1) return;
            try
            {
                control.ResumeProcess(pid);
                log.Info(Cat, $"Game resumed (pid {pid})");
            }
            catch (Exception e)
            {
                Error = $"The game couldn't be resumed ({e.Message}). Reconnect and resume it with " +
                        $"POST /api/processes/{pid}/resume, or restart the game.";
                log.Error(Cat, $"Resuming pid {pid} failed: the game may still be paused", e);
            }
        }
    }

    private void Finish(DebugLog log, Run run, ScanResults? results, Exception? error, long generation)
    {
        lock (_lock)
        {
            run.Elapsed = run.Watch.Elapsed;
            var seconds = run.Elapsed.Value.TotalSeconds;
            if (generation != _generation)
            {
                results?.Dispose();
                _history.Clear();
                run.State = ScanState.Cancelled;
                run.Message = "Disconnected, so the scan stopped and the results were dropped.";
                log.Info(Cat, $"The {run.Kind} scan stopped after the disconnect ({seconds:0.0} s); results dropped");
            }
            else if (results is not null)
            {
                _history.Push(results);
                run.State = ScanState.Done;
                log.Info(Cat, $"{Capital(run.Kind)} scan #{results.ScanNumber} done in {seconds:0.0} s: {results.Count} results " +
                              $"in {results.Sections.Count(s => s.Hits > 0)}/{results.Sections.Count} sections");
            }
            else if (error is OperationCanceledException)
            {
                run.State = ScanState.Cancelled;
                run.Message = _history.Current is null ? "Cancelled." : "Cancelled. The previous results are kept.";
                log.Info(Cat, $"The {run.Kind} scan was cancelled after {seconds:0.0} s");
            }
            else
            {
                run.State = ScanState.Failed;
                run.Message = error?.Message ?? "The scan failed.";
                log.Error(Cat, $"The {run.Kind} scan failed after {seconds:0.0} s", error);
            }
            _cancel?.Dispose();
            _cancel = null;
        }
    }

    private static string Capital(string s) => char.ToUpperInvariant(s[0]) + s[1..];

    private void ThrowIfBusy()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_run?.State == ScanState.Running)
            throw new ScanStateException("A scan is running. Wait for it to finish or cancel it first.");
    }

    private ScanStatus StatusLocked()
    {
        var run = _run;
        var current = _history.Current;
        var progress = run?.Progress;
        var running = run?.State == ScanState.Running;
        var percent = run?.State == ScanState.Done ? 100 : Math.Round((progress?.Fraction ?? 0) * 100, 1);
        var message = string.Join(" ", new[] { run?.Note, run?.Message, run?.Pause?.Error }.Where(m => !string.IsNullOrEmpty(m)));
        return new ScanStatus
        {
            State = run?.State ?? ScanState.Idle,
            Kind = run?.Kind,
            Query = run?.Query.ToString(),
            Method = run?.Method.Name(),
            Percent = percent,
            Section = progress?.Section,
            Done = progress?.Done ?? 0,
            Total = progress?.Total ?? 0,
            Hits = progress?.Hits ?? 0,
            ElapsedSeconds = Math.Round((run?.Elapsed ?? run?.Watch.Elapsed ?? TimeSpan.Zero).TotalSeconds, 1),
            Message = message.Length > 0 ? message : null,
            GamePaused = run?.Pause is { Resumed: false },
            Pid = current?.Pid,
            ScanNumber = current?.ScanNumber ?? 0,
            ResultCount = current?.Count ?? 0,
            Type = current?.Type,
            ValueSize = current?.ValueSize ?? 0,
            ResultMethod = current?.Method,
            CanUndo = _history.CanUndo,
            Sections = running ? null : current?.Sections,
        };
    }
}
