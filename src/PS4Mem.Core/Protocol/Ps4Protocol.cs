namespace PS4Mem.Core.Protocol;

/// <summary>
/// Wire constants for ps4debug(-NG). Every value is from docs/protocol.md, which cites the source.
/// LD = ref/libdebug/PS4DBG/, NG = ref/ps4debug-NG/debugger/.
/// </summary>
public static class Ps4Protocol
{
    /// <summary>Request header magic (NG/include/protocol.h:15, LD/PS4DBG.cs:27). protocol.md §1.</summary>
    public const uint PacketMagic = 0xFFAABBCC;

    /// <summary>magic + cmd + datalen, all u32 LE (NG/include/protocol.h:96-102). protocol.md §1.</summary>
    public const int HeaderSize = 12;

    /// <summary>Process list entry: char[32] name + i32 pid (NG/include/kdbg.h:12-15). protocol.md §5.</summary>
    public const int ProcListEntrySize = 36;

    /// <summary>Map entry: char[32] name, u64 start, u64 end, u64 offset, u16 prot (NG/include/proc.h:11-17). protocol.md §7.</summary>
    public const int ProcMapEntrySize = 58;

    /// <summary>Process info struct: u32 pid, char[40] name, char[64] path, char[16] titleid, char[64] contentid. protocol.md §6.</summary>
    public const int ProcInfoSize = 188;

    /// <summary>CMD_PROC_READ / CMD_PROC_WRITE body: u32 pid, u64 address, u32 length. protocol.md §8, §9.</summary>
    public const int ProcReadWriteBodySize = 16;

    /// <summary>Prot bits (ps4-ksdk/include/ksdk_bsd.h:14-16). protocol.md §7.</summary>
    public const ushort ProtRead = 0x1, ProtWrite = 0x2, ProtExec = 0x4;
}

/// <summary>Command IDs (NG/include/protocol.h). Only the ones PS4 Mem sends so far.</summary>
public enum Ps4Command : uint
{
    /// <summary>protocol.md §4 (NG/include/protocol.h:17).</summary>
    Version = 0xBD000001,
    /// <summary>protocol.md §2 (NG/include/protocol.h:76).</summary>
    ConsoleEnd = 0xBDDD0002,
    /// <summary>protocol.md §5 (NG/include/protocol.h:22).</summary>
    ProcList = 0xBDAA0001,
    /// <summary>protocol.md §8 (NG/include/protocol.h:23).</summary>
    ProcRead = 0xBDAA0002,
    /// <summary>protocol.md §9 (NG/include/protocol.h:25).</summary>
    ProcWrite = 0xBDAA0003,
    /// <summary>protocol.md §7 (NG/include/protocol.h:26).</summary>
    ProcMaps = 0xBDAA0004,
    /// <summary>protocol.md §6 (NG/include/protocol.h:32).</summary>
    ProcInfo = 0xBDAA000A,
}

/// <summary>Reply status words (NG/include/protocol.h:87-94, LD/PS4DBG.Enums.cs:5-12). protocol.md §3.</summary>
public enum Ps4Status : uint
{
    Success = 0x80000000,
    Error = 0xF0000001,
    TooMuchData = 0xF0000002,
    DataNull = 0xF0000003,
    AlreadyDebug = 0xF0000004,
    InvalidIndex = 0xF0000005,
}

/// <summary>Something went wrong talking to ps4debug (bad reply, closed connection ...).</summary>
public class Ps4ProtocolException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The console answered a command with a status other than CMD_SUCCESS.</summary>
public sealed class Ps4StatusException(Ps4Command command, Ps4Status status, bool connectionClosed)
    : Ps4ProtocolException(
        $"{command} failed: console returned {Describe(status)}" +
        (connectionClosed ? " and closed the connection." : "."))
{
    public Ps4Command Command { get; } = command;
    public Ps4Status Status { get; } = status;

    /// <summary>True when the server drops the connection after this error (protocol.md §2).</summary>
    public bool ConnectionClosed { get; } = connectionClosed;

    public static string Describe(Ps4Status status) =>
        Enum.IsDefined(status) ? $"{status} (0x{(uint)status:X8})" : $"unknown status 0x{(uint)status:X8}";
}
