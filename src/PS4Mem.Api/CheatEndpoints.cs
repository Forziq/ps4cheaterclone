using PS4Mem.Core;
using PS4Mem.Core.Cheats;
using PS4Mem.Core.Connection;
using PS4Mem.Core.Scanning;

namespace PS4Mem.Api;

/// <summary>
/// Body of <c>POST /api/cheats/{titleId}</c> (add) and <c>PUT /api/cheats/{titleId}/{id}</c> (edit).
/// <list type="bullet">
/// <item><c>address</c>: hex, e.g. "0x2001A3F10" (required to add).</item>
/// <item><c>type</c>: as for scans (u8 ... u64, i8 ... i64, f32, f64, string, bytes); default u32.</item>
/// <item><c>value</c>: as typed (required to add); <c>hex</c> reads a number as hex.</item>
/// <item><c>section</c>, <c>sectionStart</c>: where the address was found (a scan result row has both).</item>
/// <item><c>game</c>: the game's name, saved with the table if it has none yet.</item>
/// </list>
/// For an edit, fields left out stay as they are; "" clears <c>section</c> or <c>sectionStart</c>.
/// </summary>
public sealed record CheatRequest(
    string? Description, string? Address, string? Type, string? Value, bool? Hex,
    string? Section, string? SectionStart, string? Game)
{
    public CheatInput ToInput() => new(Description, Address, Type, Value, Hex ?? false, Section, SectionStart, Game);
}

/// <summary>
/// One cheat. Addresses are hex strings; <c>value</c> is the canonical text (numbers in decimal).
/// <c>locked</c> while its lock runs (in <c>lockPid</c>, <c>lockWrites</c> writes so far);
/// <c>lockError</c> says why a lock stopped by itself (a write failed).
/// </summary>
public sealed record CheatDto(
    string Id, string Description, string Address, string? Section, string? SectionStart, string? SectionOffset,
    string Type, string Value, bool Locked, int? LockPid, long? LockWrites, string? LockError);

/// <summary>Body of <c>POST .../write</c>: the process, and optionally a new value (saved first).</summary>
public sealed record CheatWriteRequest(int? Pid, string? Value, bool? Hex);

/// <summary>Body of <c>POST .../lock</c> and <c>POST /api/cheats/{titleId}/values</c>.</summary>
public sealed record CheatPidRequest(int? Pid);

/// <summary>What a write did: the cheat as it is now and the value it replaced in memory.</summary>
public sealed record CheatWriteDto(CheatDto Cheat, string OldValue);

public sealed record CheatValueDto(string Id, string Value);

/// <summary>Each cheat's current value in memory, in list order.</summary>
public sealed record CheatValuesDto(string TitleId, int Pid, IReadOnlyList<CheatValueDto> Values);

/// <summary>A game's cheat list and the file it is saved in.</summary>
public sealed record CheatTableDto(string TitleId, string? Game, string File, IReadOnlyList<CheatDto> Cheats);

/// <summary>A game with a saved cheat list. <c>error</c> is set (and <c>count</c> 0) when its file can't be read.</summary>
public sealed record CheatGameDto(string TitleId, string? Game, int Count, string? Error);

public sealed record CheatGamesDto(string Folder, IReadOnlyList<CheatGameDto> Games);

