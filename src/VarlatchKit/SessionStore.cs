namespace VarlatchKit;

/// <summary>Whether the CLI can be used, and if not, why.</summary>
public abstract record CliState
{
    /// <summary>Not read yet.</summary>
    public sealed record Unknown : CliState;

    /// <summary>Not in any of the places the app looks.</summary>
    public sealed record Missing(IReadOnlyList<string> Searched) : CliState
    {
        public bool Equals(Missing? other) => other is not null && Searched.SequenceEqual(other.Searched);
        public override int GetHashCode() => Searched.Count;
    }

    /// <summary>The path set in Settings cannot run.</summary>
    public sealed record PathUnusable(string Path) : CliState;

    /// <summary>
    /// The CLI runs on Node.js, which is not installed, or (with
    /// <paramref name="Found"/>, the version found) too old.
    /// </summary>
    public sealed record NodeMissing(string Path, string? Found = null) : CliState;

    /// <summary>Too old to have <c>status</c>.</summary>
    public sealed record Unsupported : CliState;

    /// <summary>It ran but failed, or printed something unreadable.</summary>
    public sealed record Unavailable(string Detail) : CliState;

    public sealed record Ready : CliState;
}

/// <summary>
/// The sessions the panel and the tray icon show, from polling
/// <c>varlatch status --json</c>. That command reads local files only, so
/// polling makes no network requests and never uses a stored credential.
/// </summary>
/// <remarks>
/// Made and used on the UI thread, whose synchronization context its timer
/// posts to.
/// </remarks>
public sealed class SessionStore : ObservableObject
{
    /// <summary>The oldest Node.js the release CLI runs on.</summary>
    public const int MinimumNodeMajor = 22;

    public static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan LogoutTimeout = TimeSpan.FromSeconds(60);

    private readonly SystemContext system;
    private readonly string? memoryPath;
    private readonly Func<DateTimeOffset> clock;
    private readonly ExpiryTracker tracker = new();
    private readonly object gate = new();
    private SynchronizationContext? context;
    private Timer? timer;
    private bool reading;
    private bool readQueued;

    private IReadOnlyList<ServerStatus> allServers = [];
    private CliState cliState = new CliState.Unknown();
    private CliCommand? cliCommand;
    private ReleaseVersion? cliVersion;
    private string? nodeVersion;
    private DateTimeOffset? lastRead;
    private Preferences preferences;
    private ServerMemory memory;
    private bool verifying;
    private string? verifyError;
    private IReadOnlyDictionary<string, ProbeResult> probes = new Dictionary<string, ProbeResult>();

    public sealed record ProbeResult(Probe Probe, string? CredentialId);

    /// <param name="stateDirectory">Where to keep the servers seen before; null keeps them in memory only.</param>
    public SessionStore(Preferences preferences, SystemContext system, string? stateDirectory = null,
                        Func<DateTimeOffset>? clock = null)
    {
        this.preferences = preferences;
        this.system = system;
        this.clock = clock ?? (() => DateTimeOffset.Now);
        memoryPath = stateDirectory is null ? null : Path.Combine(stateDirectory, "servers.json");
        memory = memoryPath is null ? new ServerMemory() : ServerMemory.Load(memoryPath);
    }

    /// <summary>Called with each move into <i>expiring</i> or <i>expired</i>, whatever the notification setting says.</summary>
    public event Action<IReadOnlyList<ExpiryEvent>>? ExpiryEvents;

    /// <summary>Every stored credential, localhost ones included.</summary>
    public IReadOnlyList<ServerStatus> AllServers { get => allServers; private set => Set(ref allServers, value); }

    public CliState CliState { get => cliState; private set => Set(ref cliState, value); }

    /// <summary>The CLI found, whether or not it works.</summary>
    public CliCommand? CliCommand { get => cliCommand; private set => Set(ref cliCommand, value); }

    public string? CliPath => CliCommand?.Path;

    public ReleaseVersion? CliVersion { get => cliVersion; private set => Set(ref cliVersion, value); }

    /// <summary>"v22.17.0", for a CLI that runs on Node.js.</summary>
    public string? NodeVersion { get => nodeVersion; private set => Set(ref nodeVersion, value); }

    public DateTimeOffset? LastRead { get => lastRead; private set => Set(ref lastRead, value); }

    public Preferences Preferences { get => preferences; private set => Set(ref preferences, value); }

    /// <summary>Servers seen before, so "Log in" has a target after a full logout.</summary>
    public ServerMemory Memory { get => memory; private set => Set(ref memory, value); }

    public bool Verifying { get => verifying; private set => Set(ref verifying, value); }

    public string? VerifyError { get => verifyError; private set => Set(ref verifyError, value); }

    /// <summary>The last <c>--probe</c> result per server, for as long as the same credential is stored.</summary>
    public IReadOnlyDictionary<string, ProbeResult> Probes { get => probes; private set => Set(ref probes, value); }

