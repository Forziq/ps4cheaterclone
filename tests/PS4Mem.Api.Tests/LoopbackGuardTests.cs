namespace PS4Mem.Api.Tests;

public class LoopbackGuardTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5080")]
    [InlineData("http://localhost:5080")]
    [InlineData("http://[::1]:5080")]
    public void Loopback_addresses_are_allowed(string url) => Assert.True(LoopbackGuard.IsLoopback(url));

    [Theory]
    [InlineData("http://0.0.0.0:5080")]
    [InlineData("http://*:5080")]
    [InlineData("http://+:5080")]
    [InlineData("http://192.0.2.10:5080")]
    [InlineData("not a url")]
    public void Anything_else_is_refused(string url) => Assert.False(LoopbackGuard.IsLoopback(url));

    [Fact]
    public void Every_address_in_a_list_must_be_loopback()
    {
        LoopbackGuard.EnsureLoopback("http://127.0.0.1:5080;http://localhost:5081");
        Assert.Throws<InvalidOperationException>(() =>
            LoopbackGuard.EnsureLoopback("http://127.0.0.1:5080;http://0.0.0.0:5081"));
    }
}
