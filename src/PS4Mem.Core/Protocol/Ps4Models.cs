namespace PS4Mem.Core.Protocol;

/// <summary>One entry of CMD_PROC_LIST (protocol.md §5).</summary>
public sealed record ProcessEntry(string Name, int Pid);

/// <summary>Reply of CMD_PROC_INFO (protocol.md §6). TitleId is empty when the console doesn't know it.</summary>
public sealed record ProcessInfo(int Pid, string Name, string Path, string TitleId, string ContentId);

/// <summary>One entry of CMD_PROC_MAPS (protocol.md §7).</summary>
public sealed record MemoryMap(string Name, ulong Start, ulong End, ulong Offset, ushort Prot)
{
    public ulong Size => End > Start ? End - Start : 0;
    public bool IsReadable => (Prot & Ps4Protocol.ProtRead) != 0;
    public bool IsWritable => (Prot & Ps4Protocol.ProtWrite) != 0;
    public bool IsExecutable => (Prot & Ps4Protocol.ProtExec) != 0;

    public string ProtText =>
        $"{(IsReadable ? 'r' : '-')}{(IsWritable ? 'w' : '-')}{(IsExecutable ? 'x' : '-')}";
}
