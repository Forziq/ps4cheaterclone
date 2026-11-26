using System.Buffers;
using System.Runtime.ExceptionServices;
using PS4Mem.Core.Protocol;

namespace PS4Mem.Core.Scanning;

/// <summary>One read of a scan: <see cref="Length"/> bytes at <see cref="Address"/>.</summary>
public readonly record struct ReadRange(ulong Address, int Length);

/// <summary>
/// Reads a list of ranges with several connections at once and hands the data to the caller
/// in list order, on the caller's thread. PS4Cheater and PS4CheaterNeo scan with 3 connections:
/// ps4debug runs one thread per client and sends each read in 64 KB steps, so one connection
/// waits on round trips (docs/research/scan-speed.md).
/// <list type="bullet">
/// <item>Each reader (connection) has its own thread and is only used by it.</item>
/// <item>Ranges are taken from <c>items</c> one at a time, in order, under a lock, so the
/// enumerator needs no thread safety.</item>
/// <item>The consumer gets every range in order, so results are identical to reading serially.
/// Readers run at most <c>2 × readers</c> ranges ahead of the consumer (memory stays bounded).</item>
/// <item>A failed read stops every reader and is rethrown; nothing is replaced with zeros.</item>
/// </list>
/// With one reader it simply reads and consumes in a loop.
/// </summary>
public static class ParallelReader
{
    public delegate void Consumer<T>(T item, ReadOnlySpan<byte> data);

    public static void Run<T>(IReadOnlyList<IProcessMemory> readers, int pid, IEnumerable<T> items,
        Func<T, ReadRange> rangeOf, Consumer<T> consume, CancellationToken cancel = default)
    {
        if (readers.Count == 0)
            throw new ArgumentException("At least one reader is needed.", nameof(readers));
        if (readers.Count == 1)
        {
            RunSerial(readers[0], pid, items, rangeOf, consume, cancel);
            return;
        }
        new Pipeline<T>(readers, pid, items, rangeOf, consume, cancel).Run();
    }

    private static void RunSerial<T>(IProcessMemory reader, int pid, IEnumerable<T> items,
        Func<T, ReadRange> rangeOf, Consumer<T> consume, CancellationToken cancel)
    {
        var buffer = Array.Empty<byte>();
        try
        {
            foreach (var item in items)
            {
                cancel.ThrowIfCancellationRequested();
                var range = rangeOf(item);
                if (buffer.Length < range.Length)
                {
                    if (buffer.Length > 0) ArrayPool<byte>.Shared.Return(buffer);
                    buffer = ArrayPool<byte>.Shared.Rent(range.Length);
                }
                reader.ReadMemory(pid, range.Address, buffer.AsMemory(0, range.Length), cancel);
                consume(item, buffer.AsSpan(0, range.Length));
            }
        }
        finally
        {
            if (buffer.Length > 0) ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed class Pipeline<T>(IReadOnlyList<IProcessMemory> readers, int pid, IEnumerable<T> items,
        Func<T, ReadRange> rangeOf, Consumer<T> consume, CancellationToken cancel)
    {
        private readonly object _gate = new(); // a plain object: Monitor.Wait/PulseAll don't work on Lock
        private readonly Dictionary<long, (T Item, byte[] Buffer, int Length)> _ready = [];
        private readonly int _maxAhead = readers.Count * 2;
        private IEnumerator<T>? _items;
        private long _nextIndex;      // next range to hand to a reader
        private long _nextToConsume;  // next range the consumer needs
        private bool _noMoreItems;
        private int _readersLeft = readers.Count;
        private Exception? _error;
        private CancellationToken _stop;

        public void Run()
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            _stop = stop.Token;
            using var wake = _stop.Register(WakeAll);
            using var enumerator = items.GetEnumerator();
            _items = enumerator;

            var threads = readers.Select((reader, i) => new Thread(() => ReadLoop(reader))
            {
                IsBackground = true,
                Name = $"scan-read-{i}",
            }).ToList();
            foreach (var t in threads) t.Start();

            Exception? consumerError = null;
            try
            {
                ConsumeLoop();
            }
            catch (Exception e)
            {
                consumerError = e;
            }
            finally
            {
                stop.Cancel(); // a no-op when everything was read; stops the readers otherwise
                foreach (var t in threads) t.Join();
                foreach (var slot in _ready.Values) ArrayPool<byte>.Shared.Return(slot.Buffer);
                _ready.Clear();
            }

            if (consumerError is not null) ExceptionDispatchInfo.Capture(consumerError).Throw();
            cancel.ThrowIfCancellationRequested();
            if (_error is not null) ExceptionDispatchInfo.Capture(_error).Throw();
        }

        private void WakeAll()
        {
            lock (_gate) Monitor.PulseAll(_gate);
        }

        private void ConsumeLoop()
        {
            while (true)
            {
                (T Item, byte[] Buffer, int Length) slot;
                lock (_gate)
                {
                    while (!_ready.Remove(_nextToConsume, out slot))
                    {
                        // Readers only stop after storing what they read, so nothing is left behind.
                        if (_error is not null || _stop.IsCancellationRequested || _readersLeft == 0) return;
                        Monitor.Wait(_gate);
                    }
                }
                try
                {
                    consume(slot.Item, slot.Buffer.AsSpan(0, slot.Length));
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(slot.Buffer);
                }
                lock (_gate)
                {
                    _nextToConsume++;
                    Monitor.PulseAll(_gate);
                }
            }
        }

        private void ReadLoop(IProcessMemory reader)
        {
            try
            {
                while (true)
                {
                    T item;
                    long index;
                    lock (_gate)
                    {
                        while (!_noMoreItems && _error is null && !_stop.IsCancellationRequested &&
                               _nextIndex - _nextToConsume >= _maxAhead)
                            Monitor.Wait(_gate);
                        if (_noMoreItems || _error is not null || _stop.IsCancellationRequested) return;
                        if (!_items!.MoveNext())
                        {
                            _noMoreItems = true;
                            return;
                        }
                        item = _items.Current;
                        index = _nextIndex++;
                    }

                    var range = rangeOf(item);
                    var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(1, range.Length));
                    try
                    {
                        reader.ReadMemory(pid, range.Address, buffer.AsMemory(0, range.Length), _stop);
                    }
                    catch
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        throw;
                    }
                    lock (_gate)
                    {
                        _ready[index] = (item, buffer, range.Length);
                        Monitor.PulseAll(_gate);
                    }
                }
            }
            catch (Exception e)
            {
                lock (_gate)
                {
                    // A reader stopped by our own cancel isn't the cause.
                    if (_error is null && !(e is OperationCanceledException && _stop.IsCancellationRequested))
                        _error = e;
                    Monitor.PulseAll(_gate);
                }
            }
            finally
            {
                lock (_gate)
                {
                    _readersLeft--;
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }
}
