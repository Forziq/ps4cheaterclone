using PS4Mem.Core;

namespace PS4Mem.Core.Tests;

/// <summary>
/// <c>dotnet run --project src/PS4Mem.Api</c> runs the API from src/PS4Mem.Api, so relative
/// defaults like payloads/... and logs must be found from the project folder, not the working folder.
/// </summary>
public sealed class ProjectPathsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ps4mem-paths-").FullName;
    private readonly string _apiDir;

    public ProjectPathsTests()
    {
        File.WriteAllText(Path.Combine(_root, ProjectPaths.MarkerFile), "");
        _apiDir = Directory.CreateDirectory(Path.Combine(_root, "src", "PS4Mem.Api")).FullName;
        Directory.CreateDirectory(Path.Combine(_root, "payloads"));
        File.WriteAllBytes(Path.Combine(_root, "payloads", "p.bin"), [1]);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Finds_the_project_folder_from_a_subfolder()
    {
        Assert.Equal(_root, ProjectPaths.FindRoot(_apiDir));
    }

    [Fact]
    public void No_project_folder_outside_the_project()
    {
        var outside = Directory.CreateTempSubdirectory("ps4mem-outside-").FullName;
        try { Assert.Null(ProjectPaths.FindRoot(outside)); }
        finally { Directory.Delete(outside); }
    }

    [Fact]
    public void Relative_file_is_found_in_the_project_folder_when_run_from_src()
    {
        var path = ProjectPaths.ResolveFile("payloads/p.bin", _apiDir);
        Assert.Equal(Path.Combine(_root, "payloads", "p.bin"), path);
    }

    [Fact]
    public void Relative_file_in_the_working_folder_wins()
    {
        Directory.CreateDirectory(Path.Combine(_apiDir, "payloads"));
        var here = Path.Combine(_apiDir, "payloads", "p.bin");
        File.WriteAllBytes(here, [2]);

        Assert.Equal(here, ProjectPaths.ResolveFile("payloads/p.bin", _apiDir));
    }

    [Fact]
    public void Missing_relative_file_points_at_the_project_folder()
    {
        var path = ProjectPaths.ResolveFile("payloads/missing.bin", _apiDir);
        Assert.Equal(Path.Combine(_root, "payloads", "missing.bin"), path);
    }

    [Fact]
    public void Absolute_file_is_kept()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "somewhere", "p.bin");
        Assert.Equal(absolute, ProjectPaths.ResolveFile(absolute, _apiDir));
    }

    [Fact]
    public void Relative_file_outside_a_project_uses_the_working_folder()
    {
        var outside = Directory.CreateTempSubdirectory("ps4mem-outside-").FullName;
        try
        {
            Assert.Equal(Path.Combine(outside, "payloads", "p.bin"), ProjectPaths.ResolveFile("payloads/p.bin", outside));
        }
        finally { Directory.Delete(outside); }
    }

    [Fact]
    public void Relative_folder_goes_in_the_project_folder()
    {
        Assert.Equal(Path.Combine(_root, "logs"), ProjectPaths.ResolveDirectory("logs", _apiDir));
    }

    [Fact]
    public void Relative_folder_outside_a_project_uses_the_working_folder()
    {
        var outside = Directory.CreateTempSubdirectory("ps4mem-outside-").FullName;
        try { Assert.Equal(Path.Combine(outside, "logs"), ProjectPaths.ResolveDirectory("logs", outside)); }
        finally { Directory.Delete(outside); }
    }

    [Fact]
    public void Default_payload_path_resolves_to_the_real_file_in_this_repo()
    {
        // The test runs from tests/.../bin/..., deep inside the repo, like the API runs from src/PS4Mem.Api.
        var root = ProjectPaths.FindRoot(AppContext.BaseDirectory);
        Assert.NotNull(root);
        var path = ProjectPaths.ResolveFile(PS4MemSettings.Default.PayloadPath, AppContext.BaseDirectory);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, PS4MemSettings.Default.PayloadPath)), path);
    }
}
