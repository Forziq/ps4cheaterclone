using PS4Mem.Core.Logging;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Sections;
using PS4Mem.Core.Settings;

namespace PS4Mem.Core.Connection;

/// <summary>Where a connect can find the console IP besides the request and the saved settings.</summary>
/// <param name="EnvironmentIp">The <c>PS4_IP</c> environment variable.</param>
/// <param name="StartDirectory">Where to look for <c>ps4ip.local</c> (this folder and its parents).</param>
public sealed record IpSources(string? EnvironmentIp, string? StartDirectory)
{
    public static IpSources FromEnvironment() => new(
        Environment.GetEnvironmentVariable(ConsoleIpResolver.EnvironmentVariable),
        Directory.GetCurrentDirectory());

    /// <summary>Only the request and the saved settings (tests).</summary>
    public static IpSources None { get; } = new(null, null);
}

/// <summary>What <c>GET /api/status</c> reports.</summary>
/// <param name="DefaultIp">The IP a connect without one would use, and where it comes from (pre-fills the IP field).</param>
/// <param name="LastError">Why the last connect failed or the connection dropped; cleared by the next connect.</param>
public sealed record ConnectionStatus(
    bool Connected,
    string? Ip,
    int? Port,
    string? Version,
    DateTimeOffset? ConnectedAt,
    string? DebugLogPath,
    string? DefaultIp,
    string? DefaultIpSource,
    string? LastError);

/// <summary>The process list with the suggested game. The suggestion is never applied by itself.</summary>
public sealed record ProcessListing(IReadOnlyList<ProcessEntry> Processes, ProcessPick Suggestion);

/// <summary>How to build the sections list; null fields use the settings' defaults.</summary>
public sealed record SectionOptions(IReadOnlyList<string>? NameFilter = null, bool IncludeReadOnly = false, bool SelectAll = false);

/// <summary>
/// The one ps4debug connection of the app (a singleton in the API). Connect and disconnect are
/// serialised; commands run on <see cref="PS4DebugClient"/>, which has its own socket lock.
/// The process picker and section filter only suggest: the caller always passes the pid the user chose.
/// Disconnect releases everything this manager holds (later also locks and scans).
/// </summary>
public sealed class ConnectionManager : IDisposable
{
    private const string Cat = "connection";

    private readonly AppSettingsStore _store;
    private readonly IpSources _ipSources;
    private readonly PS4MemSettings _defaults;
    private readonly DebugLogSwitch _logs;
    private readonly Lock _gate = new();

    private PS4DebugClient? _client;
    private PS4MemSettings? _sessionSettings;
    private ConnectionStatus? _connected;
    private string? _lastError;

    public ConnectionManager(AppSettingsStore store, IpSources ipSources, DebugLogSwitch logs, PS4MemSettings? defaults = null)
    {
        _store = store;
        _ipSources = ipSources;
        _logs = logs;
        _defaults = defaults ?? PS4MemSettings.Default;
    }

    private DebugLog Log => _logs.Current;

    /// <summary>Settings for the next connection: saved app settings on top of the defaults.</summary>
    private PS4MemSettings CurrentSettings => _store.Current.ApplyTo(_defaults);

    public ConnectionStatus Status
    {
        get
        {
            lock (_gate)
            {
                DropIfLost();
                if (_connected is not null) return _connected;

                var (ip, source) = DefaultIp();
                return new ConnectionStatus(false, null, null, null, null, null, ip, source, _lastError);
            }
        }
    }

    /// <summary>
    /// Connects to <paramref name="ip"/>, or if that's empty to the first of: <c>PS4_IP</c>,
    /// <c>ps4ip.local</c>, the saved console IP. Reads the ps4debug version to prove the payload
    /// answers, then remembers the IP in the settings.
    /// </summary>
    /// <exception cref="ArgumentException">No IP anywhere, or not a valid IP.</exception>
    /// <exception cref="ConnectionStateException">Already connected.</exception>
    /// <exception cref="Ps4ProtocolException">The PS4 didn't answer.</exception>
    public ConnectionStatus Connect(string? ip = null)
    {
        lock (_gate)
        {
            DropIfLost();
            if (_connected is not null)
                throw ConnectionStateException.AlreadyConnected($"{_connected.Ip}:{_connected.Port}");

            var resolved = ResolveIp(ip)
                ?? throw new ArgumentException(
                    "The PS4 IP is not set. Enter it in the app, set PS4_IP, or put it in ps4ip.local.");

            var settings = CurrentSettings with { ConsoleIp = resolved.Ip };
            // Logging on/off is set at startup and by PUT /api/settings; connect just uses it.
            var log = Log;
            log.Info(Cat, $"Connect to {resolved.Ip}:{settings.CommandPort} (IP from {resolved.Source})");

            var client = new PS4DebugClient(settings, log);
            try
            {
                client.Connect();
                var version = client.GetVersion();

                _client = client;
                _sessionSettings = settings;
                _lastError = null;
                _connected = new ConnectionStatus(true, resolved.Ip, settings.CommandPort, version,
                    DateTimeOffset.Now, log.FilePath, resolved.Ip, resolved.Source, null);
                log.Info(Cat, $"Connected to {resolved.Ip}:{settings.CommandPort}, ps4debug {version}");
            }
            catch (Exception e)
            {
                client.Dispose();
                _lastError = e.Message;
                log.Error(Cat, $"Connect to {resolved.Ip}:{settings.CommandPort} failed: {e.Message}");
                throw;
            }

            _store.RememberIp(resolved.Ip, _defaults.MaxRecentIps);
            return _connected;
        }
    }

