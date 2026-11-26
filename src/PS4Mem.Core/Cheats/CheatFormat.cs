using System.Text.Json;
using System.Text.Json.Serialization;
using PS4Mem.Core.Scanning;

namespace PS4Mem.Core.Cheats;

/// <summary>
/// The cheat file format, used for the saved tables and for import/export:
/// <code>
/// {
///   "format": 1,
///   "titleId": "CUSA11711",
///   "game": "ac3_engine_orbis_fn.elf",
///   "cheats": [
///     { "id": "1f3a9c20", "description": "Money", "address": "0x2001A3F10", "type": "u32",
///       "value": "999999", "section": "", "sectionStart": "0x200000000" }
///   ]
/// }
/// </code>
/// Addresses are hex strings. Types are the scan type names (u8 ... u64, i8 ... i64, f32, f64,
/// string, bytes). Only <c>address</c>, <c>type</c> and <c>value</c> are required in a cheat.
/// </summary>
public static class CheatFormat
{
    public const int CurrentFormat = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed record FileDto(int Format, string? TitleId, string? Game, List<EntryDto>? Cheats);

    public sealed record EntryDto(
        string? Id, string? Description, string? Address, string? Type, string? Value,
        string? Section = null, string? SectionStart = null);

    public static string Serialize(CheatTable table) =>
        JsonSerializer.Serialize(new FileDto(CurrentFormat, table.TitleId, table.Game,
            table.Cheats.Select(ToDto).ToList()), Json);

    public static EntryDto ToDto(Cheat c) => new(c.Id, c.Description, HexAddress.Format(c.Address),
        c.Type.ShortName(), c.Value, c.Section, c.SectionStart is { } s ? HexAddress.Format(s) : null);

    /// <summary>
    /// Reads a cheat file. Every cheat is checked (address, type, value); the first bad one stops
    /// the parse with a message naming it. Missing or repeated ids get new ones.
    /// </summary>
    /// <param name="titleId">The table's title ID; a file with another title ID is refused, one without any gets this one.</param>
    /// <exception cref="FormatException">Not a cheat file, or a cheat in it is bad.</exception>
    public static CheatTable Parse(string json, string titleId)
    {
        FileDto? file;
        try
        {
            file = JsonSerializer.Deserialize<FileDto>(json, Json);
        }
        catch (JsonException e)
        {
            throw new FormatException($"Not a cheat file: {e.Message}");
        }
        if (file is null)
            throw new FormatException("Not a cheat file: it is empty.");
        if (file.Format > CurrentFormat)
            throw new FormatException($"This cheat file is format {file.Format}; this version of PS4 Mem reads format {CurrentFormat}. Update PS4 Mem.");
        if (!string.IsNullOrWhiteSpace(file.TitleId) && CheatStore.NormalizeTitleId(file.TitleId) != titleId)
            throw new FormatException($"This cheat file is for {file.TitleId.Trim()}, not {titleId}.");

        var cheats = new List<Cheat>();
        var ids = new HashSet<string>();
        foreach (var (entry, i) in (file.Cheats ?? []).Select((e, i) => (e, i)))
        {
            Cheat cheat;
            try
            {
                cheat = FromDto(entry ?? throw new FormatException("it is empty."));
            }
            catch (Exception e) when (e is FormatException or ArgumentException)
            {
                var name = string.IsNullOrWhiteSpace(entry?.Description) ? "" : $" ('{entry.Description}')";
                throw new FormatException($"Cheat {i + 1}{name}: {e.Message}");
            }
            if (!ids.Add(cheat.Id))
            {
                cheat = cheat with { Id = NewId(ids) };
                ids.Add(cheat.Id);
            }
            cheats.Add(cheat);
        }
        return new CheatTable(titleId, Blank(file.Game), cheats);
    }

    /// <summary>Checks one cheat from a file. A missing id gets a new one (the caller makes it unique).</summary>
    public static Cheat FromDto(EntryDto e)
    {
        if (string.IsNullOrWhiteSpace(e.Address)) throw new FormatException("the address is missing.");
        if (string.IsNullOrWhiteSpace(e.Type)) throw new FormatException("the type is missing.");
        var type = ScanNames.ParseType(e.Type);
        var value = CheatValues.Normalize(type, e.Value);
        return new Cheat(
            string.IsNullOrWhiteSpace(e.Id) ? NewId() : e.Id.Trim(),
            e.Description?.Trim() ?? "",
            HexAddress.Parse(e.Address),
            type,
            value,
            e.Section,
            string.IsNullOrWhiteSpace(e.SectionStart) ? null : HexAddress.Parse(e.SectionStart));
    }

    /// <summary>A new 8-character id that isn't in <paramref name="taken"/>.</summary>
    public static string NewId(IReadOnlySet<string>? taken = null)
    {
        while (true)
        {
            var id = Guid.NewGuid().ToString("N")[..8];
            if (taken is null || !taken.Contains(id)) return id;
        }
    }

    internal static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
