using System.Text.RegularExpressions;
using PS4Mem.Core.Logging;

namespace PS4Mem.Core.Cheats;

/// <summary>
/// The cheat tables on disk: one <c>&lt;TITLEID&gt;.json</c> per game in <see cref="Root"/>
/// (format: <see cref="CheatFormat"/>). Files are read on every load, so a file edited by hand is
/// picked up, and written atomically (temp file, then rename). A file that can't be read is
/// never overwritten.
/// </summary>
public sealed partial class CheatStore
{
    private const string Cat = "cheats";
    private readonly DebugLogSwitch _logs;

    /// <summary><c>~/Library/Application Support/PS4Mem/cheats</c>.</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support", "PS4Mem", "cheats");

    /// <param name="root">The cheats folder; null or empty uses <see cref="DefaultRoot"/>. Created on the first save.</param>
    public CheatStore(string? root = null, DebugLogSwitch? logs = null)
    {
        Root = Path.GetFullPath(string.IsNullOrEmpty(root) ? DefaultRoot : root);
        _logs = logs ?? new DebugLogSwitch();
    }

    public string Root { get; }

    /// <summary>
    /// Upper-cases and checks a title ID. PS4 title IDs look like <c>CUSA11711</c>; anything made of
    /// letters, digits, <c>-</c> and <c>_</c> (up to 32) is accepted, so it is always a safe file name.
    /// </summary>
    /// <exception cref="ArgumentException">Empty, or other characters.</exception>
    public static string NormalizeTitleId(string? titleId)
    {
        var t = titleId?.Trim().ToUpperInvariant() ?? "";
        if (!TitleIdPattern().IsMatch(t))
            throw new ArgumentException(t.Length == 0
                ? "The title ID is missing. The process info (GET /api/processes/{pid}) has it."
                : $"'{titleId}' isn't a title ID like CUSA11711.");
        return t;
    }

    [GeneratedRegex("^[A-Z0-9_-]{1,32}$")]
    private static partial Regex TitleIdPattern();

    public string PathFor(string titleId) => Path.Combine(Root, NormalizeTitleId(titleId) + ".json");

    /// <summary>The saved table, or an empty one when there is no file yet.</summary>
    /// <exception cref="CheatFileException">The file exists but can't be read.</exception>
    public CheatTable Load(string titleId)
    {
        titleId = NormalizeTitleId(titleId);
        var path = PathFor(titleId);
        if (!File.Exists(path))
            return CheatTable.Empty(titleId);
        try
        {
            return CheatFormat.Parse(File.ReadAllText(path), titleId);
        }
        catch (Exception e) when (e is FormatException or IOException or UnauthorizedAccessException)
        {
            _logs.Current.Error(Cat, $"Can't read {path}", e);
            throw new CheatFileException($"The cheat file {path} can't be read ({e.Message}). Fix or remove it; PS4 Mem won't overwrite it.", e);
        }
    }

    public void Save(CheatTable table)
    {
        var path = PathFor(table.TitleId);
        Directory.CreateDirectory(Root);
        var temp = path + ".tmp";
        File.WriteAllText(temp, CheatFormat.Serialize(table));
        File.Move(temp, path, overwrite: true);
        _logs.Current.Info(Cat, $"Saved {path}: {table.Cheats.Count} cheats");
    }

    /// <summary>Title IDs that have a saved table, sorted.</summary>
    public IReadOnlyList<string> TitleIds()
    {
        if (!Directory.Exists(Root)) return [];
        return Directory.EnumerateFiles(Root, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null && TitleIdPattern().IsMatch(n))
            .Select(n => n!)
            .Order(StringComparer.Ordinal)
            .ToList();
    }
}
