using System.Globalization;
using System.Text.Json;
using PS4Mem.Core;
using PS4Mem.Core.Connection;
using PS4Mem.Core.Scanning;
using PS4Mem.Core.Sections;

namespace PS4Mem.Api;

/// <summary>
/// Body of <c>POST /api/scan/first</c>. Only <c>pid</c> is required.
/// <list type="bullet">
/// <item><c>type</c>: u8, i8, u16, i16, u32 (default), i32, u64, i64, f32 (float), f64 (double), string, bytes.</item>
/// <item><c>compare</c>: exact (default), bigger than, smaller than, between, unknown initial value.</item>
/// <item><c>value</c>, <c>value2</c> (between): text as typed; <c>hex</c> reads numbers as hex.</item>
/// <item><c>sections</c>: start addresses ("0x...") of the sections to scan; none = the default selection.</item>
/// <item><c>aligned</c>: false scans every byte; true or none uses the alignment setting (4).</item>
/// <item><c>method</c>: local (default) or console (falls back to local when the console can't run it).</item>
/// </list>
/// </summary>
public sealed record FirstScanRequest(
    int? Pid, string? Type, string? Compare, string? Value, string? Value2, bool? Hex,
    IReadOnlyList<string>? Sections, bool? Aligned, string? Method);

/// <summary>
/// Body of <c>POST /api/scan/next</c>. <c>type</c> defaults to the current results' type (it
/// can't change); <c>compare</c> adds increased (by), decreased (by), changed and unchanged.
/// </summary>
public sealed record NextScanRequest(string? Type, string? Compare, string? Value, string? Value2, bool? Hex);

/// <summary>Hits of one section in the current results. Addresses are hex strings.</summary>
public sealed record SectionHitsDto(string Name, string Start, ulong Size, long Hits, double Milliseconds);

/// <summary>The running or last scan and the current results (see <see cref="ScanStatus"/>).</summary>
public sealed record ScanStatusDto(
    string State, string? Kind, string? Query, string? Method,
    double Percent, string? Section, long Done, long Total, long Hits, double ElapsedSeconds, string? Message,
    int? Pid, int ScanNumber, long ResultCount, string? Type, int ValueSize, string? ResultMethod, bool CanUndo,
    IReadOnlyList<SectionHitsDto>? Sections);

/// <summary>
/// One results row. <c>value</c> is what the last scan found (the "previous value" column);
/// the current value comes from <c>POST /api/scan/results/values</c>. <c>sectionOffset</c> is
/// the address minus the section start.
/// </summary>
public sealed record ScanResultDto(long Index, string Address, string Value, string Section, string SectionStart, string SectionOffset);

/// <summary>A page of results. <c>total</c> is every result; <c>scanNumber</c> 0 = no results.</summary>
public sealed record ScanResultsDto(int ScanNumber, string? Type, int ValueSize, long Total, long Offset, int Count,
    IReadOnlyList<ScanResultDto> Results);

/// <summary>Body of <c>POST /api/scan/results/values</c>: the visible rows. No limit = the page size (5,000).</summary>
public sealed record ResultValuesRequest(long? Offset, int? Limit, bool? Hex);

public sealed record ResultValueDto(long Index, string Address, string Value);

/// <summary>
/// Current values of the rows asked for. Check <c>scanNumber</c> against the table: a scan or
/// undo in between changes which results the indexes point to.
/// </summary>
public sealed record ResultValuesDto(int ScanNumber, long Offset, IReadOnlyList<ResultValueDto> Values);

