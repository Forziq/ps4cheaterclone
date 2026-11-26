using System.Diagnostics;

namespace PS4Mem.Api;

/// <summary>
/// Stops the API when the process that started it is gone. PS4Mem.app passes its own pid
/// (<c>--PS4Mem:ParentPid</c>), so the backend also stops when the app is force-quit or crashes
/// and has no chance to stop it. Without a pid (e.g. <c>dotnet run</c> by hand) it does nothing.
/// </summary>
public sealed class ParentWatch(int parentPid, TimeSpan interval, IHostApplicationLifetime lifetime,
    ILogger<ParentWatch> logger) : BackgroundService
{
    public const string ConfigKey = "PS4Mem:ParentPid";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Stopping when process {Pid} exits", parentPid);
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (IsAlive(parentPid)) continue;
                logger.LogWarning("Process {Pid} that started the API is gone; stopping", parentPid);
                lifetime.StopApplication();
                return;
            }
        }
        catch (OperationCanceledException) { } // the API is stopping anyway
    }

    public static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; } // no such process
        catch (InvalidOperationException) { return false; } // it exited while we looked
    }
}
