using PS4Mem.Api;
using PS4Mem.Core;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Settings;

// PS4 Mem backend: a minimal API on 127.0.0.1 that the macOS app talks to.
// It holds the ps4debug connection (and later scans and cheat tables).
// Run:  dotnet run --project src/PS4Mem.Api       (sample calls: src/PS4Mem.Api/api.http)
// Options (command line or environment):
//   --urls http://127.0.0.1:<port>      listen address; loopback only (default PS4MemSettings.ApiUrl)
//   --PS4Mem:SettingsPath <file>        settings file (default ~/Library/Application Support/PS4Mem/settings.json)

var builder = WebApplication.CreateBuilder(args);
var defaults = PS4MemSettings.Default;

var urls = builder.Configuration["urls"] ?? defaults.ApiUrl;
LoopbackGuard.EnsureLoopback(urls);
builder.WebHost.UseUrls(urls);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = AppSettingsStore.JsonOptions.PropertyNamingPolicy;
    o.SerializerOptions.WriteIndented = true;
});

builder.Services.AddSingleton(defaults);
builder.Services.AddSingleton<DebugLogSwitch>();
builder.Services.AddSingleton(sp => new AppSettingsStore(
    sp.GetRequiredService<IConfiguration>()["PS4Mem:SettingsPath"],
    sp.GetRequiredService<DebugLogSwitch>()));

var app = builder.Build();

// Start the debug log now if the saved settings (or PS4MEM_DEBUG=1) ask for one.
var store = app.Services.GetRequiredService<AppSettingsStore>();
var logs = app.Services.GetRequiredService<DebugLogSwitch>();
logs.Apply(store.Current.ApplyTo(defaults)).Info("api", $"API starting on {urls}, settings {store.FilePath}");
if (store.LoadError is not null)
    app.Logger.LogWarning("{Error}", store.LoadError);

app.UseMiddleware<ApiErrors>();
app.MapSettingsEndpoints();

app.Run();

/// <summary>Lets the test project start the app with WebApplicationFactory.</summary>
public partial class Program;
