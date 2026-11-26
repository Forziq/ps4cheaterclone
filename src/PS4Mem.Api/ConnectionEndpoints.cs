using PS4Mem.Core;
using PS4Mem.Core.Connection;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Sections;

namespace PS4Mem.Api;

/// <summary>Body of <c>POST /api/connect</c>. No body, or no ip, uses PS4_IP, ps4ip.local or the saved IP.</summary>
public sealed record ConnectRequest(string? Ip);

/// <summary>
/// Body of <c>POST /api/inject</c>. Every field is optional: no ip finds it as connect does,
/// no payloadPath or loaderPort uses the saved ones (and ones that work are saved).
/// </summary>
public sealed record InjectRequest(string? Ip, string? PayloadPath, int? LoaderPort);

/// <summary>What the injection sent, how long ps4debug took to start, and the connection status.</summary>
public sealed record InjectResponseDto(
    string PayloadPath, long BytesSent, int LoaderPort, double WaitedSeconds, ConnectionStatus Status);

/// <summary>One process. <c>Suggested</c> marks the likely game; the user still chooses.</summary>
public sealed record ProcessDto(int Pid, string Name, bool Suggested, string? SuggestionReason);

/// <summary>
/// Every process (by pid), the suggested game's pid and why. The API never picks a process:
/// the app sends the pid the user chose.
/// </summary>
public sealed record ProcessListDto(IReadOnlyList<ProcessDto> Processes, int? SuggestedPid, string SuggestionReason);

/// <summary>
/// One section. Addresses are hex strings (<c>"0x..."</c>) because JSON numbers can't hold a
/// full 64-bit address exactly; sizes are plain numbers (at most 64 GB, well within range).
/// </summary>
public sealed record SectionDto(
    string Name, string DisplayName, string Start, string End, ulong Size, string Offset,
    ushort Prot, string ProtText, bool Selected, string Reason);

public sealed record SectionListDto(
    int Pid, IReadOnlyList<string> NameFilter, bool IncludeReadOnly, IReadOnlyList<SectionDto> Sections,
    int SelectedCount, int TotalCount, ulong SelectedBytes, ulong TotalBytes, int SkippedMaps);

public static class ConnectionEndpoints
{
    public static void MapConnectionEndpoints(this IEndpointRouteBuilder app)
    {
        // Connected or not, version, and the IP a connect would use (pre-fills the IP field).
        app.MapGet("/api/status", (ConnectionManager connection) => connection.Status);

        // 200 with the status; 400 no/invalid IP; 409 already connected; 502 the PS4 didn't answer.
        app.MapPost("/api/connect", (ConnectRequest? request, ConnectionManager connection) =>
            connection.Connect(request?.Ip));

        // Sends the payload, waits up to 20 s for ps4debug, then connects. 200 with what it did and the status;
        // 400 no/invalid IP or a missing/empty payload file; 409 already connected, ps4debug already
        // answers, or another injection is running; 502 loader refused or ps4debug never answered.
        app.MapPost("/api/inject", async (InjectRequest? request, ConnectionManager connection, HttpContext http) =>
        {
            var result = await connection.InjectAsync(request?.Ip, request?.PayloadPath, request?.LoaderPort,
                ct: http.RequestAborted);
            var i = result.Injection;
            return new InjectResponseDto(i.PayloadPath, i.BytesSent, i.LoaderPort,
                Math.Round(i.WaitedForStart.TotalSeconds, 1), result.Status);
        });

        // Always 200, also when not connected.
        app.MapPost("/api/disconnect", (ConnectionManager connection) =>
        {
            connection.Disconnect();
            return connection.Status;
        });

        app.MapGet("/api/processes", (ConnectionManager connection) =>
        {
            var listing = connection.GetProcesses();
            var suggested = listing.Suggestion.Process;
            var processes = listing.Processes
                .OrderBy(p => p.Pid)
                .Select(p => new ProcessDto(p.Pid, p.Name, p == suggested, p == suggested ? listing.Suggestion.Reason : null))
                .ToList();
            return new ProcessListDto(processes, suggested?.Pid, listing.Suggestion.Reason);
        });

        // Name, path and title ID of the process the user chose.
        app.MapGet("/api/processes/{pid:int}", (int pid, ConnectionManager connection) =>
            connection.GetProcessInfo(pid));

        // Resumes a stopped process. Scans with pauseGame resume the game themselves; this is for a
        // game left paused (e.g. the connection dropped mid-scan). 200 with {"pid", "resumed": true}.
        app.MapPost("/api/processes/{pid:int}/resume", (int pid, ConnectionManager connection) =>
        {
            connection.ResumeProcess(pid);
            return new { pid, resumed = true };
        });

        // ?filter=anon,exe   name filter instead of the default ("" = any name)
        // ?includeReadOnly=true   also select read-only sections
        // ?all=true   select every section
        app.MapGet("/api/processes/{pid:int}/maps", (int pid, string? filter, bool? includeReadOnly, bool? all,
            ConnectionManager connection, PS4MemSettings defaults) =>
        {
            var nameFilter = filter?.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var options = new SectionOptions(nameFilter, includeReadOnly ?? false, all ?? false);
            var selection = connection.GetSections(pid, options);
            return ToDto(pid, options, selection, defaults);
        });
    }

    private static SectionListDto ToDto(int pid, SectionOptions options, SectionSelection selection, PS4MemSettings defaults) =>
        new(pid,
            options.NameFilter ?? defaults.SectionNameFilter,
            options.IncludeReadOnly,
            selection.Sections.Select(ToDto).ToList(),
            selection.SelectedCount,
            selection.TotalCount,
            selection.SelectedBytes,
            selection.TotalBytes,
            selection.SkippedMaps);

    private static SectionDto ToDto(Section s) =>
        new(s.Name, s.DisplayName, Hex(s.Start), Hex(s.End), s.Size, Hex(s.Map.Offset),
            s.Map.Prot, s.Map.ProtText, s.Selected, s.Reason);

    private static string Hex(ulong value) => $"0x{value:X}";
}
