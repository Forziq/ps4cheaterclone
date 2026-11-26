namespace PS4Mem.Core.Scanning;

/// <summary>
/// The current scan results and the ones "undo last scan" goes back to. Keeps at most
/// <see cref="UndoLevels"/> older result sets (<see cref="PS4MemSettings.ScanUndoLevels"/>);
/// older ones are disposed, which deletes their files. <see cref="Clear"/> (new scan) and
/// <see cref="Dispose"/> (disconnect, exit) dispose everything.
/// </summary>
public sealed class ScanHistory(int undoLevels = 1) : IDisposable
{
    private readonly LinkedList<ScanResults> _undo = new(); // newest first
    private readonly object _lock = new();

    public int UndoLevels { get; } = undoLevels >= 0 ? undoLevels
        : throw new ArgumentOutOfRangeException(nameof(undoLevels), "Undo levels can't be negative.");

    public ScanResults? Current { get; private set; }

    /// <summary>How many scans <see cref="Undo"/> can still go back.</summary>
    public int UndoCount
    {
        get { lock (_lock) return _undo.Count; }
    }

    public bool CanUndo => UndoCount > 0;

    /// <summary>
    /// Makes <paramref name="results"/> current. A first scan (scan number 1) replaces everything;
    /// a next scan keeps the current results for undo.
    /// </summary>
    public void Push(ScanResults results)
    {
        lock (_lock)
        {
            if (ReferenceEquals(results, Current)) return;
            if (results.ScanNumber == 1)
            {
                ClearLocked();
            }
            else if (Current is { } current)
            {
                _undo.AddFirst(current);
                while (_undo.Count > UndoLevels)
                {
                    _undo.Last!.Value.Dispose();
                    _undo.RemoveLast();
                }
            }
            Current = results;
        }
    }

    /// <summary>Drops the current results and goes back to the previous ones. False if there are none.</summary>
    public bool Undo()
    {
        lock (_lock)
        {
            if (_undo.First is not { } previous) return false;
            _undo.RemoveFirst();
            Current?.Dispose();
            Current = previous.Value;
            return true;
        }
    }

    /// <summary>New scan: disposes the current and every kept result set.</summary>
    public void Clear()
    {
        lock (_lock) ClearLocked();
    }

    private void ClearLocked()
    {
        Current?.Dispose();
        Current = null;
        foreach (var r in _undo) r.Dispose();
        _undo.Clear();
    }

    public void Dispose() => Clear();
}