public static class CheatEndpoints
{
    public static void MapCheatEndpoints(this IEndpointRouteBuilder app)
    {
        // Every game with a saved cheat list.
        app.MapGet("/api/cheats", (CheatList cheats) =>
        {
            var games = cheats.Store.TitleIds().Select(id =>
            {
                try
                {
                    var t = cheats.Get(id);
                    return new CheatGameDto(t.TitleId, t.Game, t.Cheats.Count, null);
                }
                catch (CheatFileException e)
                {
                    return new CheatGameDto(id, null, 0, e.Message);
                }
            }).ToList();
            return new CheatGamesDto(cheats.Store.Root, games);
        });

        // A game's cheat list (empty if none is saved yet). 400 bad title ID; 409 the file can't be read.
        app.MapGet("/api/cheats/{titleId}", (string titleId, CheatList cheats) => ToDto(cheats.Get(titleId), cheats));

        // 201 with the new cheat; 400 bad input.
        app.MapPost("/api/cheats/{titleId}", (string titleId, CheatRequest? request, CheatList cheats) =>
        {
            var cheat = cheats.Add(titleId, (request ?? Empty).ToInput());
            return Results.Json(ToDto(cheat, cheats.LockOf(titleId, cheat.Id)), statusCode: StatusCodes.Status201Created);
        });

        // 200 with the changed cheat; 400 bad input; 404 no such cheat. Doesn't write memory, but a
        // locked cheat goes on writing its new value.
        app.MapPut("/api/cheats/{titleId}/{id}", (string titleId, string id, CheatRequest? request, CheatList cheats) =>
        {
            var cheat = cheats.Update(titleId, id, (request ?? Empty).ToInput());
            return ToDto(cheat, cheats.LockOf(titleId, id));
        });

        // 204; 404 no such cheat. A locked cheat is unlocked first.
        app.MapDelete("/api/cheats/{titleId}/{id}", (string titleId, string id, CheatList cheats) =>
        {
            cheats.Delete(titleId, id);
            return Results.NoContent();
        });

        // ---- Memory: only on a click in the app ----------------------------------------------

        // Writes the cheat's value into the process (a value in the body is saved first).
        // 200 with the cheat and the old value; 400 no pid or a bad value; 404 no such cheat;
        // 409 not connected; 502 the PS4 refused.
        app.MapPost("/api/cheats/{titleId}/{id}/write", (string titleId, string id, CheatWriteRequest? request,
            CheatList cheats, ConnectionManager connection) =>
        {
            var pid = request?.Pid ?? throw new ArgumentException("pid is required: the process to write to.");
            var memory = connection.GetScanTarget().Memory;
            var write = cheats.Write(titleId, id, pid, memory, request.Value, request.Hex ?? false);
            return new CheatWriteDto(ToDto(write.Cheat, write.Lock), write.OldValue);
        });

        // Locks the cheat: written now, then every ~200 ms until unlock, delete or disconnect.
        // 200 with the cheat (locked); 400 no pid; 404; 409 not connected; 502 the first write failed.
        app.MapPost("/api/cheats/{titleId}/{id}/lock", (string titleId, string id, CheatPidRequest? request,
            CheatList cheats, ConnectionManager connection) =>
        {
            var pid = request?.Pid ?? throw new ArgumentException("pid is required: the process to write to.");
            var memory = connection.GetScanTarget().Memory;
            var state = cheats.Lock(titleId, id, pid, memory);
            return ToDto(cheats.Get(titleId).Find(id)!, state);
        });

        // Stops the lock (fine if it wasn't locked). 200 with the cheat; 404 no such cheat. Needs no connection.
        app.MapPost("/api/cheats/{titleId}/{id}/unlock", (string titleId, string id, CheatList cheats) =>
        {
            cheats.Unlock(titleId, id);
            var table = cheats.Get(titleId);
            return ToDto(table.Find(id) ?? throw new CheatNotFoundException(table.TitleId, id), null);
        });

        // Reads every cheat's current value (reads only). 400 no pid; 409 not connected.
        app.MapPost("/api/cheats/{titleId}/values", (string titleId, CheatPidRequest? request, CheatList cheats,
            ConnectionManager connection, HttpContext http) =>
        {
            var pid = request?.Pid ?? throw new ArgumentException("pid is required: the process to read.");
            var memory = connection.GetScanTarget().Memory;
            var values = cheats.ReadValues(titleId, pid, memory, http.RequestAborted);
            return new CheatValuesDto(CheatStore.NormalizeTitleId(titleId), pid,
                values.Select(v => new CheatValueDto(v.Id, v.Value)).ToList());
        });
    }

    private static readonly CheatRequest Empty = new(null, null, null, null, null, null, null, null);

    internal static CheatTableDto ToDto(CheatTable table, CheatList cheats) =>
        new(table.TitleId, table.Game, cheats.Store.PathFor(table.TitleId),
            table.Cheats.Select(c => ToDto(c, cheats.LockOf(table.TitleId, c.Id))).ToList());

    internal static CheatDto ToDto(Cheat c, CheatLockState? lockState) => new(
        c.Id, c.Description, HexAddress.Format(c.Address), c.Section,
        c.SectionStart is { } s ? HexAddress.Format(s) : null,
        c.SectionOffset is { } o ? HexAddress.Format(o) : null,
        c.Type.ShortName(), c.Value,
        lockState?.Locked ?? false,
        lockState?.Locked == true ? lockState.Pid : null,
        lockState?.Locked == true ? lockState.Writes : null,
        lockState?.Error);
}
