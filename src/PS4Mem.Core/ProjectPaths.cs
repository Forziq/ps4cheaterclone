namespace PS4Mem.Core;

/// <summary>
/// Resolves relative paths such as the default payload (<c>payloads/...</c>) and the log folder
/// (<c>logs</c>). They belong to the project folder, the one that holds <see cref="MarkerFile"/>.
/// The working folder alone isn't enough: <c>dotnet run --project src/PS4Mem.Api</c> runs the API
/// from src/PS4Mem.Api. Outside a project folder, relative paths use the working folder.
/// </summary>
public static class ProjectPaths
{
    /// <summary>The file that marks the project folder.</summary>
    public const string MarkerFile = "PS4Mem.sln";

    /// <summary>The nearest folder holding <see cref="MarkerFile"/>: <paramref name="startDirectory"/> or a parent.</summary>
    public static string? FindRoot(string startDirectory)
    {
        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, MarkerFile)))
                return dir.FullName;
        }
        return null;
    }

    /// <summary>
    /// A file to read. An absolute path is kept. A relative one is taken from the working folder if
    /// the file is there, otherwise from the project folder (also when the file is missing, so the
    /// error names the place it should be).
    /// </summary>
    public static string ResolveFile(string path, string? startDirectory = null)
    {
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);

        var start = startDirectory ?? Directory.GetCurrentDirectory();
        var here = Path.GetFullPath(Path.Combine(start, path));
        if (File.Exists(here)) return here;

        var root = FindRoot(start);
        return root is null ? here : Path.GetFullPath(Path.Combine(root, path));
    }

    /// <summary>A folder to write into. An absolute path is kept; a relative one goes in the project folder.</summary>
    public static string ResolveDirectory(string path, string? startDirectory = null)
    {
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);

        var start = startDirectory ?? Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(FindRoot(start) ?? start, path));
    }
}
