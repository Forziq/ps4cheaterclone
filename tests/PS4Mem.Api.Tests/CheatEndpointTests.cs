using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Tests.Fakes;
using static PS4Mem.Core.Tests.Fakes.FakeConsole;

namespace PS4Mem.Api.Tests;

/// <summary>/api/cheats: list, add, edit and delete per title ID (T1.6 m1); write, lock and values (m2).</summary>
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

    [Fact]
    public async Task Memory_endpoints_need_a_pid_and_a_connection_but_unlock_does_not()
    {
        var id = await AddMoney();

        var noPid = await _http.PostAsJsonAsync($"/api/cheats/{Title}/{id}/write", new { });
        Assert.Equal(HttpStatusCode.BadRequest, noPid.StatusCode);
        Assert.Contains("pid is required", await Error(noPid));

        foreach (var url in new[] { $"/api/cheats/{Title}/{id}/write", $"/api/cheats/{Title}/{id}/lock", $"/api/cheats/{Title}/values" })
        {
            var response = await _http.PostAsJsonAsync(url, new { pid = Pid });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("Not connected", await Error(response));
        }

        var unlock = await _http.PostAsync($"/api/cheats/{Title}/{id}/unlock", null);
        unlock.EnsureSuccessStatusCode();
        Assert.False((await unlock.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("locked").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsync($"/api/cheats/{Title}/nope/unlock", null)).StatusCode);
    }

    [Fact]
    public async Task Write_values_lock_and_disconnect_against_the_fake_console()
    {
        var memory = new byte[0x100];
        BinaryPrimitives.WriteUInt32LittleEndian(memory, 500);
        var writes = new ConcurrentQueue<(ulong Address, byte[] Data)>();
        using var server = new FakePs4Server(c =>
        {
            c.Expect(Ps4Command.Version, 0);
            c.Send(U32(3), "1.3"u8.ToArray());
            ServeUntilEnd(c, memory, writes);
        });
        (await _http.PutAsJsonAsync("/api/settings", new { commandPort = server.Port })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/connect", new { ip = "127.0.0.1" })).EnsureSuccessStatusCode();
        var id = await AddMoney();

        var values = await (await _http.PostAsJsonAsync($"/api/cheats/{Title}/values", new { pid = Pid }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("500", Assert.Single(values.GetProperty("values").EnumerateArray()).GetProperty("value").GetString());
        Assert.Empty(writes);

        var write = await _http.PostAsJsonAsync($"/api/cheats/{Title}/{id}/write", new { pid = Pid, value = "750" });
        write.EnsureSuccessStatusCode();
        var written = await write.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("500", written.GetProperty("oldValue").GetString());
        Assert.Equal("750", written.GetProperty("cheat").GetProperty("value").GetString());
        Assert.Equal(750u, BinaryPrimitives.ReadUInt32LittleEndian(memory));
        Assert.Single(writes);

        var locked = await (await _http.PostAsJsonAsync($"/api/cheats/{Title}/{id}/lock", new { pid = Pid }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(locked.GetProperty("locked").GetBoolean());
        Assert.Equal(Pid, locked.GetProperty("lockPid").GetInt32());

        BinaryPrimitives.WriteUInt32LittleEndian(memory, 1); // the game spends the money
        Assert.True(SpinWait.SpinUntil(() => writes.Count >= 5, TimeSpan.FromSeconds(5)));
        Assert.Equal(750u, BinaryPrimitives.ReadUInt32LittleEndian(memory));
        var table = await _http.GetFromJsonAsync<JsonElement>($"/api/cheats/{Title}");
        var row = Assert.Single(table.GetProperty("cheats").EnumerateArray());
        Assert.True(row.GetProperty("locked").GetBoolean());
        Assert.True(row.GetProperty("lockWrites").GetInt64() >= 4);

        // Disconnect stops the lock before the socket closes: the server sees no write after CONSOLE_END.
        (await _http.PostAsync("/api/disconnect", null)).EnsureSuccessStatusCode();
        server.WaitForScript();
        table = await _http.GetFromJsonAsync<JsonElement>($"/api/cheats/{Title}");
        row = Assert.Single(table.GetProperty("cheats").EnumerateArray());
        Assert.False(row.GetProperty("locked").GetBoolean());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("lockPid").ValueKind);
        Assert.All(writes, w => Assert.Equal(0x1000UL, w.Address));
    }

    [Fact]
    public async Task Export_and_import_a_cheat_file()
    {
        await AddMoney();

        var export = await _http.GetAsync($"/api/cheats/cusa11711/export");
        export.EnsureSuccessStatusCode();
        Assert.Equal("application/json", export.Content.Headers.ContentType?.MediaType);
        Assert.Equal("CUSA11711.json", export.Content.Headers.ContentDisposition?.FileNameStar ?? export.Content.Headers.ContentDisposition?.FileName);
        var file = await export.Content.ReadAsStringAsync();
        Assert.Contains("\"titleId\": \"CUSA11711\"", file);

        // Merge of its own export changes nothing; into another game it's refused.
        var merge = await (await _http.PostAsync($"/api/cheats/{Title}/import", Json(file))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("merge", merge.GetProperty("mode").GetString());
        Assert.Equal(0, merge.GetProperty("added").GetInt32());
        Assert.Equal(1, merge.GetProperty("replaced").GetInt32());
        var wrongGame = await _http.PostAsync("/api/cheats/CUSA00001/import", Json(file));
        Assert.Equal(HttpStatusCode.BadRequest, wrongGame.StatusCode);
        Assert.Contains("for CUSA11711, not CUSA00001", await Error(wrongGame));

        var replace = await _http.PostAsync($"/api/cheats/{Title}/import?mode=replace",
            Json("""{"cheats": [{"description": "Ammo", "address": "0x2000", "type": "u16", "value": "99"}]}"""));
        replace.EnsureSuccessStatusCode();
        var replaced = await replace.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((1, 1), (replaced.GetProperty("added").GetInt32(), replaced.GetProperty("removed").GetInt32()));
        Assert.Equal("Ammo", Assert.Single(replaced.GetProperty("table").GetProperty("cheats").EnumerateArray())
            .GetProperty("description").GetString());

        var bad = await _http.PostAsync($"/api/cheats/{Title}/import?mode=sideways", Json(file));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("Unknown import mode", await Error(bad));
        var notJson = await _http.PostAsync($"/api/cheats/{Title}/import", Json("hello"));
        Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
        Assert.Contains("Not a cheat file", await Error(notJson));
    }

    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");

    /// <summary>Serves reads and writes of 0x1000.. from <paramref name="memory"/> until CONSOLE_END.</summary>
    private static void ServeUntilEnd(FakeConsole c, byte[] memory, ConcurrentQueue<(ulong, byte[])> writes)
    {
        while (true)
        {
            var r = c.ReadRequest();
            switch (r.Command)
            {
                case Ps4Command.ProcRead:
                    Assert.Equal(Pid, r.I32(0));
                    c.SendStatus(Ps4Status.Success);
                    c.Send(memory.AsSpan((int)(r.U64(4) - 0x1000), (int)r.U32(12)).ToArray());
                    break;
                case Ps4Command.ProcWrite:
                    Assert.Equal(Pid, r.I32(0));
                    c.SendStatus(Ps4Status.Success);
                    var data = c.ReadBytes((int)r.U32(12));
                    data.CopyTo(memory.AsSpan((int)(r.U64(4) - 0x1000)));
                    writes.Enqueue((r.U64(4), data));
                    c.SendStatus(Ps4Status.Success);
                    break;
                case Ps4Command.ConsoleEnd:
                    return;
                default:
                    throw new InvalidOperationException($"Unexpected {r.Command}");
            }
        }
    }

    private const int Pid = 85;

    private async Task<string> AddMoney()
    {
        var add = await _http.PostAsJsonAsync($"/api/cheats/{Title}", new { description = "Money", address = "0x1000", type = "u32", value = "999" });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        return (await add.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<string> Error(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
}
