using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace PS4Mem.Api.Tests;

public sealed class SettingsEndpointTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _http;

    public SettingsEndpointTests() => _http = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Get_returns_the_defaults_in_camelCase()
    {
        var json = await GetJson("/api/settings");

        Assert.Equal("", json.GetProperty("consoleIp").GetString());
        Assert.Equal(0, json.GetProperty("recentIps").GetArrayLength());
        Assert.Equal(744, json.GetProperty("commandPort").GetInt32());
        Assert.Equal(9090, json.GetProperty("loaderPort").GetInt32());
        Assert.Equal("payloads/ps4debug-ng_v1.3.4_release_2026-11-05.bin", json.GetProperty("payloadPath").GetString());
        Assert.False(json.GetProperty("debugLogging").GetBoolean());
    }

    [Fact]
    public async Task Put_changes_only_the_given_fields_and_saves_them()
    {
        var response = await _http.PutAsJsonAsync("/api/settings", new { loaderPort = 9021, consoleIp = "192.0.2.10" });
        response.EnsureSuccessStatusCode();

        var json = await GetJson("/api/settings");
        Assert.Equal(9021, json.GetProperty("loaderPort").GetInt32());
        Assert.Equal("192.0.2.10", json.GetProperty("consoleIp").GetString());
        Assert.Equal(744, json.GetProperty("commandPort").GetInt32());
        Assert.Contains("\"loaderPort\": 9021", File.ReadAllText(_factory.SettingsPath));
    }

    [Fact]
    public async Task Put_with_invalid_values_is_400_with_every_problem()
    {
        var response = await _http.PutAsJsonAsync("/api/settings", new { consoleIp = "999.1.1.1", loaderPort = 70000 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString();
        Assert.Contains("consoleIp", error);
        Assert.Contains("loaderPort", error);
        Assert.False(File.Exists(_factory.SettingsPath));
    }

    [Fact]
    public async Task Put_with_broken_json_is_400()
    {
        var response = await _http.PutAsync("/api/settings",
            new StringContent("{ \"loaderPort\": ", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("Invalid JSON", json.GetProperty("error").GetString());
    }

    private async Task<JsonElement> GetJson(string url)
    {
        var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
