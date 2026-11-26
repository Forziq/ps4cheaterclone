using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace PS4Mem.Api.Tests;

/// <summary>/api/cheats: list, add, edit and delete per title ID (T1.6 m1).</summary>
public sealed class CheatEndpointTests : IDisposable
{
    private const string Title = "CUSA11711";
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _http;

    public CheatEndpointTests() => _http = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    private string CheatsDir => Path.Combine(_factory.TempDir, "cheats");

    [Fact]
    public async Task Add_edit_and_delete_a_cheat()
    {
        var empty = await _http.GetFromJsonAsync<JsonElement>("/api/cheats/cusa11711");
        Assert.Equal(Title, empty.GetProperty("titleId").GetString());
        Assert.Equal(Path.Combine(CheatsDir, "CUSA11711.json"), empty.GetProperty("file").GetString());
        Assert.Empty(empty.GetProperty("cheats").EnumerateArray());

        var add = await _http.PostAsJsonAsync($"/api/cheats/{Title}", new
        {
            description = "Money", address = "0x2001A3F10", type = "u32", value = "999",
            section = "", sectionStart = "0x200000000", game = "ac3_engine_orbis_fn.elf",
        });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        var cheat = await add.Content.ReadFromJsonAsync<JsonElement>();
        var id = cheat.GetProperty("id").GetString();
        Assert.Equal("0x2001A3F10", cheat.GetProperty("address").GetString());
        Assert.Equal("0x1A3F10", cheat.GetProperty("sectionOffset").GetString());
        Assert.Equal("u32", cheat.GetProperty("type").GetString());
        Assert.Equal("999", cheat.GetProperty("value").GetString());

        var edit = await _http.PutAsJsonAsync($"/api/cheats/{Title}/{id}", new { value = "FF", hex = true, description = "Cash" });
        edit.EnsureSuccessStatusCode();
        var edited = await edit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("255", edited.GetProperty("value").GetString());
        Assert.Equal("Cash", edited.GetProperty("description").GetString());
        Assert.Equal("0x2001A3F10", edited.GetProperty("address").GetString());

        var table = await _http.GetFromJsonAsync<JsonElement>($"/api/cheats/{Title}");
        Assert.Equal("ac3_engine_orbis_fn.elf", table.GetProperty("game").GetString());
        Assert.Equal("Cash", Assert.Single(table.GetProperty("cheats").EnumerateArray()).GetProperty("description").GetString());

        var games = await _http.GetFromJsonAsync<JsonElement>("/api/cheats");
        Assert.Equal(CheatsDir, games.GetProperty("folder").GetString());
        var game = Assert.Single(games.GetProperty("games").EnumerateArray());
        Assert.Equal(Title, game.GetProperty("titleId").GetString());
        Assert.Equal(1, game.GetProperty("count").GetInt32());

        Assert.Equal(HttpStatusCode.NoContent, (await _http.DeleteAsync($"/api/cheats/{Title}/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.DeleteAsync($"/api/cheats/{Title}/{id}")).StatusCode);
        table = await _http.GetFromJsonAsync<JsonElement>($"/api/cheats/{Title}");
        Assert.Empty(table.GetProperty("cheats").EnumerateArray());
    }

    [Theory]
    [InlineData("""{"type": "u32", "value": "1"}""", "address is missing")]
    [InlineData("""{"address": "0x10", "type": "u32", "value": "x"}""", "x")]
    [InlineData("""{"address": "0x10", "type": "bytes", "value": "12 ??"}""", "wildcards")]
    [InlineData("""{"address": "0x10", "type": "nope", "value": "1"}""", "Unknown value type")]
    [InlineData("""{"address": "nope", "value": "1"}""", "isn't a hex address")]
    public async Task Bad_cheats_are_400(string body, string message)
    {
        var response = await _http.PostAsync($"/api/cheats/{Title}", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(message, await Error(response));
    }

    [Fact]
    public async Task Bad_title_ids_are_400_and_unknown_cheats_404()
    {
        var bad = await _http.GetAsync("/api/cheats/CUSA%2011711");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("isn't a title ID", await Error(bad));

        var missing = await _http.PutAsJsonAsync($"/api/cheats/{Title}/abcd1234", new { value = "1" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Contains("no cheat 'abcd1234'", await Error(missing));
    }

    [Fact]
    public async Task A_broken_cheat_file_is_409_and_listed_with_its_error()
    {
        Directory.CreateDirectory(CheatsDir);
        File.WriteAllText(Path.Combine(CheatsDir, "CUSA00001.json"), "broken");

        var response = await _http.GetAsync("/api/cheats/CUSA00001");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("can't be read", await Error(response));

        var games = await _http.GetFromJsonAsync<JsonElement>("/api/cheats");
        var game = Assert.Single(games.GetProperty("games").EnumerateArray());
        Assert.Contains("can't be read", game.GetProperty("error").GetString());
    }

    private static async Task<string> Error(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
}
