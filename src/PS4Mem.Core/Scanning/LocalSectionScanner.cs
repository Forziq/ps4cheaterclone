using System.Buffers;
using System.Buffers.Binary;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Sections;

namespace PS4Mem.Core.Scanning;

/// <summary>
/// LocalScan, the default and reference back end: reads the section with CMD_PROC_READ in
/// chunks of <see cref="PS4MemSettings.ScanChunkBytes"/> and compares on the Mac. This is what
/// PS4Cheater does (protocol.md §10, "local scan over CMD_PROC_READ is the proven path").
///
/// Each chunk is read with 3 extra bytes (clamped to the section end), so a value that starts in
/// one chunk and ends in the next is still found. Nothing past the section end is read.
/// </summary>
public sealed class LocalSectionScanner(IProcessMemory memory, PS4MemSettings? settings = null) : ISectionScanner
{
    private const int ValueSize = 4;
    private readonly int _chunkBytes = Math.Max(ValueSize, (settings ?? PS4MemSettings.Default).ScanChunkBytes);

    public string Name => "local";

    public void ScanSection(int pid, Section section, uint value, int alignment, ScanHitBuffer hits,
        Action<long> bytesDone, CancellationToken cancel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(alignment, 1);
        Span<byte> needle = stackalloc byte[ValueSize];
        BinaryPrimitives.WriteUInt32LittleEndian(needle, value); // the PS4 is little-endian

        var buffer = ArrayPool<byte>.Shared.Rent(_chunkBytes + ValueSize - 1);
        try
        {
            for (var pos = section.Start; pos < section.End;)
            {
                cancel.ThrowIfCancellationRequested();
                var left = section.End - pos;
                var own = (int)Math.Min((ulong)_chunkBytes, left);               // bytes this chunk owns
                var read = (int)Math.Min((ulong)own + ValueSize - 1, left);      // plus the overlap
                memory.ReadMemory(pid, pos, buffer.AsMemory(0, read), cancel);

                FindInChunk(buffer.AsSpan(0, read), own, pos, needle, value, alignment, hits);
                pos += (ulong)own;
                bytesDone(own);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Adds matches that start in the first <paramref name="own"/> bytes of <paramref name="data"/>.</summary>
    private static void FindInChunk(ReadOnlySpan<byte> data, int own, ulong baseAddress,
        ReadOnlySpan<byte> needle, uint value, int alignment, ScanHitBuffer hits)
    {
        var from = 0;
        while (from < own)
        {
            var found = data[from..].IndexOf(needle); // vectorised search
            if (found < 0) return;
            var offset = from + found;
            if (offset >= own) return; // starts in the next chunk; that chunk will report it
            var address = baseAddress + (ulong)offset;
            if (address % (ulong)alignment == 0)
                hits.Add(address, value);
            from = offset + 1;
        }
    }
}
