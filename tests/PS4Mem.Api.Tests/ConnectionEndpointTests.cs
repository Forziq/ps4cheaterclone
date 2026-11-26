using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

namespace PS4Mem.Api.Tests;

public sealed class ConnectionEndpointTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _http;

    public ConnectionEndpointTests() => _http = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Status_when_nothing_is_set_is_disconnected_with_no_default_ip()
    {
        var json = await _http.GetFromJsonAsync<JsonElement>("/api/status");

        Assert.False(json.GetProperty("connected").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("defaultIp").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("version").ValueKind);
    }

    [Fact]
    public async Task Connect_without_any_ip_is_400()
    {
        var response = await _http.PostAsync("/api/connect", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("PS4 IP is not set", await Error(response));
    }

    [Fact]
    public async Task Connect_with_an_invalid_ip_is_400()
    {
        var response = await _http.PostAsJsonAsync("/api/connect", new { ip = "999.0.0.1" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Connect_when_the_console_does_not_answer_is_502_and_status_keeps_the_error()
    {
        var port = FreePort();
        await _http.PutAsJsonAsync("/api/settings", new { commandPort = port });

        var response = await _http.PostAsJsonAsync("/api/connect", new { ip = "127.0.0.1" });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var error = await Error(response);
        Assert.Contains($"127.0.0.1:{port}", error);
        var status = await _http.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.Equal(error, status.GetProperty("lastError").GetString());
    }

    [Theory]
    [InlineData("/api/processes")]
    [InlineData("/api/processes/85")]
    [InlineData("/api/processes/85/maps")]
    public async Task Console_calls_without_a_connection_are_409(string url)
    {
        var response = await _http.GetAsync(url);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Not connected", await Error(response));
    }

    [Fact]
    public async Task Disconnect_when_not_connected_is_fine()
    {
        var response = await _http.PostAsync("/api/disconnect", null);
        response.EnsureSuccessStatusCode();
    }

    private static async Task<string> Error(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
