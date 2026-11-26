using PS4Mem.Core;

namespace PS4Mem.Core.Tests;

public class PS4MemSettingsTests
{
    [Fact]
    public void Defaults_match_the_spec()
    {
        var s = PS4MemSettings.Default;
        Assert.Equal("", s.ConsoleIp); // no built-in IP: it comes from ConsoleIpResolver
        Assert.Equal(744, s.CommandPort);
        Assert.Equal(755, s.DebuggerPort);
        Assert.Equal(9090, s.PayloadPort);
        Assert.Contains("SmallBlockAllocator", s.SectionNameFilter);
    }

    [Fact]
    public void Defaults_can_be_overridden()
    {
        var s = PS4MemSettings.Default with { ConsoleIp = "192.0.2.10", CommandPort = 7440 };
        Assert.Equal("192.0.2.10", s.ConsoleIp);
        Assert.Equal(7440, s.CommandPort);
        Assert.Equal(755, s.DebuggerPort);
    }
}
