using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PS4Mem.Core.Payload;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Tests.Fakes;
using static PS4Mem.Core.Tests.Fakes.FakeConsole;

namespace PS4Mem.Api.Tests;

public sealed class InjectEndpointTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _http;
    private readonly string _payloadPath;
    private FakePs4Server? _ps4debug;

    public InjectEndpointTests()
    {
        _http = _factory.CreateClient();
        _payloadPath = Path.Combine(_factory.TempDir, "ps4debug.bin");
        File.WriteAllBytes(_payloadPath, [0xDE, 0xAD, 0xBE, 0xEF]);
    }

    public void Dispose()
    {
        _factory.Dispose();
        _ps4debug?.Dispose();
    }

    private Task Settings(int commandPort, int loaderPort) =>
        _http.PutAsJsonAsync("/api/settings", new { commandPort, loaderPort, payloadPath = _payloadPath });

    [Fact]
    public async Task Inject_sends_the_payload_and_connects()
    {
        var commandPort = FakeLoader.FreePort();
        using var loader = new FakeLoader(_ => _ps4debug = new FakePs4Server(commandPort,
            c => Assert.True(c.ClientClosed()), // the injector's "does 744 answer?" check
            c =>
            {
                c.Expect(Ps4Command.Version, 0);
                c.Send(U32(3), "1.3"u8.ToArray());
                c.Expect(Ps4Command.ConsoleEnd, 0);
            }));
        await Settings(commandPort, loader.Port);

        var response = await _http.PostAsJsonAsync("/api/inject", new { ip = "127.0.0.1" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, json.GetProperty("bytesSent").GetInt64());
        Assert.Equal(_payloadPath, json.GetProperty("payloadPath").GetString());
        Assert.True(json.GetProperty("waitedSeconds").GetDouble() >= 0);
        Assert.True(json.GetProperty("status").GetProperty("connected").GetBoolean());
        Assert.Equal("1.3", json.GetProperty("status").GetProperty("version").GetString());
        Assert.Equal([0xDE, 0xAD, 0xBE, 0xEF], await loader.Received);

        (await _http.PostAsync("/api/disconnect", null)).EnsureSuccessStatusCode();
        _ps4debug!.WaitForScript();
    }

    [Fact]
    public async Task Inject_when_ps4debug_already_answers_is_409()
    {
        using var running = new FakePs4Server(c => Assert.True(c.ClientClosed()));
        using var loader = new FakeLoader();
        await Settings(running.Port, loader.Port);

        var response = await _http.PostAsJsonAsync("/api/inject", new { ip = "127.0.0.1" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("already answers", await Error(response));
        Assert.False(loader.Contacted);
    }

    [Fact]
    public async Task Refused_loader_is_502_with_the_binloader_help()
    {
        await Settings(FakeLoader.FreePort(), FakeLoader.FreePort());

        var response = await _http.PostAsJsonAsync("/api/inject", new { ip = "127.0.0.1" });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var error = await Error(response);
        Assert.Contains(PayloadInjector.RefusedHelp, error);
        var status = await _http.GetFromJsonAsync<JsonElement>("/api/status");
        Assert.Equal(error, status.GetProperty("lastError").GetString());
    }

    [Fact]
    public async Task Missing_payload_file_is_400()
    {
        using var loader = new FakeLoader();
        await Settings(FakeLoader.FreePort(), loader.Port);

        var response = await _http.PostAsJsonAsync("/api/inject",
            new { ip = "127.0.0.1", payloadPath = Path.Combine(_factory.TempDir, "missing.bin") });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("was not found", await Error(response));
        Assert.False(loader.Contacted);
    }

    private static async Task<string> Error(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
}
