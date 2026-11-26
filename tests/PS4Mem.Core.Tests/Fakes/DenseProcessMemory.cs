using PS4Mem.Core.Protocol;

namespace PS4Mem.Core.Tests.Fakes;

/// <summary>
/// Fake process memory backed by one byte array at <see cref="Base"/>, for scans over many
/// megabytes (<see cref="FakeProcessMemory"/> is a per-byte dictionary). Other addresses read as zeros.
/// </summary>
public sealed class DenseProcessMemory(ulong baseAddress, int size) : IProcessMemory
{
    public ulong Base { get; } = baseAddress;
    public byte[] Data { get; } = new byte[size];
    public int ReadCount { get; private set; }
    public int WriteCount { get; private set; }

    /// <summary>Called before each read; tests use it to cancel mid-scan or change memory.</summary>
    public Action<ulong>? OnRead { get; set; }

    public void ReadMemory(int pid, ulong address, Memory<byte> destination, CancellationToken cancel = default)
    {
        OnRead?.Invoke(address);
        ReadCount++;
        var span = destination.Span;
        span.Clear();
        var start = Math.Max(address, Base);
        var end = Math.Min(address + (ulong)span.Length, Base + (ulong)Data.Length);
        if (start < end)
            Data.AsSpan((int)(start - Base), (int)(end - start)).CopyTo(span[(int)(start - address)..]);
    }

    public void WriteMemory(int pid, ulong address, ReadOnlySpan<byte> data) =>
        throw new InvalidOperationException($"Unexpected write of {data.Length} bytes at 0x{address:X}.");
}
