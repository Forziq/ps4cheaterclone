namespace PS4Mem.Core.Logging;

/// <summary>
/// The debug log of a long-running process (the API), which the user can turn on and off
/// while it runs. Components keep the switch and log to <see cref="Current"/>, which is
/// <see cref="DebugLog.Disabled"/> while logging is off. Turning it on starts a new log file.
/// </summary>
public sealed class DebugLogSwitch : IDisposable
{
    private readonly Lock _gate = new();
    private DebugLog _current;

    public DebugLogSwitch(DebugLog? initial = null) => _current = initial ?? DebugLog.Disabled;

    public DebugLog Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>
    /// Opens a log file if <paramref name="settings"/> (or <c>PS4MEM_DEBUG=1</c>) asks for one
    /// and none is open, and closes it when logging is turned off. Returns the current log.
    /// </summary>
    public DebugLog Apply(PS4MemSettings settings)
    {
        lock (_gate)
        {
            var wanted = DebugLog.IsRequested(settings);
            if (wanted && !_current.IsEnabled)
                _current = DebugLog.Create(settings);
            else if (!wanted && _current.IsEnabled)
            {
                _current.Info("log", "Debug logging turned off");
                _current.Dispose();
                _current = DebugLog.Disabled;
            }
            return _current;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _current.Dispose();
            _current = DebugLog.Disabled;
        }
    }
}
