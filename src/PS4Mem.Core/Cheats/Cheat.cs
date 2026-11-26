using PS4Mem.Core.Scanning;

namespace PS4Mem.Core.Cheats;

/// <summary>
/// One row of the cheat list (PROMPT §5 "Cheat list"): what to write where.
/// <see cref="Value"/> is kept in its canonical text form (<see cref="CheatValues.Normalize"/>),
/// so it always parses back to the same bytes. Whether a cheat is locked isn't stored: a lock
/// only lasts while the app runs and is connected, and only starts on a click.
/// </summary>
/// <param name="Id">Short unique id within the table (8 hex characters).</param>
/// <param name="Section">Name of the section the address was found in (may be empty: unnamed heap).</param>
/// <param name="SectionStart">Start of that section when the cheat was made; with the address it gives the offset.</param>
public sealed record Cheat(
    string Id,
    string Description,
    ulong Address,
    ScanValueType Type,
    string Value,
    string? Section = null,
    ulong? SectionStart = null)
{
    /// <summary>Address minus <see cref="SectionStart"/>, or null without a section start (or an address below it).</summary>
    public ulong? SectionOffset => SectionStart is { } start && Address >= start ? Address - start : null;

    /// <summary>The bytes a write puts in memory.</summary>
    public byte[] ValueBytes() => CheatValues.ToBytes(Type, Value);
}

/// <summary>All the cheats of one game, keyed by its title ID (e.g. <c>CUSA11711</c>).</summary>
/// <param name="Game">The game's name or process name, for people reading the file. Optional.</param>
public sealed record CheatTable(string TitleId, string? Game, IReadOnlyList<Cheat> Cheats)
{
    public static CheatTable Empty(string titleId) => new(titleId, null, []);

    public Cheat? Find(string id) => Cheats.FirstOrDefault(c => c.Id == id);
}

/// <summary>A cheat id that isn't in the table (API: 404).</summary>
public sealed class CheatNotFoundException(string titleId, string id)
    : Exception($"There is no cheat '{id}' in the {titleId} cheat list. Reload the list.");

/// <summary>A cheat file on disk that can't be read (API: 409). It is never overwritten until it's fixed or removed.</summary>
public sealed class CheatFileException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Turns typed cheat values into their canonical text and into bytes.</summary>
public static class CheatValues
{
    /// <summary>
    /// The canonical form of <paramref name="text"/> as a <paramref name="type"/>: numbers in
    /// decimal (hex input is converted), strings as typed, byte arrays as <c>12 34 56</c>.
    /// </summary>
    /// <exception cref="FormatException">Not a valid value, or a byte array with <c>??</c> (a write needs every byte).</exception>
    public static string Normalize(ScanValueType type, string? text, bool hex = false)
    {
        var pattern = ValueParser.Parse(type, text, hex);
        if (pattern.HasWildcards)
            throw new FormatException("A cheat writes every byte, so its value can't have ?? wildcards.");
        return type == ScanValueType.String ? text! : ValueFormatter.Format(type, pattern.Bytes);
    }

    /// <summary>The bytes of a canonical value (see <see cref="Normalize"/>).</summary>
    public static byte[] ToBytes(ScanValueType type, string value) => ValueParser.Parse(type, value).Bytes;
}
