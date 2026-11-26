using PS4Mem.Api;
using PS4Mem.Core;
using PS4Mem.Core.Cheats;
using PS4Mem.Core.Connection;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Scanning;
using PS4Mem.Core.Settings;

// PS4 Mem backend: a minimal API on 127.0.0.1 that the macOS app talks to.
// It holds the ps4debug connection, the scan session and the cheat lists.
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
// The console IP: request, then PS4_IP, then ps4ip.local (from the working folder up), then saved settings.
builder.Services.AddSingleton(_ => IpSources.FromEnvironment());
// The one ps4debug connection. Disposed (cleanly disconnected) when the API stops.
builder.Services.AddSingleton<ConnectionManager>();
// First/next scans, results and undo. Disconnect (or a lost connection) cancels the scan and drops its results.
builder.Services.AddSingleton(sp => new ScanSession(sp.GetRequiredService<PS4MemSettings>()));
// Cheat lists per title ID in ~/Library/Application Support/PS4Mem/cheats (or CheatDirectory).
builder.Services.AddSingleton(sp => new CheatStore(
    sp.GetRequiredService<PS4MemSettings>().CheatDirectory, sp.GetRequiredService<DebugLogSwitch>()));
// Locked cheats: written again every CheatLockInterval (200 ms) until unlocked or disconnected.
builder.Services.AddSingleton(sp => new CheatLocks(sp.GetRequiredService<PS4MemSettings>(), sp.GetRequiredService<DebugLogSwitch>()));
builder.Services.AddSingleton(sp => new CheatList(sp.GetRequiredService<CheatStore>(),
    sp.GetRequiredService<CheatLocks>(), sp.GetRequiredService<DebugLogSwitch>()));

var app = builder.Build();

// The connection releases the scans and stops every cheat lock before it closes.
var scans = app.Services.GetRequiredService<ScanSession>();
var locks = app.Services.GetRequiredService<CheatLocks>();
var connection = app.Services.GetRequiredService<ConnectionManager>();
connection.Releasing += scans.Release;
connection.Releasing += () => locks.StopAll("disconnected");

// Start the debug log now if the saved settings (or PS4MEM_DEBUG=1) ask for one.
var store = app.Services.GetRequiredService<AppSettingsStore>();
var logs = app.Services.GetRequiredService<DebugLogSwitch>();
logs.Apply(store.Current.ApplyTo(defaults)).Info("api", $"API starting on {urls}, settings {store.FilePath}");
if (store.LoadError is not null)
    app.Logger.LogWarning("{Error}", store.LoadError);

app.UseMiddleware<ApiErrors>();
app.MapSettingsEndpoints();
app.MapConnectionEndpoints();
app.MapScanEndpoints();
app.MapCheatEndpoints();

app.Run();

/// <summary>Lets the test project start the app with WebApplicationFactory.</summary>
public partial class Program;