    /// <summary>Closes the connection cleanly. Safe to call when not connected.</summary>
    public void Disconnect()
    {
        lock (_gate)
        {
            if (_client is null) return;
            Log.Info(Cat, $"Disconnect from {_connected?.Ip}:{_connected?.Port}");
            Release();
            _lastError = null;
        }
    }

    /// <summary>Every process, with the suggested game (never chosen by itself).</summary>
    public ProcessListing GetProcesses()
    {
        var (client, settings) = Session();
        var processes = Run(() => client.GetProcessList());
        var pick = GameProcessPicker.Pick(processes, settings, log: Log);
        return new ProcessListing(processes, pick);
    }

    /// <summary>Process info for the pid the user chose (name, path, title ID).</summary>
    public ProcessInfo GetProcessInfo(int pid)
    {
        var (client, _) = Session();
        return Run(() => client.GetProcessInfo(pid));
    }

    /// <summary>The pid's memory maps as the sections list, with the suggested selection and each reason.</summary>
    public SectionSelection GetSections(int pid, SectionOptions? options = null)
    {
        options ??= new SectionOptions();
        var (client, settings) = Session();
        if (options.NameFilter is not null)
            settings = settings with { SectionNameFilter = options.NameFilter };
        if (options.IncludeReadOnly)
            settings = settings with { SectionRequireWritable = false };

        var maps = Run(() => client.GetProcessMaps(pid));
        Log.Info(Cat, $"Sections for pid {pid}");
        var selection = new SectionFilter(settings).Apply(maps, Log);
        return options.SelectAll ? SectionFilter.SelectAll(selection) : selection;
    }

    public void Dispose() => Disconnect();

    // ---- Plumbing --------------------------------------------------------------------------

    private (PS4DebugClient Client, PS4MemSettings Settings) Session()
    {
        lock (_gate)
        {
            DropIfLost();
            if (_client is null || _sessionSettings is null)
                throw ConnectionStateException.NotConnected();
            return (_client, _sessionSettings);
        }
    }

    /// <summary>Runs a command; if it killed the connection, the manager forgets it.</summary>
    private T Run<T>(Func<T> command)
    {
        try
        {
            return command();
        }
        finally
        {
            lock (_gate) DropIfLost();
        }
    }

    /// <summary>The client closes its socket when the stream breaks; then we're disconnected too.</summary>
    private void DropIfLost()
    {
        if (_client is null || _client.IsConnected) return;
        _lastError = "The connection to the PS4 was lost. Is the console still on and the payload running?";
        Log.Warn(Cat, _lastError);
        Release();
    }

    private void Release()
    {
        _client?.Dispose(); // sends CMD_CONSOLE_END when the socket is still open
        _client = null;
        _sessionSettings = null;
        _connected = null;
    }

    private ResolvedConsoleIp? ResolveIp(string? requestIp)
    {
        var resolved = ConsoleIpResolver.Resolve(
            requestIp, _ipSources.EnvironmentIp, _ipSources.StartDirectory, appSettingsPath: null);
        if (!string.IsNullOrWhiteSpace(requestIp) && resolved is not null)
            resolved = resolved with { Source = "the request" };
        if (resolved is null && _store.Current.ConsoleIp.Length > 0)
            resolved = new ResolvedConsoleIp(_store.Current.ConsoleIp, "saved settings");
        return resolved;
    }

    /// <summary>The IP a connect without one would use, or (null, null). A bad source isn't an error here.</summary>
    private (string? Ip, string? Source) DefaultIp()
    {
        try
        {
            return ResolveIp(null) is { } r ? (r.Ip, r.Source) : (null, null);
        }
        catch (ArgumentException e)
        {
            return (null, e.Message);
        }
    }
}
