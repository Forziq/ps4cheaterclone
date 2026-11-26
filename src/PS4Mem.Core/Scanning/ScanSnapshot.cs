using PS4Mem.Core.Sections;

namespace PS4Mem.Core.Scanning;

/// <summary>
/// The raw bytes of every scanned section, kept by an unknown initial value scan. With a
/// <see cref="ScanStorage"/> they're one file read through a memory map; without one, an array.
/// </summary>
internal abstract class SnapshotData : IDisposable
{
    public abstract ReadOnlySpan<byte> Slice(long offset, int length);
    public virtual bool OnDisk => false;
    public virtual void Dispose() { }
}

internal sealed class MemorySnapshotData(byte[] bytes) : SnapshotData
{
    public override ReadOnlySpan<byte> Slice(long offset, int length) => bytes.AsSpan(checked((int)offset), length);
}

internal sealed unsafe class FileSnapshotData(ScanStorage storage, string path, long length) : SnapshotData
{
    private readonly MappedFile _map = new(path);
    private bool _disposed;

    public override bool OnDisk => true;

    public override ReadOnlySpan<byte> Slice(long offset, int size)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0 || size < 0 || offset + size > length) throw new ArgumentOutOfRangeException(nameof(offset));
        return new ReadOnlySpan<byte>(_map.Pointer + offset, size);
    }

    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _map.Dispose();
        storage.Delete(path);
    }
}

/// <summary>Where one section's bytes are in the snapshot and which results are its.</summary>
internal sealed record SnapshotSection(Section Section, long DataOffset, int FirstBlock, int BlockCount);

/// <summary>
/// Unknown initial value results: every aligned value in the snapshot that's a candidate (all
/// values, or the non-zero ones with <see cref="PS4MemSettings.UnknownInitialSkipZeros"/>).
/// Rows aren't stored one by one (that would be 12 bytes per 4 bytes of memory); a result's
/// address is found from a per-block count of candidates (<see cref="BlockBytes"/> of memory per
/// block), and reading results in order is fast. The value of a result is its snapshot bytes.
/// </summary>
internal sealed class SnapshotHitColumns : HitColumns
{
    public const int BlockShift = 16;
    public const int BlockBytes = 1 << BlockShift;

    private readonly long[] _blockFirst;     // global index of each block's first result
    private readonly int[] _blockSection;    // section index of each block
    private readonly object _lock = new();
    private (int Block, long Index, long Offset) _cursor = (-1, 0, 0);  // last result found: block, index, section offset

    public SnapshotHitColumns(SnapshotData data, IReadOnlyList<SnapshotSection> sections, long[] blockCounts,
        int alignment, ValueMatcher candidate)
        : base(blockCounts.Sum(), candidate.Size)
    {
        Data = data;
        Sections = sections;
        Alignment = alignment;
        Candidate = candidate;
        _blockFirst = new long[blockCounts.Length];
        _blockSection = new int[blockCounts.Length];
        long sum = 0;
        for (var b = 0; b < blockCounts.Length; b++)
        {
            _blockFirst[b] = sum;
            sum += blockCounts[b];
        }
        for (var s = 0; s < sections.Count; s++)
            Array.Fill(_blockSection, s, sections[s].FirstBlock, sections[s].BlockCount);
    }

    public SnapshotData Data { get; }
    public IReadOnlyList<SnapshotSection> Sections { get; }
    public int Alignment { get; }

    /// <summary>Unknown initial matcher: true for a value that's a result.</summary>
    public ValueMatcher Candidate { get; }

    public override bool OnDisk => Data.OnDisk;

    public override ulong AddressAt(long index)
    {
        var (section, offset) = Locate(index);
        return Sections[section].Section.Start + (ulong)offset;
    }

    public override ReadOnlySpan<byte> ValueAt(long index)
    {
        var (section, offset) = Locate(index);
        return Data.Slice(Sections[section].DataOffset + offset, ValueSize);
    }

