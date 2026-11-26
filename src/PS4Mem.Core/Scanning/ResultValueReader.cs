using PS4Mem.Core.Protocol;

namespace PS4Mem.Core.Scanning;

/// <summary>
/// Reads the current values at a list of addresses (the visible result rows) with as few
/// CMD_PROC_READ calls as possible: sorted addresses at most <c>maxGap</c> bytes apart share
/// one read of at most <c>maxBatch</c> bytes, as in a next scan.
/// </summary>
public static class ResultValueReader
{
    /// <param name="addresses">In ascending order (scan results are).</param>
    /// <returns>One <paramref name="size"/>-byte value per address, in the same order.</returns>
    public static byte[][] Read(IProcessMemory memory, int pid, IReadOnlyList<ulong> addresses, int size,
        int maxGap, int maxBatch, CancellationToken cancel = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        var values = new byte[addresses.Count][];
        var gap = (ulong)Math.Max(0, maxGap);
        var batch = (ulong)Math.Max(size, maxBatch);
        var buffer = Array.Empty<byte>();

        for (var i = 0; i < addresses.Count;)
        {
            cancel.ThrowIfCancellationRequested();
            var start = addresses[i];
            var end = start + (ulong)size;
            var j = i + 1;
            while (j < addresses.Count)
            {
                var next = addresses[j];
                if (next < start) throw new ArgumentException("Addresses must be in ascending order.");
                if ((next > end ? next - end : 0) > gap || next + (ulong)size - start > batch) break;
                end = Math.Max(end, next + (ulong)size);
                j++;
            }

            var length = (int)(end - start);
            if (buffer.Length < length) buffer = new byte[length];
            memory.ReadMemory(pid, start, buffer.AsMemory(0, length), cancel);
            for (var k = i; k < j; k++)
                values[k] = buffer.AsSpan((int)(addresses[k] - start), size).ToArray();
            i = j;
        }
        return values;
    }
}
