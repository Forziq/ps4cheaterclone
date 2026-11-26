using System.Runtime.InteropServices;

namespace PS4Mem.Core.Scanning;

/// <summary>
/// Write side of a result set, used while a scan runs: an address array and a value array with
/// <see cref="ValueSize"/> bytes per hit. With a <see cref="ScanStorage"/>, once it holds more
/// than <c>spillRows</c> hits it writes them to two files and from then on keeps only a small
/// tail in memory, so millions of hits don't fill the Mac's memory (PROMPT §5 "spill to disk").
/// Without a storage everything stays in memory.
/// <see cref="Complete"/> hands the rows to <see cref="ScanResults"/>; disposing an unfinished
/// buffer (a cancelled scan) deletes its files.
/// </summary>
public sealed class ScanHitBuffer : IDisposable
{
    /// <summary>Hits kept in memory between file writes once the buffer has spilled.</summary>
    private const int TailRows = 64 * 1024;

    private readonly ScanStorage? _storage;
    private readonly long _spillRows;
    private ulong[] _addresses;
    private byte[] _values;
    private int _memCount;
    private FileStream? _addressFile, _valueFile;
    private bool _completed;

    public ScanHitBuffer(int valueSize, int capacity = 1024, ScanStorage? storage = null, long spillRows = 2_000_000)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(valueSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(spillRows, 1);
        ValueSize = valueSize;
        _storage = storage;
        _spillRows = spillRows;
        if (storage is not null) capacity = (int)Math.Min(capacity, spillRows);
        _addresses = new ulong[Math.Max(16, capacity)];
        _values = new byte[checked(_addresses.Length * valueSize)];
    }

    public int ValueSize { get; }

    public long Count => SpilledCount + _memCount;

    /// <summary>Hits already written to disk: the first <see cref="SpilledCount"/> rows.</summary>
    public long SpilledCount { get; private set; }

    public bool Spilled => _addressFile is not null;

    /// <summary>Adds a hit; <paramref name="value"/> must be <see cref="ValueSize"/> bytes.</summary>
    public void Add(ulong address, ReadOnlySpan<byte> value)
    {
        if (value.Length != ValueSize)
            throw new ArgumentException($"Value is {value.Length} bytes, expected {ValueSize}.", nameof(value));
        ObjectDisposedException.ThrowIf(_completed, this);
        if (_memCount == _addresses.Length) MakeRoom();
        _addresses[_memCount] = address;
        value.CopyTo(_values.AsSpan(_memCount * ValueSize));
        _memCount++;
    }

    /// <summary>Address of a hit still in memory (<c>index &gt;= SpilledCount</c>).</summary>
    public ulong AddressAt(long index) => _addresses[InMemory(index)];

    /// <summary>Value of a hit still in memory (<c>index &gt;= SpilledCount</c>).</summary>
    public ReadOnlySpan<byte> ValueAt(long index) => _values.AsSpan(InMemory(index) * ValueSize, ValueSize);

    private int InMemory(long index)
    {
        if (index < SpilledCount || index >= Count)
            throw new ArgumentOutOfRangeException(nameof(index), $"Hit {index} isn't in memory (in memory: {SpilledCount} to {Count - 1}).");
        return (int)(index - SpilledCount);
    }

    /// <summary>
    /// Sorts hits <c>[start, Count)</c> by address (for back ends that may report out of order).
    /// Only hits still in memory can be sorted.
    /// </summary>
    public void SortFrom(long start)
    {
        if (start < SpilledCount)
            throw new InvalidOperationException($"Can't sort hits from {start}: hits below {SpilledCount} are already on disk.");
        var from = (int)(start - SpilledCount);
        var sorted = true;
        for (var i = from + 1; i < _memCount && sorted; i++)
            sorted = _addresses[i] >= _addresses[i - 1];
        if (sorted) return;

        var n = _memCount - from;
        var keys = _addresses[from.._memCount];
        var order = Enumerable.Range(0, n).ToArray();
        Array.Sort(keys, order);
        var oldValues = _values.AsSpan(from * ValueSize, n * ValueSize).ToArray();
        for (var i = 0; i < n; i++)
            oldValues.AsSpan(order[i] * ValueSize, ValueSize).CopyTo(_values.AsSpan((from + i) * ValueSize));
        keys.CopyTo(_addresses, from);
    }

    private void MakeRoom()
    {
        if (Spilled)
        {
            Flush();
            return;
        }
        if (_storage is not null && _memCount >= _spillRows)
        {
            StartSpill(_storage);
            return;
        }
        var size = checked(_addresses.Length * 2);
        if (_storage is not null) size = (int)Math.Min(size, _spillRows);
        Array.Resize(ref _addresses, size);
        Array.Resize(ref _values, checked(size * ValueSize));
    }

    private void StartSpill(ScanStorage storage)
    {
        _addressFile = Create(storage.NewFile("addresses"));
        try
        {
            _valueFile = Create(storage.NewFile("values"));
        }
        catch
        {
            DeleteFiles();
            throw;
        }
        Flush();
        if (_addresses.Length > TailRows)
        {
            _addresses = new ulong[TailRows];
            _values = new byte[TailRows * ValueSize];
        }
    }

    private static FileStream Create(string path) =>
        new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 20);

    private void Flush()
    {
        _addressFile!.Write(MemoryMarshal.AsBytes(_addresses.AsSpan(0, _memCount)));
        _valueFile!.Write(_values.AsSpan(0, _memCount * ValueSize));
        SpilledCount += _memCount;
        _memCount = 0;
    }

    /// <summary>Finishes writing and returns the rows. The buffer can't be used afterwards.</summary>
    internal HitColumns Complete()
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        _completed = true;
        if (!Spilled)
            return new MemoryHitColumns(_addresses, _values, _memCount, ValueSize);

        Flush();
        var addressPath = _addressFile!.Name;
        var valuePath = _valueFile!.Name;
        _addressFile.Dispose();
        _valueFile!.Dispose();
        _addressFile = _valueFile = null;
        try
        {
            return new FileHitColumns(_storage!, addressPath, valuePath, SpilledCount, ValueSize);
        }
        catch
        {
            _storage!.Delete(addressPath);
            _storage.Delete(valuePath);
            throw;
        }
    }

    /// <summary>Drops an unfinished buffer and deletes its files. Does nothing after <see cref="Complete"/>.</summary>
    public void Dispose()
    {
        if (_completed) return;
        _completed = true;
        DeleteFiles();
    }

    private void DeleteFiles()
    {
        foreach (var file in new[] { _addressFile, _valueFile })
        {
            if (file is null) continue;
            var path = file.Name;
            file.Dispose();
            _storage!.Delete(path);
        }
        _addressFile = _valueFile = null;
    }
}
