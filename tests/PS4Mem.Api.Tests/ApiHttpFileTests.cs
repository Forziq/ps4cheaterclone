using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Tests.Fakes;
using static PS4Mem.Core.Tests.Fakes.FakeConsole;

namespace PS4Mem.Api.Tests;

/// <summary>
/// Replays every request in src/PS4Mem.Api/api.http, top to bottom, against the API connected
/// to a fake console. If a sample in the file breaks, this test fails.
/// </summary>
public sealed partial class ApiHttpFileTests
{
    private sealed record HttpFileRequest(string Title, HttpMethod Method, string Url, string? Body);

    [Fact]
    public async Task Every_call_in_api_http_works_against_the_fake_console()
    {
        // The file injects first: the fake loader takes the payload and then "starts" ps4debug.
        var commandPort = FakeLoader.FreePort();
        FakePs4Server? server = null;
        using var loader = new FakeLoader(_ => server = new FakePs4Server(commandPort,
            c => Assert.True(c.ClientClosed()), // the injector's "does 744 answer?" check
            c =>
        {
            c.Expect(Ps4Command.Version, 0);
            c.Send(U32(3), "1.3"u8.ToArray());

            c.Expect(Ps4Command.ProcList, 0);
            c.SendStatus(Ps4Status.Success);
            c.Send(U32(2), ProcEntry("SceShellCore", 45), ProcEntry("ac3_engine_orbis_fn.elf", 85));

            Assert.Equal(85, c.Expect(Ps4Command.ProcInfo, 4).I32(0));
            c.SendStatus(Ps4Status.Success);
            c.Send(ProcInfo(85, "ac3_engine_orbis_fn.elf", "/app0/ac3.elf", "CUSA11711", ""));

            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(85, c.Expect(Ps4Command.ProcMaps, 4).I32(0));
                c.SendStatus(Ps4Status.Success);
                c.Send(U32(2),
                    MapEntry("", 0x2_0000_0000, 0x2_0010_0000, 0, Ps4Protocol.ProtRead | Ps4Protocol.ProtWrite),
                    MapEntry("executable", 0x40_0000, 0x50_0000, 0, Ps4Protocol.ProtRead | Ps4Protocol.ProtExec));
            }

            c.Expect(Ps4Command.ConsoleEnd, 0);
        },
            c => // the connect near the end
            {
                c.Expect(Ps4Command.Version, 0);
                c.Send(U32(3), "1.3"u8.ToArray());
                c.Expect(Ps4Command.ConsoleEnd, 0);
            }));

        // The file's inject and connect send no IP, so they use the saved one: the fake console.
        using var factory = new ApiFactory();
        var payloadPath = Path.Combine(factory.TempDir, "ps4debug.bin");
        File.WriteAllBytes(payloadPath, [1, 2, 3]);
        factory.WithSettingsFile(JsonSerializer.Serialize(new
        {
            consoleIp = "127.0.0.1", commandPort, loaderPort = loader.Port, payloadPath,
        }));
        using var http = factory.CreateClient();

