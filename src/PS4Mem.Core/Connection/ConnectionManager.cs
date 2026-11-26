using PS4Mem.Core.Logging;
using PS4Mem.Core.Payload;
using PS4Mem.Core.Protocol;
using PS4Mem.Core.Scanning;
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

/// <summary>What <c>POST /api/inject</c> did: the payload sent, then the connection made.</summary>
public sealed record InjectResult(PayloadInjection Injection, ConnectionStatus Status);

/// <summary>How to build the sections list; null fields use the settings' defaults.</summary>
public sealed record SectionOptions(IReadOnlyList<string>? NameFilter = null, bool IncludeReadOnly = false, bool SelectAll = false);

/// <summary>
/// The one ps4debug connection of the app (a singleton in the API). Connect and disconnect are
/// serialised; commands run on <see cref="PS4DebugClient"/>, which has its own socket lock.
/// The process picker and section filter only suggest: the caller always passes the pid the user chose.
/// Disconnect releases everything this manager holds and raises <see cref="Releasing"/> first,
/// so scans (and later locks) stop before the socket closes.
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
    private (ISectionScanner? Scanner, string Reason)? _consoleScanner; // per connection, made on first use
    private int _injecting; // 1 while an injection runs (it waits up to 20 s, outside the lock)

    public ConnectionManager(AppSettingsStore store, IpSources ipSources, DebugLogSwitch logs, PS4MemSettings? defaults = null)
    {
        _store = store;
        _ipSources = ipSources;
        _logs = logs;
        _defaults = defaults ?? PS4MemSettings.Default;
    }

    private DebugLog Log => _logs.Current;

    /// <summary>
    /// Raised on disconnect and when the connection is lost, before the client closes (so a
    /// handler can still send a last command). Runs with the connection lock held: handlers
    /// must not call back into this manager.
    /// </summary>
    public event Action? Releasing;

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
            return ConnectTo(RequireIp(ip));
        }
    }

    /// <summary>
    /// Sends the payload to the console's loader, waits for ps4debug to answer, then connects.
    /// The IP is found as for <see cref="Connect"/>. A <paramref name="payloadPath"/> or
    /// <paramref name="loaderPort"/> that the loader accepted is saved in the settings; null uses
    /// the saved ones. Refuses when connected or when ps4debug already answers (a second copy can
    /// freeze the console).
    /// </summary>
    /// <exception cref="PayloadException">The injection failed (see <see cref="PayloadErrorKind"/>).</exception>
    /// <exception cref="ConnectionStateException">Another injection is running.</exception>
    public async Task<InjectResult> InjectAsync(
        string? ip = null, string? payloadPath = null, int? loaderPort = null,
        Action<string>? report = null, CancellationToken ct = default)
    {
        ResolvedConsoleIp resolved;
        PS4MemSettings settings;
        lock (_gate)
        {
            DropIfLost();
            if (_connected is not null)
                throw new PayloadException(PayloadErrorKind.AlreadyRunning,
                    $"Already connected to {_connected.Ip}:{_connected.Port}, so the payload is running. No need to inject it again.");
            resolved = RequireIp(ip);
            settings = CurrentSettings with { ConsoleIp = resolved.Ip };
        }

        if (Interlocked.Exchange(ref _injecting, 1) == 1)
            throw new ConnectionStateException("A payload injection is already running. Wait for it to finish.");
        try
        {
            var log = Log;
            log.Info(Cat, $"Inject the payload into {resolved.Ip} (IP from {resolved.Source})");
            PayloadInjection injection;
            try
            {
                injection = await new PayloadInjector(settings, log).InjectAsync(resolved.Ip, payloadPath, loaderPort, report, ct);
            }
            catch (PayloadException e)
            {
                // NoAnswer means the loader took the file, so the path and port were right.
                if (e.Kind == PayloadErrorKind.NoAnswer)
                    RememberPayload(payloadPath, loaderPort);
                lock (_gate) _lastError = e.Message;
                throw;
            }

            RememberPayload(payloadPath, loaderPort);
            report?.Invoke($"ps4debug answered after {injection.WaitedForStart.TotalSeconds:0.0} s. Connecting ...");
            lock (_gate)
            {
                DropIfLost();
                if (_connected is not null) // someone pressed Connect while we waited
                    return new InjectResult(injection, _connected);
                return new InjectResult(injection, ConnectTo(resolved));
            }
        }
        finally
        {
            Volatile.Write(ref _injecting, 0);
        }
    }

    private void RememberPayload(string? payloadPath, int? loaderPort)
    {
        if (payloadPath is null && loaderPort is null) return;
        _store.Update(new AppSettingsUpdate { PayloadPath = payloadPath, LoaderPort = loaderPort });
    }

    private ResolvedConsoleIp RequireIp(string? ip) =>
        ResolveIp(ip) ?? throw new ArgumentException(
            "The PS4 IP is not set. Enter it in the app, set PS4_IP, or put it in ps4ip.local.");

    /// <summary>Connects to an already resolved IP. Call with <see cref="_gate"/> held and not connected.</summary>
    private ConnectionStatus ConnectTo(ResolvedConsoleIp resolved)
    {
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

    /// <summary>
    /// What a scan needs from this connection. The console scanner is created (it authenticates
    /// with ps4debug-NG and checks for Turbo Scan) on first use and kept for the connection.
    /// </summary>
    public ScanTarget GetScanTarget()
    {
        var (client, settings) = Session();
        return new ScanTarget(client, settings, Log, () => ConsoleScanner(client));
    }

    private (ISectionScanner? Scanner, string Reason) ConsoleScanner(PS4DebugClient client)
    {
        lock (_gate)
        {
            DropIfLost();
            if (!ReferenceEquals(client, _client) || _sessionSettings is null)
                throw ConnectionStateException.NotConnected();
            // TryCreate may reconnect the client (the server closes on an unknown command),
            // so it runs under the lock where DropIfLost can't see the gap.
            if (_consoleScanner is null)
            {
                var (scanner, reason) = ConsoleSectionScanner.TryCreate(client, _sessionSettings, Log);
                _consoleScanner = (scanner, reason);
            }
            return _consoleScanner.Value;
        }
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
        try
        {
            Releasing?.Invoke();
        }
        catch (Exception e)
        {
            Log.Error(Cat, "Releasing the connection's scans failed", e);
        }
        _consoleScanner = null;
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
