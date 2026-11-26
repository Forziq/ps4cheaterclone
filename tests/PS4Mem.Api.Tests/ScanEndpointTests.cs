using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Tests.Fakes;
using static PS4Mem.Core.Tests.Fakes.FakeConsole;

namespace PS4Mem.Api.Tests;

/// <summary>/api/scan: first, next, new, undo, cancel, status and the progress stream (T1.5).</summary>
public sealed class ScanEndpointTests : IDisposable
{
    private const int Pid = 85;
    private const ulong Heap = 0x2_0000_0000;
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _http;

    public ScanEndpointTests() => _http = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Without_a_connection_scans_are_409_and_the_status_is_idle()
    {
        var first = await _http.PostAsJsonAsync("/api/scan/first", new { pid = Pid, value = "1500" });
        Assert.Equal(HttpStatusCode.Conflict, first.StatusCode);
        Assert.Contains("Not connected", await Error(first));

        var next = await _http.PostAsJsonAsync("/api/scan/next", new { value = "1500" });
        Assert.Equal(HttpStatusCode.Conflict, next.StatusCode);
        Assert.Contains("no scan results", await Error(next));

        Assert.Equal(HttpStatusCode.Conflict, (await _http.PostAsync("/api/scan/undo", null)).StatusCode);

        foreach (var url in new[] { "/api/scan/cancel", "/api/scan/new" })
        {
            var response = await _http.PostAsync(url, null);
            response.EnsureSuccessStatusCode();
            Assert.Equal("idle", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        }

        var status = await _http.GetFromJsonAsync<JsonElement>("/api/scan/status");
        Assert.Equal("idle", status.GetProperty("state").GetString());
        Assert.Equal(0, status.GetProperty("resultCount").GetInt64());
    }

    [Fact]
    public async Task Progress_when_idle_sends_one_done_event_and_ends()
    {
        var response = await _http.GetAsync("/api/scan/progress");

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var events = ParseEvents(await response.Content.ReadAsStringAsync());
        var (name, data) = Assert.Single(events);
        Assert.Equal("done", name);
        Assert.Equal("idle", data.GetProperty("state").GetString());
    }

    [Theory]
    [InlineData("""{"value": "1"}""", "pid is required")]
    [InlineData("""{"pid": 85, "type": "u33", "value": "1"}""", "Unknown value type 'u33'")]
    [InlineData("""{"pid": 85, "compare": "nearly", "value": "1"}""", "Unknown compare 'nearly'")]
    [InlineData("""{"pid": 85, "value": "abc"}""", "abc")]
    [InlineData("""{"pid": 85, "method": "cloud", "value": "1"}""", "Unknown scan method")]
    public async Task Bad_first_scan_input_is_400(string body, string message)
    {
        var response = await _http.PostAsync("/api/scan/first",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(message, await Error(response));
    }

    [Fact]
    public async Task First_next_undo_and_disconnect_against_the_fake_console()
    {
        var memory = new byte[0x1000];
        BinaryPrimitives.WriteUInt32LittleEndian(memory.AsSpan(0x10), 1500);
        BinaryPrimitives.WriteUInt32LittleEndian(memory.AsSpan(0x20), 1500);
        var reads = new List<(ulong Address, uint Length)>();

        using var server = new FakePs4Server(c =>
        {
            c.Expect(Ps4Command.Version, 0);
            c.Send(U32(3), "1.3"u8.ToArray());

            Assert.Equal(Pid, c.Expect(Ps4Command.ProcMaps, 4).I32(0));
            c.SendStatus(Ps4Status.Success);
            c.Send(U32(2),
                MapEntry("", Heap, Heap + 0x1000, 0, Ps4Protocol.ProtRead | Ps4Protocol.ProtWrite),
                MapEntry("executable", 0x40_0000, 0x50_0000, 0, Ps4Protocol.ProtRead | Ps4Protocol.ProtExec));

            ServeRead(c, memory, reads); // first scan: the whole heap section (the default selection)
            BinaryPrimitives.WriteUInt32LittleEndian(memory.AsSpan(0x20), 1400);
            ServeRead(c, memory, reads); // current values of the results table
            ServeRead(c, memory, reads); // next scan: both hits in one read

            c.Expect(Ps4Command.ConsoleEnd, 0);
        });
        (await _http.PutAsJsonAsync("/api/settings", new { commandPort = server.Port })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/connect", new { ip = "127.0.0.1" })).EnsureSuccessStatusCode();

        var first = await _http.PostAsJsonAsync("/api/scan/first", new { pid = Pid, value = "1500" });
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var done = await WaitForDone();
        Assert.Equal("done", done.GetProperty("state").GetString());
        Assert.Equal(2, done.GetProperty("resultCount").GetInt64());
        Assert.Equal("u32", done.GetProperty("type").GetString());
        var section = Assert.Single(done.GetProperty("sections").EnumerateArray());
        Assert.Equal("0x200000000", section.GetProperty("start").GetString());
        Assert.Equal(2, section.GetProperty("hits").GetInt64());

        var results = await _http.GetFromJsonAsync<JsonElement>("/api/scan/results?offset=1&limit=10");
        Assert.Equal(2, results.GetProperty("total").GetInt64());
        Assert.Equal(1, results.GetProperty("count").GetInt32());
        var row = Assert.Single(results.GetProperty("results").EnumerateArray());
        Assert.Equal(1, row.GetProperty("index").GetInt64());
        Assert.Equal("0x200000020", row.GetProperty("address").GetString());
        Assert.Equal("1500", row.GetProperty("value").GetString());
        Assert.Equal("(unnamed)", row.GetProperty("section").GetString());
        Assert.Equal("0x20", row.GetProperty("sectionOffset").GetString());
        var hexPage = await _http.GetFromJsonAsync<JsonElement>("/api/scan/results?limit=1&hex=true");
        Assert.Equal("0x000005DC", hexPage.GetProperty("results")[0].GetProperty("value").GetString());

        var values = await (await _http.PostAsJsonAsync("/api/scan/results/values", new { offset = 0, limit = 2 }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, values.GetProperty("scanNumber").GetInt32());
        Assert.Equal(["1500", "1400"], values.GetProperty("values").EnumerateArray().Select(v => v.GetProperty("value").GetString()));

        var next = await _http.PostAsJsonAsync("/api/scan/next", new { value = "1500" });
        Assert.Equal(HttpStatusCode.Accepted, next.StatusCode);
        done = await WaitForDone();
        Assert.Equal(1, done.GetProperty("resultCount").GetInt64());
        Assert.Equal(2, done.GetProperty("scanNumber").GetInt32());
        Assert.True(done.GetProperty("canUndo").GetBoolean());

        var undo = await (await _http.PostAsync("/api/scan/undo", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, undo.GetProperty("resultCount").GetInt64());

        (await _http.PostAsync("/api/disconnect", null)).EnsureSuccessStatusCode();
        var status = await _http.GetFromJsonAsync<JsonElement>("/api/scan/status");
        Assert.Equal(0, status.GetProperty("resultCount").GetInt64());
        server.WaitForScript();

        Assert.Equal([(Heap, 0x1000u), (Heap + 0x10, 0x14u), (Heap + 0x10, 0x14u)], reads);
    }

    [Fact]
    public async Task Results_without_a_scan_are_an_empty_page_and_values_need_a_connection()
    {
        var results = await _http.GetFromJsonAsync<JsonElement>("/api/scan/results");
        Assert.Equal(0, results.GetProperty("total").GetInt64());
        Assert.Equal(0, results.GetProperty("scanNumber").GetInt32());
        Assert.Empty(results.GetProperty("results").EnumerateArray());

        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync("/api/scan/results?limit=0")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.GetAsync("/api/scan/results?offset=-1")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _http.PostAsJsonAsync("/api/scan/results/values", new { })).StatusCode);
    }

    [Fact]
    public async Task First_scan_of_a_section_start_that_does_not_exist_is_400()
    {
        using var server = new FakePs4Server(c =>
        {
            c.Expect(Ps4Command.Version, 0);
            c.Send(U32(3), "1.3"u8.ToArray());
            c.Expect(Ps4Command.ProcMaps, 4);
            c.SendStatus(Ps4Status.Success);
            c.Send(U32(1), MapEntry("", Heap, Heap + 0x1000, 0, Ps4Protocol.ProtRead | Ps4Protocol.ProtWrite));
            c.Expect(Ps4Command.ConsoleEnd, 0);
        });
        (await _http.PutAsJsonAsync("/api/settings", new { commandPort = server.Port })).EnsureSuccessStatusCode();
        (await _http.PostAsJsonAsync("/api/connect", new { ip = "127.0.0.1" })).EnsureSuccessStatusCode();

        var response = await _http.PostAsJsonAsync("/api/scan/first",
            new { pid = Pid, value = "1", sections = new[] { "0x300000000" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("No section starts at 0x300000000", await Error(response));
        (await _http.PostAsync("/api/disconnect", null)).EnsureSuccessStatusCode();
        server.WaitForScript();
    }

    // ---- Helpers ----

    /// <summary>Answers one CMD_PROC_READ from <paramref name="memory"/> (which starts at <see cref="Heap"/>).</summary>
    internal static void ServeRead(FakeConsole c, byte[] memory, List<(ulong, uint)> reads)
    {
        var r = c.Expect(Ps4Command.ProcRead, 16);
        Assert.Equal(Pid, r.I32(0));
        var address = r.U64(4);
        var length = r.U32(12);
        reads.Add((address, length));
        c.SendStatus(Ps4Status.Success);
        c.Send(memory.AsSpan((int)(address - Heap), (int)length).ToArray());
    }

    /// <summary>Follows the progress stream to its "done" event and returns that status.</summary>
    private async Task<JsonElement> WaitForDone()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var text = await (await _http.GetAsync("/api/scan/progress", cts.Token)).Content.ReadAsStringAsync(cts.Token);
        var events = ParseEvents(text);
        Assert.All(events[..^1], e => Assert.Equal("progress", e.Name));
        Assert.Equal("done", events[^1].Name);
        return events[^1].Data;
    }

    internal static List<(string Name, JsonElement Data)> ParseEvents(string text) =>
        text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block =>
            {
                var lines = block.Split('\n');
                var name = lines.Single(l => l.StartsWith("event: "))["event: ".Length..];
                var data = lines.Single(l => l.StartsWith("data: "))["data: ".Length..];
                return (name, JsonDocument.Parse(data).RootElement.Clone());
            })
            .ToList();

    private static async Task<string> Error(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString()!;
}
