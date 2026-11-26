using PS4Mem.Core;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Settings;

namespace PS4Mem.Api;

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        // The saved settings.
        app.MapGet("/api/settings", (AppSettingsStore store) => store.Current);

        // Partial update: only the fields in the body change. 400 lists every invalid field.
        app.MapPut("/api/settings", (AppSettingsUpdate update, AppSettingsStore store,
            DebugLogSwitch logs, PS4MemSettings defaults) =>
        {
            var saved = store.Update(update);
            // Debug logging on/off takes effect at once.
            logs.Apply(saved.ApplyTo(defaults));
            return saved;
        });
    }
}