    /// <summary>The servers to show: localhost ones only with "show localhost" on.</summary>
    public IReadOnlyList<ServerStatus> Servers => Sessions.Visible(AllServers, Preferences.ShowLocalhost);

    public OverallState Overall() => Overall(clock());

    public OverallState Overall(DateTimeOffset now) => CliState switch
    {
        CliState.Unknown => OverallState.Loading,
        CliState.Missing or CliState.PathUnusable => OverallState.CliMissing,
        CliState.Unsupported => OverallState.Unsupported,
        CliState.Unavailable or CliState.NodeMissing => OverallState.Unavailable,
        _ => Sessions.Overall(Servers, now),
    };

    // Polling

    /// <summary>Reads now, then every <see cref="Preferences.RefreshInterval"/> seconds.</summary>
    public void Start()
    {
        context = SynchronizationContext.Current;
        ScheduleTimer();
        _ = Initial();

        async Task Initial()
        {
            await RefreshVersionAsync();
            await RefreshAsync();
        }
    }

    public void Stop()
    {
        timer?.Dispose();
        timer = null;
    }

    public void Update(Preferences next)
    {
        var old = Preferences;
        if (next == old) return;
        Preferences = next;
        if (next.RefreshInterval != old.RefreshInterval && timer is not null) ScheduleTimer();
        if (next.CliPath != old.CliPath)
        {
            _ = Reread();
        }
        else if (next.ShowLocalhost != old.ShowLocalhost)
        {
            // Shown servers changed: notify for newly shown ones.
            Announce(nameof(Servers));
            EmitExpiryEvents();
        }

        async Task Reread()
        {
            await RefreshVersionAsync();
            await RefreshAsync();
        }
    }

    private void ScheduleTimer()
    {
        timer?.Dispose();
        var interval = TimeSpan.FromSeconds(Preferences.RefreshInterval);
        timer = new Timer(_ => Post(() => _ = RefreshAsync()), null, interval, interval);
    }

    private void Post(Action action)
    {
        if (context is null) action();
        else context.Post(_ => action(), null);
    }

    /// <summary>
    /// Reads <c>status --json</c> once. Never two at once: a refresh asked for
    /// while one runs happens right after it, so the result is never older
    /// than the request.
    /// </summary>
    public async Task RefreshAsync()
    {
        lock (gate)
        {
            if (reading)
            {
                readQueued = true;
                return;
            }
            reading = true;
        }
        while (true)
        {
            lock (gate) readQueued = false;
            await ReadStatus();
            lock (gate)
            {
                if (!readQueued)
                {
                    reading = false;
                    return;
                }
            }
        }
    }

    private async Task ReadStatus()
    {
        if (ResolveCli() is not { } cli)
        {
            AllServers = [];
            LastRead = clock();
            return;
        }
        var result = await cli.RunAsync(["status", "--json"], StatusTimeout);
        LastRead = clock();
        if (!result.Succeeded)
        {
            AllServers = [];
            CliState = FailureState(result, cli.Command, NodeVersion);
            return;
        }
        if (StatusDocument.Parse(result.Stdout) is not { } document)
        {
            AllServers = [];
            CliState = new CliState.Unavailable("unreadable status output");
            return;
        }
        Apply(document);
    }

    private void Apply(StatusDocument document)
    {
        AllServers = document.Servers;
        CliState = new CliState.Ready();
        if (document.Servers.Count > 0) Remember(document.Servers.Select(s => s.Server));
        // A probe result belongs to the credential it checked.
        var kept = Probes
            .Where(entry => document.Servers.Any(s => s.Server == entry.Key && s.CredentialId == entry.Value.CredentialId))
            .ToDictionary(e => e.Key, e => e.Value);
        if (kept.Count != Probes.Count) Probes = kept;
        EmitExpiryEvents();
    }

    // Servers seen before

    /// <summary>
    /// Where "Log in" goes: a stored server (a real deployment before a
    /// localhost one), else one seen before.
    /// </summary>
    public string? KnownServer
    {
        get
        {
            var servers = Servers;
            return servers.FirstOrDefault(s => !Sessions.IsLocalhost(s.Server))?.Server
                ?? servers.FirstOrDefault()?.Server
                ?? Memory.Preferred(Preferences.ShowLocalhost);
        }
    }

    public void Remember(IEnumerable<string> seen)
    {
        var next = Memory.Remember(seen);
        if (next == Memory) return;
        Memory = next;
        if (memoryPath is not null) Memory.Save(memoryPath);
    }

    // Actions

