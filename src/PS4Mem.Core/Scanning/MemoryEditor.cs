using System.Buffers.Binary;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;

namespace PS4Mem.Core.Scanning;

/// <summary>
/// Writes values the user asked for (CLAUDE.md §7: never write unless asked). Every write is logged
/// with pid, address, old and new bytes (category <c>write</c>), whether it worked or not.
/// The one exception is a lock's repeat writes (<see cref="Rewrite"/>): a lock writes about five
/// times a second, so only its first write is logged in full, and the lock logs how many followed.
/// </summary>
public sealed class MemoryEditor(IProcessMemory memory, DebugLog? log = null)
{
    private const string Cat = "write";
    private readonly DebugLog _log = log ?? DebugLog.Disabled;

    /// <summary>Reads the old u32 at <paramref name="address"/>, writes <paramref name="value"/> (little-endian) and returns the old value.</summary>
    public uint WriteUInt32(int pid, ulong address, uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return BinaryPrimitives.ReadUInt32LittleEndian(Write(pid, address, bytes));
    }

    /// <summary>Reads the bytes at <paramref name="address"/>, writes <paramref name="data"/> over them and returns the old bytes.</summary>
    public byte[] Write(int pid, ulong address, ReadOnlySpan<byte> data, string? why = null)
    {
        var old = new byte[data.Length];
        memory.ReadMemory(pid, address, old);

        var what = $"pid {pid} 0x{address:X}: old {Convert.ToHexString(old)} new {Convert.ToHexString(data)}";
        if (why is not null) what += $" ({why})";
        try
        {
            memory.WriteMemory(pid, address, data);
        }
        catch (Exception e)
        {
            _log.Error(Cat, $"{what} failed", e);
            throw;
        }
        _log.Info(Cat, $"{what} ok");
        return old;
    }

    /// <summary>
    /// A locked cheat's repeat write: no read first and no log line (the lock logs its first write
    /// with <see cref="Write"/> and a count when it stops). Failures are still logged.
    /// </summary>
    public void Rewrite(int pid, ulong address, ReadOnlySpan<byte> data)
    {
        try
        {
            memory.WriteMemory(pid, address, data);
        }
        catch (Exception e)
        {
            _log.Error(Cat, $"pid {pid} 0x{address:X}: lock write {Convert.ToHexString(data)} failed", e);
            throw;
        }
    }
}
