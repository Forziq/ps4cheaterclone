using System.Buffers.Binary;
using System.Collections.Concurrent;
using PS4Mem.Core.Cheats;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;

namespace PS4Mem.Core.Tests;

/// <summary>Writing cheats and the lock loop (T1.6 m2).</summary>
public sealed class CheatLockTests : IDisposable
{
    private const string Title = "CUSA11711";
    private const int Pid = 85;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly string _dir = Directory.CreateTempSubdirectory("ps4mem-locks-").FullName;
    private readonly StringWriter _logText = new();
    private readonly DebugLogSwitch _logs;
    private readonly CheatLocks _locks;
    private readonly CheatList _list;
    private readonly SharedMemory _memory = new();

    public CheatLockTests()
    {
        _logs = new DebugLogSwitch(new DebugLog(TextWriter.Synchronized(_logText)));
        _locks = new CheatLocks(PS4MemSettings.Default with { CheatLockInterval = TimeSpan.FromMilliseconds(10) }, _logs);
        _list = new CheatList(new CheatStore(Path.Combine(_dir, "cheats")), _locks, _logs);
    }

    public void Dispose()
    {
        _locks.Dispose();
        _logs.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private Cheat AddMoney(string value = "999") =>
        _list.Add(Title, new CheatInput("Money", "0x2001A3F10", "u32", value));

    [Fact]
    public void Write_puts_the_value_in_memory_once_and_returns_the_old_value()
    {
        _memory.SetU32(0x2001A3F10, 500);
        var cheat = AddMoney();

        var write = _list.Write(Title, cheat.Id, Pid, _memory);

        Assert.Equal("500", write.OldValue);
        Assert.Equal(999u, _memory.GetU32(0x2001A3F10));
        Assert.Single(_memory.Writes);
        Assert.Null(write.Lock);
        Assert.Contains("[write] pid 85 0x2001A3F10: old F4010000 new E7030000 (cheat CUSA11711 " + cheat.Id + ") ok", _logText.ToString());
    }

    [Fact]
    public void Write_with_a_value_saves_it_first()
    {
        var cheat = AddMoney();

        var write = _list.Write(Title.ToLowerInvariant(), cheat.Id, Pid, _memory, "0x10", hex: false);

        Assert.Equal("16", write.Cheat.Value);
        Assert.Equal(16u, _memory.GetU32(0x2001A3F10));
        Assert.Equal("16", _list.Get(Title).Find(cheat.Id)!.Value);
    }

    [Fact]
    public void Nothing_is_written_without_a_write_or_lock_call()
    {
        var cheat = AddMoney();
        _list.Update(Title, cheat.Id, new CheatInput(Value: "5"));
        _list.ReadValues(Title, Pid, _memory);
        _list.Unlock(Title, cheat.Id);
        _list.Delete(Title, cheat.Id);

        Assert.Empty(_memory.Writes);
    }

    [Fact]
    public void ReadValues_reads_each_cheat_without_writing()
    {
        _memory.SetU32(0x2001A3F10, 1234);
        _memory.SetBytes(0x3000, [0xDE, 0xAD]);
        var money = AddMoney();
        var bytes = _list.Add(Title, new CheatInput("Raw", "0x3000", "bytes", "00 00"));

        var values = _list.ReadValues(Title, Pid, _memory);

        Assert.Equal([new CheatValue(money.Id, "1234"), new CheatValue(bytes.Id, "DE AD")], values);
        Assert.Empty(_memory.Writes);
    }

    [Fact]
    public void A_lock_writes_at_once_then_again_until_unlocked()
    {
        var cheat = AddMoney();

        var state = _list.Lock(Title, cheat.Id, Pid, _memory);
        Assert.True(state.Locked);
        Assert.Equal(1, state.Writes);
        Assert.Equal(999u, _memory.GetU32(0x2001A3F10));

        _memory.SetU32(0x2001A3F10, 1); // the game spends the money
        Assert.True(SpinWait.SpinUntil(() => _memory.Writes.Count >= 4, Wait));
        Assert.Equal(999u, _memory.GetU32(0x2001A3F10));
        Assert.True(_list.LockOf(Title, cheat.Id)!.Writes >= 4);

        Assert.True(_list.Unlock(Title, cheat.Id));
        Assert.Null(_list.LockOf(Title, cheat.Id));
        Assert.False(_list.Unlock(Title, cheat.Id));
        AssertNoMoreWrites();

        var log = _logText.ToString();
        Assert.Single(log.Split('\n'), l => l.Contains("[write]")); // only the first write is logged in full
        Assert.Contains($"locked {cheat.Id}", log);
        Assert.Contains($"unlocked {cheat.Id} after", log);
    }

    [Fact]
    public void An_edit_changes_what_a_lock_writes_and_a_delete_stops_it()
    {
        var cheat = AddMoney();
        _list.Lock(Title, cheat.Id, Pid, _memory);

        _list.Update(Title, cheat.Id, new CheatInput(Value: "7", Address: "0x5000"));
        Assert.True(SpinWait.SpinUntil(() => _memory.GetU32(0x5000) == 7, Wait));

        _list.Write(Title, cheat.Id, Pid, _memory, "8");
        Assert.True(SpinWait.SpinUntil(() => _memory.LastWrite is (0x5000, [8, 0, 0, 0]), Wait));
        Assert.True(_list.LockOf(Title, cheat.Id)!.Locked);

        _list.Delete(Title, cheat.Id);
        Assert.Empty(_locks.Active);
        AssertNoMoreWrites();
    }

    [Fact]
    public void StopAll_stops_every_lock()
    {
        var a = AddMoney();
        var b = _list.Add(Title, new CheatInput("Ammo", "0x4000", "u16", "99"));
        _list.Lock(Title, a.Id, Pid, _memory);
        _list.Lock(Title, b.Id, Pid, _memory);
        Assert.Equal(2, _locks.Active.Count);
        Assert.True(SpinWait.SpinUntil(() => _memory.Writes.Count >= 6, Wait));

        _locks.StopAll("disconnected");

        Assert.Empty(_locks.Active);
        AssertNoMoreWrites();
        Assert.Contains("stopped (disconnected)", _logText.ToString());

        // A new lock after StopAll starts a new loop.
        _list.Lock(Title, a.Id, Pid, _memory);
        var count = _memory.Writes.Count;
        Assert.True(SpinWait.SpinUntil(() => _memory.Writes.Count >= count + 2, Wait));
    }

    [Fact]
    public void A_failed_first_write_does_not_lock()
    {
        var cheat = AddMoney();
        _memory.FailWrites = true;

        Assert.Throws<IOException>(() => _list.Lock(Title, cheat.Id, Pid, _memory));
        Assert.Null(_list.LockOf(Title, cheat.Id));
        Assert.Empty(_locks.Active);
    }

    [Fact]
    public void A_failed_repeat_write_stops_the_lock_and_keeps_the_error()
    {
        var cheat = AddMoney();
        _list.Lock(Title, cheat.Id, Pid, _memory);
        _memory.FailWrites = true;

        Assert.True(SpinWait.SpinUntil(() => _list.LockOf(Title, cheat.Id) is { Locked: false }, Wait));
        var state = _list.LockOf(Title, cheat.Id)!;
        Assert.Contains("The lock stopped", state.Error);
        Assert.Contains("gone", state.Error);
        Assert.Empty(_locks.Active);

        // Unlock clears the error; a new lock works again.
        _memory.FailWrites = false;
        _list.Unlock(Title, cheat.Id);
        Assert.Null(_list.LockOf(Title, cheat.Id));
        Assert.True(_list.Lock(Title, cheat.Id, Pid, _memory).Locked);
    }

    [Fact]
    public void Locks_are_per_title_id()
    {
        var a = AddMoney();
        _list.Lock(Title, a.Id, Pid, _memory);

        Assert.Null(_locks.Find("CUSA00001", a.Id));
        Assert.Throws<CheatNotFoundException>(() => _list.Lock("CUSA00001", a.Id, Pid, _memory));
    }

    private void AssertNoMoreWrites()
    {
        Thread.Sleep(30); // a write already on its way when the lock stopped may still land
        var count = _memory.Writes.Count;
        Thread.Sleep(100);
        Assert.Equal(count, _memory.Writes.Count);
    }

    /// <summary>Fake memory that the lock loop's thread and the test can use at once.</summary>
    private sealed class SharedMemory : IProcessMemory
    {
        private readonly ConcurrentDictionary<ulong, byte> _bytes = new();

        public ConcurrentQueue<(ulong Address, byte[] Data)> Writes { get; } = new();
        public (ulong Address, byte[] Data)? LastWrite { get; private set; }
        public volatile bool FailWrites;

        public void SetU32(ulong address, uint value)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, value);
            SetBytes(address, b);
        }

        public void SetBytes(ulong address, ReadOnlySpan<byte> data)
        {
            for (var i = 0; i < data.Length; i++) _bytes[address + (ulong)i] = data[i];
        }

        public uint GetU32(ulong address)
        {
            var b = new byte[4];
            for (var i = 0; i < 4; i++) b[i] = _bytes.GetValueOrDefault(address + (ulong)i);
            return BinaryPrimitives.ReadUInt32LittleEndian(b);
        }

        public void ReadMemory(int pid, ulong address, Memory<byte> destination, CancellationToken cancel = default)
        {
            var span = destination.Span;
            for (var i = 0; i < span.Length; i++) span[i] = _bytes.GetValueOrDefault(address + (ulong)i);
        }

        public void WriteMemory(int pid, ulong address, ReadOnlySpan<byte> data)
        {
            if (FailWrites) throw new IOException("gone");
            SetBytes(address, data);
            var entry = (address, data.ToArray());
            Writes.Enqueue(entry);
            LastWrite = entry;
        }
    }
}