        var requests = Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "api.http")));
        Assert.True(requests.Count >= 13, $"api.http has only {requests.Count} requests");

        var replies = new Dictionary<string, JsonElement>();
        foreach (var r in requests)
        {
            using var message = new HttpRequestMessage(r.Method, r.Url);
            if (r.Body is not null)
                message.Content = new StringContent(r.Body, Encoding.UTF8, "application/json");
            var response = await http.SendAsync(message);
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"'{r.Title}' ({r.Method} {r.Url}) got {(int)response.StatusCode}: {text}");
            replies[r.Title] = JsonDocument.Parse(text).RootElement.Clone();
        }
        Assert.NotNull(server);
        server.WaitForScript();
        server.Dispose();

        // Spot checks that the samples show what their titles promise.
        JsonElement Reply(string titleStart) => replies.First(kv => kv.Key.StartsWith(titleStart)).Value;

        Assert.False(Reply("Connection state").GetProperty("connected").GetBoolean());
        Assert.Equal("saved settings", Reply("Connection state").GetProperty("defaultIpSource").GetString());
        Assert.Equal(3, Reply("Inject the payload").GetProperty("bytesSent").GetInt64());
        Assert.True(Reply("Inject the payload").GetProperty("status").GetProperty("connected").GetBoolean());
        Assert.Equal("1.3", Reply("Status again").GetProperty("version").GetString());
        Assert.True(Reply("Connect when the payload").GetProperty("connected").GetBoolean());

        var processes = Reply("Every process");
        Assert.Equal(85, processes.GetProperty("suggestedPid").GetInt32());
        var game = processes.GetProperty("processes").EnumerateArray().Single(p => p.GetProperty("suggested").GetBoolean());
        Assert.Equal("ac3_engine_orbis_fn.elf", game.GetProperty("name").GetString());

        Assert.Equal("CUSA11711", Reply("Process info").GetProperty("titleId").GetString());

        var maps = Reply("Sections with the default");
        Assert.Equal(1, maps.GetProperty("selectedCount").GetInt32());
        var heap = maps.GetProperty("sections")[1]; // sorted by start: executable first
        Assert.Equal("0x200000000", heap.GetProperty("start").GetString());
        Assert.Equal("(unnamed)", heap.GetProperty("displayName").GetString());
        Assert.Contains("unnamed", heap.GetProperty("reason").GetString());

        Assert.Equal(["anon", "exe"], Reply("Your own name filter").GetProperty("nameFilter").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(2, Reply("Your own name filter").GetProperty("selectedCount").GetInt32());
        Assert.Equal(2, Reply("Select every section").GetProperty("selectedCount").GetInt32());

        Assert.False(Reply("Disconnect").GetProperty("connected").GetBoolean());
    }

    /// <summary>
    /// A small .http reader: <c>@name = value</c> variables, <c>###</c> starts a request (the rest of
    /// that line is its title), <c>#</c> lines are comments, then the request line, headers, a blank
    /// line and an optional body.
    /// </summary>
    private static List<HttpFileRequest> Parse(string text)
    {
        var vars = new Dictionary<string, string>();
        var requests = new List<HttpFileRequest>();
        var blocks = Regex.Split(text.Replace("\r\n", "\n"), @"^###", RegexOptions.Multiline);

        foreach (var block in blocks.Select((b, i) => (b, i)))
        {
            var lines = block.b.Split('\n');
            var title = block.i == 0 ? "" : lines[0].Trim();
            var rest = block.i == 0 ? lines : lines[1..];

            string? requestLine = null;
            var inBody = false;
            var body = new StringBuilder();
            foreach (var line in rest)
            {
                if (VariableLine().Match(line) is { Success: true } v) { vars[v.Groups[1].Value] = v.Groups[2].Value.Trim(); continue; }
                if (!inBody && line.TrimStart().StartsWith('#')) continue;
                if (requestLine is null) { if (line.Trim().Length > 0) requestLine = line.Trim(); continue; }
                if (!inBody) { if (line.Trim().Length == 0) inBody = true; continue; } // headers: only JSON here
                body.AppendLine(line);
            }
            if (requestLine is null) continue;

            var parts = requestLine.Split(' ', 2);
            var url = Substitute(parts[1], vars).Replace("http://127.0.0.1:5080", "");
            var bodyText = body.ToString().Trim();
            requests.Add(new HttpFileRequest(title, new HttpMethod(parts[0]), url, bodyText.Length > 0 ? Substitute(bodyText, vars) : null));
        }
        return requests;
    }

    private static string Substitute(string s, Dictionary<string, string> vars) =>
        Placeholder().Replace(s, m => vars.TryGetValue(m.Groups[1].Value, out var v) ? Substitute(v, vars) : m.Value);

    [GeneratedRegex(@"^@(\w+)\s*=\s*(.*)$")]
    private static partial Regex VariableLine();

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex Placeholder();
}
