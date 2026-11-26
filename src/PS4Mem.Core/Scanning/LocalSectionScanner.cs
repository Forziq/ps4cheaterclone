using System.Buffers;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Sections;

namespace PS4Mem.Core.Scanning;

/// <summary>
/// LocalScan, the default and reference back end: reads the section with CMD_PROC_READ in
/// chunks of <see cref="PS4MemSettings.ScanChunkBytes"/> and compares on the Mac with a
/// <see cref="ValueMatcher"/>. This is what PS4Cheater does (protocol.md §10, "local scan over
/// CMD_PROC_READ is the proven path"). Supports every value type and first-scan compare.
///
/// Each chunk is read with (value size - 1) extra bytes (clamped to the section end), so a value
/// that starts in one chunk and ends in the next is still found. Nothing past the section end is read.
/// </summary>
public sealed class LocalSectionScanner(IProcessMemory memory, PS4MemSettings? settings = null) : ISectionScanner
{
    private readonly PS4MemSettings _settings = settings ?? PS4MemSettings.Default;

    public string Name => "local";

    public bool Supports(ScanQuery query) => query.Compare.AllowedOnFirstScan();

    public void ScanSection(int pid, Section section, ScanQuery query, int alignment, ScanHitBuffer hits,
        Action<long> bytesDone, CancellationToken cancel)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(alignment, 1);
        var matcher = ValueMatcher.Create(query, hits.ValueSize, _settings.UnknownInitialSkipZeros);
        var size = matcher.Size;
        var chunkBytes = Math.Max(size, _settings.ScanChunkBytes);

        var buffer = ArrayPool<byte>.Shared.Rent(chunkBytes + size - 1);
        try
        {
            for (var pos = section.Start; pos < section.End;)
            {
                cancel.ThrowIfCancellationRequested();
                var left = section.End - pos;
                var own = (int)Math.Min((ulong)chunkBytes, left);                // bytes this chunk owns
                var read = (int)Math.Min((ulong)own + (ulong)size - 1, left);    // plus the overlap
                memory.ReadMemory(pid, pos, buffer.AsMemory(0, read), cancel);

                matcher.FindAll(buffer.AsSpan(0, read), own, pos, alignment, hits);
                pos += (ulong)own;
                bytesDone(own);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
