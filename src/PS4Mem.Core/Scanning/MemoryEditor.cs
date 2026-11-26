using System.Buffers.Binary;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;

namespace PS4Mem.Core.Scanning;

/// <summary>
/// Writes values the user asked for (CLAUDE.md §7: never write unless asked). Every write is logged
/// with pid, address, old and new bytes (category <c>write</c>), whether it worked or not.
/// </summary>
public sealed class MemoryEditor(IProcessMemory memory, DebugLog? log = null)
{
    private const string Cat = "write";
    private readonly DebugLog _log = log ?? DebugLog.Disabled;

    /// <summary>Reads the old u32 at <paramref name="address"/>, writes <paramref name="value"/> (little-endian) and returns the old value.</summary>
    public uint WriteUInt32(int pid, ulong address, uint value)
    {
        var old = new byte[4];
        memory.ReadMemory(pid, address, old);
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);

        var what = $"pid {pid} 0x{address:X}: old {Convert.ToHexString(old)} new {Convert.ToHexString(bytes)}";
        try
        {
            memory.WriteMemory(pid, address, bytes);
        }
        catch (Exception e)
        {
            _log.Error(Cat, $"{what} failed", e);
            throw;
        }
        _log.Info(Cat, $"{what} ok");
        return BinaryPrimitives.ReadUInt32LittleEndian(old);
    }
}
