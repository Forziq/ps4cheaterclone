using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;
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

/// <summary>What a cheat write did: the cheat (with its new value, if one was given) and the value it replaced.</summary>
public sealed record CheatWrite(Cheat Cheat, string OldValue, CheatLockState? Lock);

/// <summary>How an import treats the list already saved.</summary>
public enum CheatImportMode
{
    /// <summary>Keep the saved cheats; add the imported ones. One with the id of a saved cheat replaces it, so importing a file twice adds nothing.</summary>
    Merge,
    /// <summary>The imported file becomes the whole list.</summary>
    Replace,
}

/// <summary>What an import did.</summary>
public sealed record CheatImport(CheatTable Table, int Added, int Replaced, int Removed);

/// <summary>A cheat's current value in memory.</summary>
public sealed record CheatValue(string Id, string Value);

/// <summary>
/// The cheat lists per game (title ID): list, add, edit, delete, write and lock. Each change is
/// saved at once. Changes are serialised, so two quick edits can't lose each other. Memory is only
/// written by <see cref="Write"/> and <see cref="Lock"/>, which the API calls on a click.
/// An edit or delete of a locked cheat updates or stops its lock.
/// </summary>
public sealed class CheatList(CheatStore store, CheatLocks? locks = null, DebugLogSwitch? logs = null)
{
    private const string Cat = "cheats";
    private readonly Lock _gate = new();
    private readonly DebugLogSwitch _logs = logs ?? new DebugLogSwitch();

    public CheatStore Store => store;

    public CheatLocks? Locks => locks;

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
            locks?.Update(table.TitleId, cheat);
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
            locks?.Unlock(table.TitleId, id);
            store.Save(table with { Cheats = table.Cheats.Where(c => c.Id != id).ToList() });
            Log.Info(Cat, $"{table.TitleId}: deleted {Describe(cheat)}");
            return cheat;
        }
    }

    /// <summary>
    /// Writes a cheat's value into process <paramref name="pid"/> (the user clicked Write or edited
    /// the value inline). A <paramref name="value"/> is saved as the cheat's value first. A locked
    /// cheat goes on writing the new value.
    /// </summary>
    /// <exception cref="CheatNotFoundException">No cheat with this id.</exception>
    public CheatWrite Write(string titleId, string id, int pid, IProcessMemory memory, string? value = null, bool hex = false)
    {
        var cheat = value is null ? Find(titleId, id) : Update(titleId, id, new CheatInput(Value: value, Hex: hex));
        var title = CheatStore.NormalizeTitleId(titleId);
        var old = new MemoryEditor(memory, Log).Write(pid, cheat.Address, cheat.ValueBytes(), $"cheat {title} {cheat.Id}");
        Log.Info(Cat, $"{title}: wrote {Describe(cheat)} in pid {pid}");
        return new CheatWrite(cheat, Format(cheat.Type, old), locks?.Find(title, id));
    }

    /// <summary>Locks a cheat in process <paramref name="pid"/>: written now, then every lock interval until unlocked.</summary>
    /// <exception cref="InvalidOperationException">This list has no lock loop.</exception>
    public CheatLockState Lock(string titleId, string id, int pid, IProcessMemory memory)
    {
        var l = locks ?? throw new InvalidOperationException("Locks aren't available.");
        var cheat = Find(titleId, id);
        return l.Lock(CheatStore.NormalizeTitleId(titleId), cheat, pid, memory);
    }

    /// <summary>Stops a lock. False when the cheat wasn't locked (not an error).</summary>
    /// <exception cref="CheatNotFoundException">No cheat with this id.</exception>
    public bool Unlock(string titleId, string id)
    {
        var cheat = Find(titleId, id);
        return locks?.Unlock(CheatStore.NormalizeTitleId(titleId), cheat.Id) ?? false;
    }

    /// <summary>The lock of a cheat (or a stopped lock's error), or null.</summary>
    public CheatLockState? LockOf(string titleId, string id) => locks?.Find(CheatStore.NormalizeTitleId(titleId), id);

    /// <summary>Reads every cheat's current value from process <paramref name="pid"/> (reads only).</summary>
    public IReadOnlyList<CheatValue> ReadValues(string titleId, int pid, IProcessMemory memory, CancellationToken ct = default)
    {
        var table = Get(titleId);
        var values = new List<CheatValue>(table.Cheats.Count);
        foreach (var c in table.Cheats)
        {
            var bytes = new byte[c.ValueBytes().Length];
            memory.ReadMemory(pid, c.Address, bytes, ct);
            values.Add(new CheatValue(c.Id, Format(c.Type, bytes)));
        }
        return values;
    }

    /// <summary>The saved list as a cheat file (<see cref="CheatFormat"/>), for export.</summary>
    public string Export(string titleId) => CheatFormat.Serialize(Get(titleId));

    /// <summary>
    /// Imports a cheat file into a game's list and saves it. Every cheat is checked first; a bad
    /// one stops the import and nothing changes. Locks of removed cheats stop; locks of replaced
    /// ones go on with the imported value. Writes no memory.
    /// </summary>
    /// <exception cref="FormatException">Not a cheat file, a bad cheat, or another game's title ID.</exception>
    public CheatImport Import(string titleId, string json, CheatImportMode mode)
    {
        var title = CheatStore.NormalizeTitleId(titleId);
        var imported = CheatFormat.Parse(json, title);
        lock (_gate)
        {
            var table = store.Load(title);
            var savedIds = table.Cheats.Select(c => c.Id).ToHashSet();
            var importedIds = imported.Cheats.Select(c => c.Id).ToHashSet();
            var replaced = imported.Cheats.Count(c => savedIds.Contains(c.Id));
            var added = imported.Cheats.Count - replaced;

            List<Cheat> cheats;
            if (mode == CheatImportMode.Replace)
                cheats = imported.Cheats.ToList();
            else
            {
                var byId = imported.Cheats.ToDictionary(c => c.Id);
                cheats = table.Cheats.Select(c => byId.GetValueOrDefault(c.Id, c)).ToList();
                cheats.AddRange(imported.Cheats.Where(c => !savedIds.Contains(c.Id)));
            }
            var removed = table.Cheats.Where(c => !cheats.Any(n => n.Id == c.Id)).ToList();

            var result = table with { Game = imported.Game ?? table.Game, Cheats = cheats };
            store.Save(result);
            foreach (var c in removed) locks?.Unlock(title, c.Id);
            foreach (var c in imported.Cheats.Where(c => savedIds.Contains(c.Id))) locks?.Update(title, c);
            Log.Info(Cat, $"{title}: imported {imported.Cheats.Count} cheats ({mode.ToString().ToLowerInvariant()}): " +
                          $"{added} added, {replaced} replaced, {removed.Count} removed");
            return new CheatImport(result, added, replaced, removed.Count);
        }
    }

    private Cheat Find(string titleId, string id)
    {
        var table = Get(titleId);
        return table.Find(id) ?? throw new CheatNotFoundException(table.TitleId, id);
    }

    private static string Format(ScanValueType type, byte[] bytes) => ValueFormatter.Format(type, bytes);

    private static ulong? ParseStart(string? text) => string.IsNullOrWhiteSpace(text) ? null : HexAddress.Parse(text);

    internal static string Describe(Cheat c) =>
        $"{c.Id} '{c.Description}' {HexAddress.Format(c.Address)} {c.Type.ShortName()} = {c.Value}";
}
