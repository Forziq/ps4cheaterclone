using System.Text.Json;
using PS4Mem.Core.Cheats;
using PS4Mem.Core.Connection;
using PS4Mem.Core.Logging;
using PS4Mem.Core.Payload;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Scanning;

namespace PS4Mem.Api;

/// <summary>
/// Turns exceptions into short JSON errors the app can show as they are: <c>{"error": "..."}</c>.
/// <list type="bullet">
/// <item>400: bad input (<see cref="ArgumentException"/>, a value that doesn't parse
/// (<see cref="FormatException"/>), bad JSON);</item>
/// <item>409: not connected, or already connected (<see cref="ConnectionStateException"/>), or the
/// payload already runs (<see cref="PayloadErrorKind.AlreadyRunning"/>), or a scan is running or
/// there are no results (<see cref="ScanStateException"/>);</item>
/// <item>400 also for a payload file that is missing or empty (<see cref="PayloadErrorKind.FileProblem"/>);</item>
/// <item>404: a cheat id that isn't in the list (<see cref="CheatNotFoundException"/>);</item>
/// <item>409 also for a cheat file on disk that can't be read (<see cref="CheatFileException"/>);</item>
/// <item>502: the PS4 refused a command or the connection failed (<see cref="Ps4ProtocolException"/>).</item>
/// </list>
/// Anything else is a bug: 500, and the full exception goes to the debug log.
/// </summary>
public sealed class ApiErrors(RequestDelegate next, DebugLogSwitch logs, ILogger<ApiErrors> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception e) when (!context.Response.HasStarted)
        {
            var (status, message) = e switch
            {
                BadHttpRequestException { InnerException: JsonException j } => (400, $"Invalid JSON: {j.Message}"),
                BadHttpRequestException b => (400, b.Message),
                ArgumentException a => (400, a.Message),
                FormatException f => (400, f.Message),
                ConnectionStateException c => (409, c.Message),
                ScanStateException s => (409, s.Message),
                CheatNotFoundException n => (404, n.Message),
                CheatFileException f => (409, f.Message),
                PayloadException { Kind: PayloadErrorKind.AlreadyRunning } p => (409, p.Message),
                PayloadException { Kind: PayloadErrorKind.FileProblem } p => (400, p.Message),
                Ps4ProtocolException p => (502, p.Message),
                _ => (500, $"Unexpected error: {e.Message}"),
            };

            var where = $"{context.Request.Method} {context.Request.Path}{context.Request.QueryString}";
            if (status == 500)
            {
                logs.Current.Error("api", $"{where} failed", e);
                logger.LogError(e, "{Where} failed", where);
            }
            else
                logs.Current.Warn("api", $"{where}: {status} {message}");

            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(new ApiError(message));
        }
    }
}

public sealed record ApiError(string Error);
