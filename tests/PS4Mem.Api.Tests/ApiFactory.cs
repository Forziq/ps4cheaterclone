using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace PS4Mem.Api.Tests;

/// <summary>
/// The real API in memory, with its settings file in a temp folder so tests never touch
/// ~/Library/Application Support/PS4Mem.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public string TempDir { get; } = Directory.CreateTempSubdirectory("ps4mem-api-").FullName;
    public string SettingsPath => Path.Combine(TempDir, "settings.json");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("PS4Mem:SettingsPath", SettingsPath);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(TempDir))
            Directory.Delete(TempDir, recursive: true);
    }
}