    /// <summary>Section index and offset in the section of result <paramref name="index"/>.</summary>
    private (int Section, long Offset) Locate(long index)
    {
        if ((ulong)index >= (ulong)Count) throw new ArgumentOutOfRangeException(nameof(index));
        lock (_lock)
        {
            // Last block whose first result is at or before index (empty blocks share it with the next).
            int lo = 0, hi = _blockFirst.Length - 1;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (_blockFirst[mid] <= index) lo = mid; else hi = mid - 1;
            }
            var block = lo;
            var s = Sections[_blockSection[block]];
            var size = (long)s.Section.Size;
            var blockStart = (long)(block - s.FirstBlock) << BlockShift;
            var blockEnd = Math.Min(blockStart + BlockBytes, size);

            // Walk the block's values, from the cursor when it's earlier in the same block.
            long at, offset;
            if (_cursor.Block == block && _cursor.Index <= index)
                (at, offset) = (_cursor.Index, _cursor.Offset);
            else
                (at, offset) = (_blockFirst[block], blockStart + FirstAligned(s.Section.Start + (ulong)blockStart));
            for (; offset < blockEnd && offset + ValueSize <= size; offset += Alignment)
            {
                if (!Candidate.IsMatch(Data.Slice(s.DataOffset + offset, ValueSize), default)) continue;
                if (at == index)
                {
                    _cursor = (block, index, offset);
                    return (_blockSection[block], offset);
                }
                at++;
            }
            throw new InvalidOperationException($"Snapshot block {block} has fewer results than its count.");
        }
    }

    private long FirstAligned(ulong address) => (long)((ulong)(Alignment - (int)(address % (ulong)Alignment)) % (ulong)Alignment);

    public override void Dispose() => Data.Dispose();
}

/// <summary>
/// Builds a snapshot while the first scan reads each section: writes the bytes (to a file with
/// a storage) and counts the candidates per block. Dispose without <see cref="Complete"/>
/// (a cancelled scan) deletes the file.
/// </summary>
internal sealed class SnapshotBuilder : IDisposable
{
    private readonly ScanStorage? _storage;
    private readonly int _alignment;
    private readonly ValueMatcher _candidate;
    private readonly List<SnapshotSection> _sections = [];
    private readonly List<long> _blockCounts = [];
    private readonly byte[]? _memory;
    private readonly FileStream? _file;
    private long _written;
    private bool _done;

    public SnapshotBuilder(ScanStorage? storage, long totalBytes, int alignment, ValueMatcher candidate)
    {
        _storage = storage;
        _alignment = alignment;
        _candidate = candidate;
        if (storage is null)
        {
            if (totalBytes > Array.MaxLength)
                throw new InvalidOperationException("An unknown initial value scan this big needs scan storage on disk.");
            _memory = new byte[totalBytes];
        }
        else
        {
            _file = new FileStream(storage.NewFile("snapshot"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 20);
        }
    }

    /// <summary>Starts the next section; its bytes must then be added in order with <see cref="AddChunk"/>.</summary>
    public void BeginSection(Section section)
    {
        var blocks = checked((int)((section.Size + SnapshotHitColumns.BlockBytes - 1) >> SnapshotHitColumns.BlockShift));
        _sections.Add(new SnapshotSection(section, _written, _blockCounts.Count, blocks));
        _blockCounts.AddRange(new long[blocks]);
    }

    /// <summary>
    /// Adds <paramref name="own"/> bytes at <paramref name="address"/>. <paramref name="data"/> may hold
    /// up to (value size - 1) more bytes of the section, so a candidate on a chunk edge is counted.
    /// Returns the number of candidates that start in this chunk.
    /// </summary>
    public long AddChunk(ReadOnlySpan<byte> data, int own, ulong address)
    {
        var s = _sections[^1];
        var bytes = data[..own];
        if (_memory is not null) bytes.CopyTo(_memory.AsSpan(checked((int)_written)));
        else _file!.Write(bytes);
        _written += own;

        var size = _candidate.Size;
        var sectionOffset = (long)(address - s.Section.Start);
        var first = (int)((ulong)(_alignment - (int)(address % (ulong)_alignment)) % (ulong)_alignment);
        long found = 0;
        for (var o = first; o < own && o + size <= data.Length; o += _alignment)
        {
            if (!_candidate.IsMatch(data.Slice(o, size), default)) continue;
            _blockCounts[s.FirstBlock + (int)((sectionOffset + o) >> SnapshotHitColumns.BlockShift)]++;
            found++;
        }
        return found;
    }

    public SnapshotHitColumns Complete()
    {
        _done = true;
        SnapshotData data;
        if (_memory is not null)
        {
            data = new MemorySnapshotData(_memory);
        }
        else
        {
            var path = _file!.Name;
            _file.Dispose();
            if (_written == 0)
            {
                _storage!.Delete(path);
                data = new MemorySnapshotData([]);
            }
            else
            {
                data = new FileSnapshotData(_storage!, path, _written);
            }
        }
        return new SnapshotHitColumns(data, _sections, _blockCounts.ToArray(), _alignment, _candidate);
    }

    public void Dispose()
    {
        if (_done || _file is null) return;
        _done = true;
        var path = _file.Name;
        _file.Dispose();
        _storage!.Delete(path);
    }
}