    /// <summary>
    /// <c>status --probe --json</c>: one authenticated request per stored
    /// credential, to see that it still works and its server answers. Only
    /// ever on request. The results stay on the rows while the same
    /// credentials are stored.
    /// </summary>
    public async Task VerifyAsync()
    {
        if (Verifying) return;
        if (ResolveCli() is not { } cli) return;
        Verifying = true;
        VerifyError = null;
        var result = await cli.RunAsync(["status", "--probe", "--json"], VerifyTimeout);
        Verifying = false;
        if (!result.Succeeded)
        {
            var detail = result.LastErrorLines();
            VerifyError = "Verify failed" + (detail.Length == 0 ? "." : ": " + detail);
            return;
        }
        if (StatusDocument.Parse(result.Stdout) is not { } document)
        {
            VerifyError = "Verify failed: unreadable output from the CLI.";
            return;
        }
        // The probe may have filled in missing expiry details, so the
        // document replaces the last read.
        var found = new Dictionary<string, ProbeResult>();
        foreach (var s in document.Servers)
            if (s.Probe is { } p && !found.ContainsKey(s.Server)) found[s.Server] = new ProbeResult(p, s.CredentialId);
        Probes = found;
        Apply(document);
    }

    public Probe? ProbeFor(ServerStatus server) =>
        Probes.TryGetValue(server.Server, out var result) && result.CredentialId == server.CredentialId
            ? result.Probe
            : null;

    /// <summary>
    /// <c>logout --server &lt;url&gt;</c>: revokes the credential on its server
    /// and removes it here. Returns the message to show.
    /// </summary>
    public async Task<(bool Succeeded, string Message)> LogoutAsync(string server)
    {
        var host = Sessions.Host(server);
        if (ResolveCli() is not { } cli) return (false, $"Log out of {host} failed: the varlatch CLI was not found.");
        var result = await cli.RunAsync(["logout", "--server", server], LogoutTimeout);
        await RefreshAsync();
        if (!result.Succeeded) return (false, $"Log out of {host} failed: {result.LastErrorLines()}");
        // "<server>: removed locally; server revocation failed (…)" is worth
        // reading as it is.
        var output = result.Stdout.Trim();
        if (output.Contains("revocation failed")) return (true, output);
        return (true, $"Logged out of {host}.");
    }

    private void EmitExpiryEvents()
    {
        var events = tracker.Update(Servers, clock());
        if (events.Count > 0) ExpiryEvents?.Invoke(events);
    }

    /// <summary>Why a <c>status --json</c> run failed, as the panel explains it.</summary>
    internal static CliState FailureState(CliResult result, CliCommand command, string? nodeVersion)
    {
        if ((result.Stdout + result.Stderr).Contains("Usage:")) return new CliState.Unsupported();
        if (command.Script is not null && NodeMajor(nodeVersion) is { } major && major < MinimumNodeMajor)
            return new CliState.NodeMissing(command.Path, nodeVersion);
        var detail = result.LastErrorLines();
        return new CliState.Unavailable(detail.Length == 0 ? $"varlatch exited with status {result.ExitCode}" : detail);
    }

    /// <summary>22 from "v22.17.0".</summary>
    internal static int? NodeMajor(string? version)
    {
        if (version is null) return null;
        var text = version.Trim().TrimStart('v');
        var dot = text.IndexOf('.');
        return int.TryParse(dot < 0 ? text : text[..dot], out var major) ? major : null;
    }

    /// <summary>Reads <c>varlatch --version</c>, and the version of the Node.js it runs on.</summary>
    public async Task RefreshVersionAsync()
    {
        if (ResolveCli() is not { } cli)
        {
            CliVersion = null;
            NodeVersion = null;
            return;
        }
        var result = await cli.RunAsync(["--version"], StatusTimeout);
        CliVersion = result.Succeeded ? ReleaseVersion.FromCliOutput(result.Stdout) : null;
        if (cli.Command.Script is not null)
        {
            var node = await CliProcess.RunAsync(cli.Command.Executable, ["--version"], cli.Environment, StatusTimeout);
            NodeVersion = node.Succeeded ? node.Stdout.Trim() : null;
        }
        else
        {
            NodeVersion = null;
        }
    }

    // The CLI

    public CliLocator Locator => new(system, Preferences.CliPath);

    /// <summary>
    /// The CLI and the environment to run it with, or null (with
    /// <see cref="CliState"/> saying why) when there is none.
    /// </summary>
    public CliInvocation? ResolveCli()
    {
        var locator = Locator;
        switch (locator.Resolve())
        {
            case CliResolution.Found found:
                CliCommand = found.Command;
                return new CliInvocation(found.Command, locator.Environment(found.Command));
            case CliResolution.OverrideUnusable unusable:
                CliCommand = null;
                CliState = new CliState.PathUnusable(unusable.Path);
                break;
            case CliResolution.NotFound notFound:
                CliCommand = null;
                CliState = new CliState.Missing(notFound.Searched);
                break;
            case CliResolution.NodeMissing node:
                CliCommand = null;
                CliState = new CliState.NodeMissing(node.Path);
                break;
        }
        return null;
    }
}
