using PS4Mem.Core;
using PS4Mem.Core.Cheats;
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

/// <summary>One cheat. Addresses are hex strings; <c>value</c> is the canonical text (numbers in decimal).</summary>
public sealed record CheatDto(
    string Id, string Description, string Address, string? Section, string? SectionStart, string? SectionOffset,
    string Type, string Value);

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
        app.MapGet("/api/cheats/{titleId}", (string titleId, CheatList cheats) => ToDto(cheats.Get(titleId), cheats.Store));

        // 201 with the new cheat; 400 bad input.
        app.MapPost("/api/cheats/{titleId}", (string titleId, CheatRequest? request, CheatList cheats) =>
        {
            var cheat = cheats.Add(titleId, (request ?? Empty).ToInput());
            return Results.Json(ToDto(cheat), statusCode: StatusCodes.Status201Created);
        });

        // 200 with the changed cheat; 400 bad input; 404 no such cheat.
        app.MapPut("/api/cheats/{titleId}/{id}", (string titleId, string id, CheatRequest? request, CheatList cheats) =>
            ToDto(cheats.Update(titleId, id, (request ?? Empty).ToInput())));

        // 204; 404 no such cheat.
        app.MapDelete("/api/cheats/{titleId}/{id}", (string titleId, string id, CheatList cheats) =>
        {
            cheats.Delete(titleId, id);
            return Results.NoContent();
        });
    }

    private static readonly CheatRequest Empty = new(null, null, null, null, null, null, null, null);

    internal static CheatTableDto ToDto(CheatTable table, CheatStore store) =>
        new(table.TitleId, table.Game, store.PathFor(table.TitleId), table.Cheats.Select(ToDto).ToList());

    internal static CheatDto ToDto(Cheat c) => new(
        c.Id, c.Description, HexAddress.Format(c.Address), c.Section,
        c.SectionStart is { } s ? HexAddress.Format(s) : null,
        c.SectionOffset is { } o ? HexAddress.Format(o) : null,
        c.Type.ShortName(), c.Value);
}
