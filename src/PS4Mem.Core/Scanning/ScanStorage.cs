using System.Diagnostics;
using PS4Mem.Core.Logging;

namespace PS4Mem.Core.Scanning;

/// <summary>
/// The folder for scan data too big for memory: spilled results (<see cref="ScanHitBuffer"/>) and
/// unknown-initial snapshots. Each instance owns a subfolder <c>session-&lt;process id&gt;-&lt;id&gt;</c>
/// of <see cref="Root"/> and deletes it on <see cref="Dispose"/> and when the process exits.
/// Subfolders left by runs that crashed (their process is gone) are deleted when a new one starts.
/// Single files are deleted by the results that own them (new scan, undo).
/// </summary>
public sealed class ScanStorage : IDisposable
{
    private const string Cat = "scan";
    private const string Prefix = "session-";

    private readonly DebugLog _log;
    private long _nextFile;
    private bool _disposed;

    /// <summary><c>~/Library/Application Support/PS4Mem/scans</c>.</summary>
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support", "PS4Mem", "scans");

    /// <param name="root">The scans folder; null or empty uses <see cref="DefaultRoot"/>.</param>
    public ScanStorage(string? root = null, DebugLog? log = null)
    {
        _log = log ?? DebugLog.Disabled;
        Root = Path.GetFullPath(string.IsNullOrEmpty(root) ? DefaultRoot : root);
        Directory.CreateDirectory(Root);
        RemoveStale();
        SessionDirectory = Path.Combine(Root, $"{Prefix}{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(SessionDirectory);
        AppDomain.CurrentDomain.ProcessExit += OnExit;
        _log.Info(Cat, $"Scan data folder: {SessionDirectory}");
    }

    public string Root { get; }

    /// <summary>This run's folder; every file this storage hands out is in it.</summary>
    public string SessionDirectory { get; }

    /// <summary>A new, unused file path in <see cref="SessionDirectory"/>, e.g. <c>addresses-000003.bin</c>.</summary>
    public string NewFile(string kind)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Path.Combine(SessionDirectory, $"{kind}-{Interlocked.Increment(ref _nextFile):D6}.bin");
    }

    /// <summary>Deletes a file this storage handed out; failures are logged, not thrown.</summary>
    internal void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn(Cat, $"Couldn't delete {path}: {e.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= OnExit;
        DeleteFolder(SessionDirectory);
    }

    private void OnExit(object? sender, EventArgs e) => Dispose();

    private void RemoveStale()
    {
        foreach (var dir in Directory.EnumerateDirectories(Root, Prefix + "*"))
        {
            var name = Path.GetFileName(dir);
            var dash = name.IndexOf('-', Prefix.Length);
            if (dash < 0 || !int.TryParse(name.AsSpan(Prefix.Length, dash - Prefix.Length), out var pid)) continue;
            if (pid == Environment.ProcessId || IsRunning(pid)) continue;
            _log.Info(Cat, $"Deleting scan data left by an earlier run: {dir}");
            DeleteFolder(dir);
        }
    }

    private void DeleteFolder(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn(Cat, $"Couldn't delete {dir}: {e.Message}");
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
