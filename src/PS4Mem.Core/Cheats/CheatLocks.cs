using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Scanning;

namespace PS4Mem.Core.Cheats;

/// <summary>A locked cheat, or the last error of a lock that stopped by itself.</summary>
/// <param name="Locked">False when the lock stopped because a write failed (see <paramref name="Error"/>).</param>
/// <param name="Writes">Writes so far, the first one included.</param>
public sealed record CheatLockState(
    string TitleId, string CheatId, bool Locked, int Pid, ulong Address, DateTimeOffset Since, long Writes, string? Error);

/// <summary>
/// Locked cheats (PROMPT §5: re-write about every 200 ms while locked). A lock starts only when
/// the user clicks it, writes once at once (so a bad address fails there and then), and then a
/// background loop writes every locked cheat again each <see cref="PS4MemSettings.CheatLockInterval"/>.
/// A write that fails stops that lock and keeps the error to show. <see cref="StopAll"/> (on
/// disconnect or a lost connection) stops every lock; after it returns no new write starts.
/// </summary>
public sealed class CheatLocks : IDisposable
{
    private const string Cat = "cheats";

    private readonly TimeSpan _interval;
    private readonly DebugLogSwitch _logs;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string TitleId, string Id), Entry> _locks = new();
    private readonly Dictionary<(string TitleId, string Id), CheatLockState> _failed = new();
    private Task? _loop;
    private CancellationTokenSource? _stop;

    private sealed class Entry(string titleId, Cheat cheat, int pid, MemoryEditor editor)
    {
        public string TitleId { get; } = titleId;
        public int Pid { get; } = pid;
        public MemoryEditor Editor { get; } = editor;
        public DateTimeOffset Since { get; } = DateTimeOffset.Now;
        public Cheat Cheat { get; set; } = cheat;
        public byte[] Bytes { get; set; } = cheat.ValueBytes();
        public long Writes;

        public CheatLockState State(string? error = null) =>
            new(TitleId, Cheat.Id, error is null, Pid, Cheat.Address, Since, Interlocked.Read(ref Writes), error);
    }

    public CheatLocks(PS4MemSettings settings, DebugLogSwitch? logs = null)
    {
        _interval = settings.CheatLockInterval > TimeSpan.Zero ? settings.CheatLockInterval : TimeSpan.FromMilliseconds(200);
        _logs = logs ?? new DebugLogSwitch();
    }

    private DebugLog Log => _logs.Current;

    /// <summary>
    /// Locks <paramref name="cheat"/> in process <paramref name="pid"/>: writes it now, then keeps
    /// writing it. Locking a cheat that is already locked re-locks it (new pid, value written now).
    /// </summary>
    /// <exception cref="Exception">The first write failed (the cheat is not locked then).</exception>
    public CheatLockState Lock(string titleId, Cheat cheat, int pid, IProcessMemory memory)
    {
        var editor = new MemoryEditor(memory, Log);
        var entry = new Entry(titleId, cheat, pid, editor);
        editor.Write(pid, cheat.Address, entry.Bytes, $"lock {titleId} {cheat.Id}");
        entry.Writes = 1;

        lock (_gate)
        {
            _locks[(titleId, cheat.Id)] = entry;
            _failed.Remove((titleId, cheat.Id));
            if (_loop is null)
            {
                _stop = new CancellationTokenSource();
                var token = _stop.Token;
                _loop = Task.Run(() => RunAsync(token));
            }
        }
        Log.Info(Cat, $"{titleId}: locked {CheatList.Describe(cheat)} in pid {pid}, every {_interval.TotalMilliseconds:0} ms");
        return entry.State();
    }

    /// <summary>Stops a lock. False when it wasn't locked. Clears a stopped lock's error too.</summary>
    public bool Unlock(string titleId, string id)
    {
        Entry? entry;
        lock (_gate)
        {
            _failed.Remove((titleId, id));
            if (!_locks.Remove((titleId, id), out entry)) return false;
        }
        Log.Info(Cat, $"{titleId}: unlocked {id} after {Interlocked.Read(ref entry.Writes)} writes");
        return true;
    }

    /// <summary>After an edit: a locked cheat goes on writing the new value at the new address.</summary>
    public void Update(string titleId, Cheat cheat)
    {
        lock (_gate)
        {
            if (!_locks.TryGetValue((titleId, cheat.Id), out var entry)) return;
            entry.Bytes = cheat.ValueBytes();
            entry.Cheat = cheat;
        }
        Log.Info(Cat, $"{titleId}: lock {cheat.Id} now writes {CheatList.Describe(cheat)}");
    }

    /// <summary>The lock of one cheat, a stopped lock's error, or null.</summary>
    public CheatLockState? Find(string titleId, string id)
    {
        lock (_gate)
        {
            if (_locks.TryGetValue((titleId, id), out var entry)) return entry.State();
            return _failed.GetValueOrDefault((titleId, id));
        }
    }

    /// <summary>Every running lock.</summary>
    public IReadOnlyList<CheatLockState> Active
    {
        get { lock (_gate) return _locks.Values.Select(e => e.State()).ToList(); }
    }

    /// <summary>
    /// Stops every lock and forgets stopped locks' errors (disconnect, lost connection, shutdown).
    /// Doesn't wait for a write that is already on its way; no other write starts after this returns.
    /// </summary>
    public void StopAll(string reason)
    {
        List<Entry> stopped;
        lock (_gate)
        {
            stopped = _locks.Values.ToList();
            _locks.Clear();
            _failed.Clear();
            _stop?.Cancel();
            _stop = null;
            _loop = null;
        }
        foreach (var e in stopped)
            Log.Info(Cat, $"{e.TitleId}: lock {e.Cheat.Id} stopped ({reason}) after {Interlocked.Read(ref e.Writes)} writes");
    }

    public void Dispose() => StopAll("shutting down");

    private async Task RunAsync(CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(token))
            {
                List<Entry> due;
                lock (_gate)
                {
                    if (token.IsCancellationRequested) return;
                    if (_locks.Count == 0)
                    {
                        // Nothing left to write: end the loop; the next Lock starts a new one.
                        _stop?.Dispose();
                        _stop = null;
                        _loop = null;
                        return;
                    }
                    due = _locks.Values.ToList();
                }
                foreach (var entry in due)
                    WriteAgain(entry, token);
            }
        }
        catch (OperationCanceledException)
        {
            // StopAll.
        }
        catch (Exception e)
        {
            Log.Error(Cat, "The lock loop failed", e);
        }
    }

    private void WriteAgain(Entry entry, CancellationToken token)
    {
        byte[] bytes;
        ulong address;
        lock (_gate)
        {
            // Unlocked, re-locked or stopped since this pass began: skip it.
            if (token.IsCancellationRequested || !_locks.TryGetValue((entry.TitleId, entry.Cheat.Id), out var current)
                || !ReferenceEquals(current, entry))
                return;
            bytes = entry.Bytes;
            address = entry.Cheat.Address;
        }
        try
        {
            entry.Editor.Rewrite(entry.Pid, address, bytes);
            Interlocked.Increment(ref entry.Writes);
        }
        catch (Exception e)
        {
            var error = $"The lock stopped: writing {HexAddress.Format(address)} in pid {entry.Pid} failed ({e.Message}).";
            lock (_gate)
            {
                if (_locks.TryGetValue((entry.TitleId, entry.Cheat.Id), out var current) && ReferenceEquals(current, entry))
                {
                    _locks.Remove((entry.TitleId, entry.Cheat.Id));
                    _failed[(entry.TitleId, entry.Cheat.Id)] = entry.State(error);
                }
            }
            Log.Warn(Cat, $"{entry.TitleId}: {entry.Cheat.Id}: {error}");
        }
    }
}
