using PS4Mem.Core.Logging;
using PS4Mem.Core.Scanning;
using PS4Mem.Core.Tests.Fakes;

namespace PS4Mem.Core.Tests;

public class MemoryEditorTests
{
    [Fact]
    public void Write_stores_little_endian_returns_old_value_and_logs_old_and_new_bytes()
    {
        var mem = new FakeProcessMemory();
        mem.SetU32(0x1000, 500);
        var writer = new StringWriter();
        using var log = new DebugLog(writer);

        var old = new MemoryEditor(mem, log).WriteUInt32(42, 0x1000, 999_999);

        Assert.Equal(500u, old);
        Assert.Equal(999_999u, mem.GetU32(0x1000));
        var write = Assert.Single(mem.Writes);
        Assert.Equal(0x1000UL, write.Address);
        Assert.Equal(new byte[] { 0x3F, 0x42, 0x0F, 0x00 }, write.Data);
        Assert.Contains("[write] pid 42 0x1000: old F4010000 new 3F420F00 ok", writer.ToString());
    }

    [Fact]
    public void Failed_write_is_logged_and_rethrown()
    {
        var writer = new StringWriter();
        using var log = new DebugLog(writer);
        var editor = new MemoryEditor(new ThrowingMemory(), log);

        Assert.Throws<IOException>(() => editor.WriteUInt32(1, 0x20, 7));
        Assert.Contains("pid 1 0x20: old 00000000 new 07000000 failed", writer.ToString());
    }

    private sealed class ThrowingMemory : PS4Mem.Core.Protocol.IProcessMemory
    {
        public void ReadMemory(int pid, ulong address, Memory<byte> destination, CancellationToken cancel = default) =>
            destination.Span.Clear();

        public void WriteMemory(int pid, ulong address, ReadOnlySpan<byte> data) => throw new IOException("gone");
    }
}
