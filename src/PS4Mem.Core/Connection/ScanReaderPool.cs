using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;

namespace PS4Mem.Core.Connection;

/// <summary>
/// Extra ps4debug connections that scans read memory with, in parallel
/// (<see cref="PS4MemSettings.ScanReadConnections"/>). ps4debug runs one thread per client and
/// allows 8 (docs/research/scan-speed.md), and PS4Cheater scans with 3 connections the same way.
/// <list type="bullet">
/// <item>Opened on the first scan, not at connect, and kept for the connection. Dead ones are
/// dropped and opened again on the next <see cref="Acquire"/>.</item>
/// <item>A connection that can't be opened (server full, network) is logged and skipped; with none
/// at all, scans read on the main connection, as before.</item>
/// <item>Only reads go through these. A new connection is checked with CMD_VERSION, because a full
/// server accepts the TCP connect and then closes it. Closing one sends CMD_CONSOLE_END, which
/// frees only that client's slot: it never attached the debugger.</item>
/// </list>
/// Used by one scan at a time (the scan session runs one scan at a time).
/// </summary>
public sealed class ScanReaderPool : IDisposable
{
    private const string Cat = "connection";

    private readonly Func<IReadableConnection> _open;
    private readonly int _size;
    private readonly DebugLog _log;
    private readonly TimeSpan _openTimeout;
    private readonly Lock _gate = new();
    private readonly List<IReadableConnection> _connections = [];
    private bool _disposed;

    /// <param name="open">Makes a new, not yet connected, client for the console.</param>
    /// <param name="openTimeout">
    /// How long a new connection may take to connect and answer CMD_VERSION; one that doesn't is
    /// closed and skipped, so a server that accepts but never answers can't hold up the scan.
    /// </param>
    public ScanReaderPool(Func<IReadableConnection> open, int size, DebugLog log, TimeSpan openTimeout)
    {
        _open = open;
        _size = Math.Max(0, size);
        _log = log;
        _openTimeout = openTimeout;
    }

    /// <summary>
    /// The connections to read with: up to the pool size, opening missing ones (in parallel; the
    /// server accepts one about every 250 ms). Empty when none could be opened or the size is 0.
    /// </summary>
    public IReadOnlyList<IProcessMemory> Acquire(CancellationToken cancel = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var dead = _connections.RemoveAll(c =>
            {
                if (c.IsConnected) return false;
                c.Dispose();
                return true;
            });
            if (dead > 0) _log.Warn(Cat, $"{dead} scan read connection(s) were lost; opening new ones");

            var missing = _size - _connections.Count;
            if (missing > 0)
            {
                var opened = Enumerable.Range(0, missing)
                    .Select(_ => Task.Run(() => TryOpen(cancel), CancellationToken.None))
                    .ToArray();
                Task.WaitAll(opened, CancellationToken.None);
                foreach (var t in opened)
                    if (t.Result is { } c) _connections.Add(c);
                _log.Info(Cat, $"Scan read connections: {_connections.Count} of {_size} open" +
                               (_connections.Count < _size ? " (the rest couldn't be opened; scans use what's open)" : ""));
            }
            return _connections.ToList();
        }
    }

    private IReadableConnection? TryOpen(CancellationToken cancel)
    {
        if (cancel.IsCancellationRequested) return null;
        var client = _open();
        var probe = Task.Run(() =>
        {
            client.Connect();
            client.GetVersion(); // a full server closes the socket here
        }, CancellationToken.None);
        try
        {
            if (probe.Wait(_openTimeout, CancellationToken.None))
                return client;
            _log.Warn(Cat, $"A scan read connection didn't answer within {_openTimeout.TotalSeconds:0.#} s; skipping it");
        }
        catch (AggregateException e)
        {
            _log.Warn(Cat, $"Couldn't open a scan read connection: {e.InnerException?.Message ?? e.Message}");
        }
        // Dispose would wait for the probe's command; Abort ends it, then the probe cleans up.
        client.Abort();
        probe.ContinueWith(t =>
        {
            _ = t.Exception; // the aborted command's error is expected
            client.Dispose();
        }, TaskScheduler.Default);
        return null;
    }

    /// <summary>Closes every pool connection (disconnect).</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_connections.Count > 0)
                _log.Info(Cat, $"Closing {_connections.Count} scan read connection(s)");
            foreach (var c in _connections)
            {
                try { c.Dispose(); }
                catch (Exception e) { _log.Warn(Cat, $"Closing a scan read connection failed: {e.Message}"); }
            }
            _connections.Clear();
        }
    }
}

/// <summary>What the pool needs from a connection; <see cref="PS4DebugClient"/> implements it.</summary>
public interface IReadableConnection : IProcessMemory, IDisposable
{
    bool IsConnected { get; }
    void Connect();
    string GetVersion();

    /// <summary>Closes the socket without waiting for a command in progress.</summary>
    void Abort();
}
