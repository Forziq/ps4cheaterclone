namespace PS4Mem.Core.Protocol;

/// <summary>
/// Read and write access to a process's memory. <see cref="PS4DebugClient"/> implements it over
/// CMD_PROC_READ / CMD_PROC_WRITE; tests use fake memory.
/// </summary>
public interface IProcessMemory
{
    /// <summary>Fills <paramref name="destination"/> from <paramref name="address"/>. Unreadable memory reads as zeros.</summary>
    void ReadMemory(int pid, ulong address, Memory<byte> destination, CancellationToken cancel = default);

    /// <summary>Writes <paramref name="data"/> at <paramref name="address"/>. Only call this when the user asked for a write.</summary>
    void WriteMemory(int pid, ulong address, ReadOnlySpan<byte> data);
}

/// <summary>
/// Pauses and resumes a process (the "pause the game during scans" option). <see cref="PS4DebugClient"/>
/// implements it with CMD_DEBUG_PROCESS_STOP; tests use a fake.
/// </summary>
public interface IProcessControl
{
    /// <summary>Stops every thread of the process until <see cref="ResumeProcess"/>.</summary>
    void StopProcess(int pid);

    void ResumeProcess(int pid);
}
