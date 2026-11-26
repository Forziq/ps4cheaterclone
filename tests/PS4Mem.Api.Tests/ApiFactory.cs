using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PS4Mem.Core.Connection;

namespace PS4Mem.Api.Tests;

/// <summary>
/// The real API in memory, with its settings file in a temp folder so tests never touch
/// ~/Library/Application Support/PS4Mem. The console IP only comes from requests and the
/// saved settings (never PS4_IP or the repo's ps4ip.local).
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public string TempDir { get; } = Directory.CreateTempSubdirectory("ps4mem-api-").FullName;
    public string SettingsPath => Path.Combine(TempDir, "settings.json");

    /// <summary>Writes a settings file before the app starts (e.g. the fake console's port).</summary>
    public ApiFactory WithSettingsFile(string json)
    {
        File.WriteAllText(SettingsPath, json);
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("PS4Mem:SettingsPath", SettingsPath);
        builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton(IpSources.None)));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(TempDir))
            Directory.Delete(TempDir, recursive: true);
    }
}