public static class ScanEndpoints
{
    // SSE data must be one line, so not the API's indented JSON.
    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web);

    public static void MapScanEndpoints(this IEndpointRouteBuilder app)
    {
        // Scans run in the background: these return 202 at once with the status; follow
        // GET /api/scan/progress. 400 bad input; 409 not connected, a scan is running, or no results yet.
        app.MapPost("/api/scan/first", (FirstScanRequest? request, ConnectionManager connection, ScanSession scans) =>
        {
            request ??= new FirstScanRequest(null, null, null, null, null, null, null, null, null);
            var pid = request.Pid ?? throw new ArgumentException("pid is required: the process you chose.");
            var type = ScanNames.ParseType(request.Type);
            var compare = ScanNames.ParseCompare(request.Compare);
            var query = ScanQuery.Parse(type, compare, request.Value, request.Value2, request.Hex ?? false);
            var options = new ScanRunOptions
            {
                Method = ScanNames.ParseMethod(request.Method),
                Alignment = request.Aligned == false ? 1 : null,
            };

            var target = connection.GetScanTarget();
            var sections = ChooseSections(connection.GetSections(pid), request.Sections);
            return Accepted(scans.StartFirst(target, pid, sections, query, options));
        });

        app.MapPost("/api/scan/next", (NextScanRequest? request, ConnectionManager connection, ScanSession scans) =>
        {
            request ??= new NextScanRequest(null, null, null, null, null);
            var current = scans.CurrentType ?? throw new ScanStateException("There are no scan results yet. Run a first scan.");
            var type = ScanNames.ParseType(request.Type, current);
            var compare = ScanNames.ParseCompare(request.Compare);
            var query = ScanQuery.Parse(type, compare, request.Value, request.Value2, request.Hex ?? false);
            return Accepted(scans.StartNext(connection.GetScanTarget(), query));
        });

        // Cancels a running scan and drops every result. 200 with the (idle) status.
        app.MapPost("/api/scan/new", async (ScanSession scans) => ToDto(await scans.NewAsync()));

        // Back to the results before the last next scan. 409 while a scan runs or with nothing to undo.
        app.MapPost("/api/scan/undo", (ScanSession scans) => ToDto(scans.Undo()));

        // Asks the running scan to stop at its next chunk; the previous results stay. Always 200.
        app.MapPost("/api/scan/cancel", (ScanSession scans) => ToDto(scans.Cancel()));

        // ?offset=0&limit=5000 (default page ScanResultsPageRows, at most ScanResultsMaxPageRows)
        // ?hex=true shows values in hex. 200 with an empty page when there are no results.
        app.MapGet("/api/scan/results", (long? offset, int? limit, bool? hex, ScanSession scans, PS4MemSettings defaults) =>
        {
            var page = scans.GetResults(offset ?? 0, Limit(limit, defaults));
            var rows = page.Rows.Select(r => new ScanResultDto(r.Index, Hex(r.Address),
                ValueFormatter.Format(page.Type!.Value, r.Value, hex ?? false), r.Section.DisplayName,
                Hex(r.Section.Start), Hex(r.Address - r.Section.Start))).ToList();
            return new ScanResultsDto(page.ScanNumber, page.Type?.ShortName(), page.ValueSize, page.Total, page.Offset,
                rows.Count, rows);
        });

        // Re-reads the current values of the visible rows from the PS4 (reads only). 409 when not connected.
        app.MapPost("/api/scan/results/values", (ResultValuesRequest? request, ConnectionManager connection,
            ScanSession scans, PS4MemSettings defaults, HttpContext http) =>
        {
            var target = connection.GetScanTarget();
            var page = scans.ReadCurrentValues(target, request?.Offset ?? 0, Limit(request?.Limit, defaults), http.RequestAborted);
            return new ResultValuesDto(page.ScanNumber, page.Offset, page.Values
                .Select(v => new ResultValueDto(v.Index, Hex(v.Address),
                    ValueFormatter.Format(page.Type!.Value, v.Value, request?.Hex ?? false)))
                .ToList());
        });

        // The status as JSON, once.
        app.MapGet("/api/scan/status", (ScanSession scans) => ToDto(scans.Status));

        // Server-sent events: "progress" events while a scan runs (every ScanProgressInterval, when
        // something changed), then one "done" event with the final status, then the stream ends.
        // When no scan runs it sends the "done" event at once.
        app.MapGet("/api/scan/progress", async (ScanSession scans, PS4MemSettings defaults, HttpContext http) =>
        {
            var ct = http.RequestAborted;
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            string? last = null;
            try
            {
                while (true)
                {
                    var status = scans.Status;
                    var running = status.State == ScanState.Running;
                    var json = JsonSerializer.Serialize(ToDto(status), SseJson);
                    if (json != last || !running)
                    {
                        await http.Response.WriteAsync($"event: {(running ? "progress" : "done")}\ndata: {json}\n\n", ct);
                        await http.Response.Body.FlushAsync(ct);
                        last = json;
                    }
                    if (!running) break;
                    await Task.Delay(defaults.ScanProgressInterval, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The app closed the stream; the scan carries on.
            }
        });
    }

    private static int Limit(int? limit, PS4MemSettings defaults) =>
        limit is null ? defaults.ScanResultsPageRows
        : limit < 1 ? throw new ArgumentException("limit must be at least 1.")
        : Math.Min(limit.Value, defaults.ScanResultsMaxPageRows);

    private static IResult Accepted(ScanStatus status) => Results.Json(ToDto(status), statusCode: StatusCodes.Status202Accepted);

    /// <summary>The sections whose start is in <paramref name="starts"/>, or the default selection.</summary>
    private static List<Section> ChooseSections(SectionSelection selection, IReadOnlyList<string>? starts)
    {
        if (starts is null)
            return selection.Selected.ToList();

        var byStart = selection.Sections.ToLookup(s => s.Start);
        var chosen = new List<Section>(starts.Count);
        foreach (var text in starts)
        {
            var start = ParseAddress(text);
            var match = byStart[start].ToList();
            if (match.Count == 0)
                throw new ArgumentException($"No section starts at {text}. Reload the sections list.");
            chosen.AddRange(match);
        }
        return chosen.DistinctBy(s => (s.Start, s.End)).ToList();
    }

    internal static ulong ParseAddress(string text)
    {
        var t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        if (!ulong.TryParse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var address))
            throw new ArgumentException($"'{text}' isn't a hex address like 0x200000000.");
        return address;
    }

    internal static string Hex(ulong value) => $"0x{value:X}";

    internal static ScanStatusDto ToDto(ScanStatus s) => new(
        s.State.ToString().ToLowerInvariant(), s.Kind, s.Query, s.Method,
        s.Percent, s.Section, s.Done, s.Total, s.Hits, s.ElapsedSeconds, s.Message,
        s.Pid, s.ScanNumber, s.ResultCount, s.Type?.ShortName(), s.ValueSize, s.ResultMethod, s.CanUndo,
        s.Sections?.Select(x => new SectionHitsDto(x.Section.DisplayName, Hex(x.Section.Start), x.Section.Size, x.Hits,
            Math.Round(x.Elapsed.TotalMilliseconds, 1))).ToList());
}
