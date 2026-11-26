using PS4Mem.Core;

namespace PS4Mem.Core.Tests;

public class PS4MemSettingsTests
{
    [Fact]
    public void Defaults_match_the_spec()
    {
        var s = PS4MemSettings.Default;
        Assert.Equal("192.168.0.99", s.ConsoleIp);
        Assert.Equal(744, s.CommandPort);
        Assert.Equal(755, s.DebuggerPort);
        Assert.Equal(9090, s.PayloadPort);
        Assert.Contains("SmallBlockAllocator", s.SectionNameFilter);
    }

    [Fact]
    public void Defaults_can_be_overridden()
    {
        var s = PS4MemSettings.Default with { ConsoleIp = "10.0.0.5", CommandPort = 7440 };
        Assert.Equal("10.0.0.5", s.ConsoleIp);
        Assert.Equal(7440, s.CommandPort);
        Assert.Equal(755, s.DebuggerPort);
    }
}
