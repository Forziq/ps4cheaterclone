using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace PS4Mem.Api.Tests;

/// <summary>The options PS4Mem.app starts its bundled backend with: a log folder and its own pid.</summary>
public sealed class BundledLaunchTests
{
    [Fact]
    public async Task LogDirectory_option_puts_the_debug_log_there()
    {
        using var factory = new ApiFactory().WithSettingsFile("""{ "debugLogging": true }""");
        var logs = Path.Combine(factory.TempDir, "app-logs");
        using var configured = factory.WithWebHostBuilder(b => b.UseSetting("PS4Mem:LogDirectory", logs));

        using var http = configured.CreateClient();
        (await http.GetAsync("/api/status")).EnsureSuccessStatusCode();

        var file = Assert.Single(Directory.GetFiles(logs, "ps4mem-*.log"));
        Assert.Contains("API starting", await ReadShared(file));
    }

    [Fact]
    public void IsAlive_is_true_for_this_process_and_false_for_one_that_exited()
    {
        Assert.True(ParentWatch.IsAlive(Environment.ProcessId));

        using var finished = Process.Start("/usr/bin/true")!;
        finished.WaitForExit();
        Assert.False(ParentWatch.IsAlive(finished.Id));
    }

    [Fact]
    public async Task Stops_the_app_when_the_parent_is_gone()
    {
        using var finished = Process.Start("/usr/bin/true")!;
        finished.WaitForExit();
        var lifetime = new FakeLifetime();
        using var watch = new ParentWatch(finished.Id, TimeSpan.FromMilliseconds(20), lifetime,
            NullLogger<ParentWatch>.Instance);

        await watch.StartAsync(CancellationToken.None);
        await lifetime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await watch.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Keeps_running_while_the_parent_lives()
    {
        var lifetime = new FakeLifetime();
        using var watch = new ParentWatch(Environment.ProcessId, TimeSpan.FromMilliseconds(20), lifetime,
            NullLogger<ParentWatch>.Instance);

        await watch.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        await watch.StopAsync(CancellationToken.None);

        Assert.False(lifetime.Stopped.Task.IsCompleted);
    }

    private static async Task<string> ReadShared(string path)
    {
        // The API keeps the log open while it runs.
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Stopped.TrySetResult();
    }
}
