using System.IO.MemoryMappedFiles;

namespace PS4Mem.Core.Scanning;

/// <summary>
/// Read side of a result set: an address column and a value column (<see cref="ValueSize"/>
/// bytes per row), in memory or in files. <see cref="ScanResults"/> wraps one.
/// </summary>
internal abstract class HitColumns : IDisposable
{
    protected HitColumns(long count, int valueSize)
    {
        Count = count;
        ValueSize = valueSize;
    }

    public long Count { get; }
    public int ValueSize { get; }

    /// <summary>True when the rows are in files, not in memory.</summary>
    public abstract bool OnDisk { get; }

    public abstract ulong AddressAt(long index);
    public abstract ReadOnlySpan<byte> ValueAt(long index);

    public virtual void Dispose() { }
}

internal sealed class MemoryHitColumns : HitColumns
{
    private readonly ulong[] _addresses;
    private readonly byte[] _values;

    /// <summary>Uses the first <paramref name="count"/> rows of the arrays (no copy).</summary>
    public MemoryHitColumns(ulong[] addresses, byte[] values, long count, int valueSize) : base(count, valueSize)
    {
        _addresses = addresses;
        _values = values;
    }

    public override bool OnDisk => false;

    public override ulong AddressAt(long index) => _addresses[index];

    public override ReadOnlySpan<byte> ValueAt(long index) =>
        _values.AsSpan(checked((int)(index * ValueSize)), ValueSize);
}

/// <summary>
/// Rows in two files written by <see cref="ScanHitBuffer"/>: u64 addresses and raw values.
/// Read through memory maps, so the OS pages in only what's used. Deletes the files on dispose.
/// </summary>
internal sealed unsafe class FileHitColumns : HitColumns
{
    private readonly ScanStorage _storage;
    private readonly string _addressFile, _valueFile;
    private readonly MappedFile _addresses, _values;
    private bool _disposed;

    public FileHitColumns(ScanStorage storage, string addressFile, string valueFile, long count, int valueSize)
        : base(count, valueSize)
    {
        _storage = storage;
        _addressFile = addressFile;
        _valueFile = valueFile;
        _addresses = new MappedFile(addressFile);
        try
        {
            _values = new MappedFile(valueFile);
        }
        catch
        {
            _addresses.Dispose();
            throw;
        }
    }

    public override bool OnDisk => true;

    public override ulong AddressAt(long index)
    {
        Check(index);
        return ((ulong*)_addresses.Pointer)[index];
    }

    public override ReadOnlySpan<byte> ValueAt(long index)
    {
        Check(index);
        return new ReadOnlySpan<byte>(_values.Pointer + index * ValueSize, ValueSize);
    }

    private void Check(long index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((ulong)index >= (ulong)Count) throw new ArgumentOutOfRangeException(nameof(index));
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _addresses.Dispose();
        _values.Dispose();
        _storage.Delete(_addressFile);
        _storage.Delete(_valueFile);
    }
}

/// <summary>A read-only memory map of a whole file and a pointer to its first byte.</summary>
internal sealed unsafe class MappedFile : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;

    public MappedFile(string path)
    {
        _file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _view = _file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        byte* p = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        Pointer = p + _view.PointerOffset;
    }

    public byte* Pointer { get; }

    public void Dispose()
    {
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
