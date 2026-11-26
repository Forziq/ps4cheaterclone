using PS4Mem.Core.Logging;
using PS4Mem.Core.Scanning;

namespace PS4Mem.Core.Cheats;

/// <summary>
/// What a cheat is made of, as the user typed it. For an add, <c>Address</c>, <c>Type</c> (default
/// u32) and <c>Value</c> are needed; for an edit, null fields stay as they are and an empty
/// <c>Section</c> or <c>SectionStart</c> clears it.
/// </summary>
/// <param name="Hex">Read a number value as hex (raw bits for floats), like the scanner's hex option.</param>
/// <param name="Game">The game's name for the table; only used when the table has none yet.</param>
public sealed record CheatInput(
    string? Description = null,
    string? Address = null,
    string? Type = null,
    string? Value = null,
    bool Hex = false,
    string? Section = null,
    string? SectionStart = null,
    string? Game = null);

/// <summary>
/// The cheat lists per game (title ID): list, add, edit, delete. Each change is saved at once.
/// Changes are serialised, so two quick edits can't lose each other.
/// </summary>
public sealed class CheatList(CheatStore store, DebugLogSwitch? logs = null)
{
    private const string Cat = "cheats";
    private readonly Lock _gate = new();
    private readonly DebugLogSwitch _logs = logs ?? new DebugLogSwitch();

    public CheatStore Store => store;

    private DebugLog Log => _logs.Current;

    public CheatTable Get(string titleId)
    {
        lock (_gate) return store.Load(titleId);
    }

    /// <summary>Adds a cheat and returns it with its new id.</summary>
    /// <exception cref="ArgumentException">Bad title ID, address or type.</exception>
    /// <exception cref="FormatException">The value doesn't fit the type.</exception>
    public Cheat Add(string titleId, CheatInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Address))
            throw new ArgumentException("The address is missing.");
        var type = ScanNames.ParseType(input.Type);
        var cheat = new Cheat(
            "",
            input.Description?.Trim() ?? "",
            HexAddress.Parse(input.Address),
            type,
            CheatValues.Normalize(type, input.Value, input.Hex),
            input.Section,
            ParseStart(input.SectionStart));

        lock (_gate)
        {
            var table = store.Load(titleId);
            cheat = cheat with { Id = CheatFormat.NewId(table.Cheats.Select(c => c.Id).ToHashSet()) };
            store.Save(table with
            {
                Game = table.Game ?? CheatFormat.Blank(input.Game),
                Cheats = [.. table.Cheats, cheat],
            });
            Log.Info(Cat, $"{table.TitleId}: added {Describe(cheat)}");
            return cheat;
        }
    }

    /// <summary>
    /// Changes the given fields of a cheat. A new type without a new value keeps the old value
    /// text if it fits the new type.
    /// </summary>
    /// <exception cref="CheatNotFoundException">No cheat with this id.</exception>
    public Cheat Update(string titleId, string id, CheatInput input)
    {
        lock (_gate)
        {
            var table = store.Load(titleId);
            var old = table.Find(id) ?? throw new CheatNotFoundException(table.TitleId, id);

            var type = input.Type is null ? old.Type : ScanNames.ParseType(input.Type);
            var value = input.Value is not null || type != old.Type
                ? CheatValues.Normalize(type, input.Value ?? old.Value, input.Value is not null && input.Hex)
                : old.Value;
            var cheat = old with
            {
                Description = input.Description?.Trim() ?? old.Description,
                Address = input.Address is null ? old.Address : HexAddress.Parse(input.Address),
                Type = type,
                Value = value,
                Section = input.Section is null ? old.Section : input.Section.Length == 0 ? null : input.Section,
                SectionStart = input.SectionStart is null ? old.SectionStart : ParseStart(input.SectionStart),
            };

            store.Save(table with { Cheats = table.Cheats.Select(c => c.Id == id ? cheat : c).ToList() });
            Log.Info(Cat, $"{table.TitleId}: changed {Describe(old)} to {Describe(cheat)}");
            return cheat;
        }
    }

    /// <exception cref="CheatNotFoundException">No cheat with this id.</exception>
    public Cheat Delete(string titleId, string id)
    {
        lock (_gate)
        {
            var table = store.Load(titleId);
            var cheat = table.Find(id) ?? throw new CheatNotFoundException(table.TitleId, id);
            store.Save(table with { Cheats = table.Cheats.Where(c => c.Id != id).ToList() });
            Log.Info(Cat, $"{table.TitleId}: deleted {Describe(cheat)}");
            return cheat;
        }
    }

    private static ulong? ParseStart(string? text) => string.IsNullOrWhiteSpace(text) ? null : HexAddress.Parse(text);

    internal static string Describe(Cheat c) =>
        $"{c.Id} '{c.Description}' {HexAddress.Format(c.Address)} {c.Type.ShortName()} = {c.Value}";
}
