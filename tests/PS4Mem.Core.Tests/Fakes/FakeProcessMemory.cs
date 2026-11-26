using System.Buffers.Binary;
using PS4Mem.Core.Protocol;

namespace PS4Mem.Core.Tests.Fakes;

/// <summary>
/// Sparse fake process memory. Unknown addresses read as zeros, like ps4debug (protocol.md §8).
/// Records every read and write so tests can check batching and that nothing is written by accident.
/// </summary>
public sealed class FakeProcessMemory : IProcessMemory
{
    private readonly Dictionary<ulong, byte> _bytes = new();

    public List<(ulong Address, int Length)> Reads { get; } = [];
    public List<(ulong Address, byte[] Data)> Writes { get; } = [];

    /// <summary>Called before each read; tests use it to cancel mid-scan.</summary>
    public Action<ulong>? OnRead { get; set; }

    public void SetU32(ulong address, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        SetBytes(address, b);
    }

    public void SetBytes(ulong address, ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
            _bytes[address + (ulong)i] = data[i];
    }

    public uint GetU32(ulong address)
    {
        Span<byte> b = stackalloc byte[4];
        for (var i = 0; i < 4; i++)
            b[i] = _bytes.GetValueOrDefault(address + (ulong)i);
        return BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    public void ReadMemory(int pid, ulong address, Memory<byte> destination, CancellationToken cancel = default)
    {
        OnRead?.Invoke(address);
        Reads.Add((address, destination.Length));
        var span = destination.Span;
        for (var i = 0; i < span.Length; i++)
            span[i] = _bytes.GetValueOrDefault(address + (ulong)i);
    }

    public void WriteMemory(int pid, ulong address, ReadOnlySpan<byte> data)
    {
        Writes.Add((address, data.ToArray()));
        SetBytes(address, data);
    }
}
