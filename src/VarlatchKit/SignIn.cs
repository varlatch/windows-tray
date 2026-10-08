using System.Text.Json;
using System.Text.RegularExpressions;

namespace VarlatchKit;

/// <summary>
/// What <c>varlatch login --start --json</c> hands out: an address to open on
/// any device and a code to type there. The address has no code in it on
/// purpose.
/// </summary>
public sealed record DeviceCode(string VerificationUri, string UserCode, DateTimeOffset? ExpiresAt)
{
    public static DeviceCode? Parse(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("verificationUri", out var uri) || uri.ValueKind != JsonValueKind.String) return null;
            if (!root.TryGetProperty("userCode", out var code) || code.ValueKind != JsonValueKind.String) return null;
            var expires = root.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.String
                ? ServerStatus.ParseDate(e.GetString())
                : null;
            var parsed = new DeviceCode(uri.GetString()!, code.GetString()!, expires);
            return parsed.VerificationUri.Length == 0 || parsed.UserCode.Length == 0 ? null : parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>"Code expires in 9m".</summary>
    public string ExpiryText(DateTimeOffset now)
    {
        if (ExpiresAt is not { } expiresAt) return "";
        var seconds = (expiresAt - now).TotalSeconds;
        if (seconds <= 0) return "Code expired";
        var minutes = (long)seconds / 60;
        return minutes >= 1 ? $"Code expires in {minutes}m" : "Code expires in under a minute";
    }

    public string ExpiryText() => ExpiryText(DateTimeOffset.Now);
}

public abstract record SignInState
{
    public sealed record Idle : SignInState;

    /// <summary>
    /// Waiting for the browser. <paramref name="Link"/> is the address the CLI
    /// printed, which the app opens, and opens again on request, for when the
    /// page opened in the wrong browser.
    /// </summary>
    public sealed record Browser(string Server, string? Link) : SignInState;

    /// <summary>Waiting for approval from another device; no code yet while <c>--start</c> runs.</summary>
    public sealed record Device(string Server, DeviceCode? Code) : SignInState;

    /// <summary>The server being signed in to, while one is.</summary>
    public string? Target => this switch
    {
        Browser b => b.Server,
        Device d => d.Server,
        _ => null,
    };
}

public abstract record SignInOutcome(string Server)
{
    /// <param name="Warning">The CLI's own line when the old credential could not be revoked.</param>
    public sealed record SignedIn(string Server, string? Warning) : SignInOutcome(Server);

    public sealed record Cancelled(string Server) : SignInOutcome(Server);

    /// <summary>The code was denied in the browser.</summary>
    public sealed record Denied(string Server) : SignInOutcome(Server);

    /// <summary>The code was not approved within its 10 minutes.</summary>
    public sealed record CodeExpired(string Server) : SignInOutcome(Server);

    /// <param name="Browser">A browser sign-in, which another device could replace.</param>
    public sealed record Failed(string Server, string Message, bool Browser) : SignInOutcome(Server);

    public string Text
    {
        get
        {
            var host = Sessions.Host(Server);
            return this switch
            {
                SignedIn s => $"Logged in to {host}." + (s.Warning is { } w ? " " + w : ""),
                Cancelled => $"Sign-in to {host} cancelled.",
                Denied => $"Sign-in to {host} was denied.",
                CodeExpired => $"The sign-in code for {host} expired before it was approved.",
                Failed f => $"Sign-in to {host} failed: {f.Message}",
                _ => "",
            };
        }
    }
}

/// <summary>
/// Sign-ins the app starts and owns, one at a time.
/// </summary>
/// <remarks>
/// A browser sign-in runs <c>varlatch login --server &lt;url&gt;</c>, which
/// prints the sign-in page's address and waits for the browser's callback.
/// The app opens that address itself (with <c>VARLATCH_NO_BROWSER</c> set,
/// so a CLI that would open it too does not). A device sign-in (CLI 0.14.0
/// and newer), for when the browser here has no passkey for the server, runs
/// <c>login --start --json</c> for an address and a code, then
/// <c>login --wait --json</c> until someone approves the code on any device.
///
/// Either way the CLI revokes the credential it replaces only after the new
/// one is saved, so renewing is signing in again, and a cancelled sign-in
/// never signs anyone out.
///
/// Made and used on the UI thread; the CLI's output arrives on another and
/// is posted back.
/// </remarks>
public sealed class SignInController : ObservableObject
{
    /// <summary>Exit status of <c>login --wait</c> while the code still waits.</summary>
    internal const int PendingExit = 75;

    public const int WaitSeconds = 600;
    internal static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(45);

    /// <summary>Tells the CLI not to open a browser: the app does.</summary>
    public const string NoBrowserVariable = "VARLATCH_NO_BROWSER";

    private readonly Func<CliInvocation?> cli;
    private readonly SynchronizationContext? context;
    private SignInState state = new SignInState.Idle();
    private CliProcess? process;
    private bool running;
    private bool cancelled;
    private bool quiet;
    private TaskCompletionSource idle = Completed();

    public SignInController(Func<CliInvocation?> cli)
    {
        this.cli = cli;
        context = SynchronizationContext.Current;
    }

    public SignInState State { get => state; private set => Set(ref state, value); }

    /// <summary>Every sign-in's end, except one cancelled to make way for another.</summary>
    public event Action<SignInOutcome>? Outcome;

    /// <summary>After any sign-in ends, to read the sessions again.</summary>
    public event Action? Finished;

    /// <summary>The address of a browser sign-in, once, as soon as the CLI prints it: for the app to open.</summary>
    public event Action<string, string>? LinkFound;

    public bool IsIdle => State is SignInState.Idle;

    // Browser

    /// <summary>
    /// Starts a browser sign-in. While one already waits, starts nothing and
    /// returns its link, for the caller to open again.
    /// </summary>
    public string? SignIn(string server, IReadOnlyList<string>? ttlArguments = null)
    {
        if (State is SignInState.Browser waiting) return waiting.Link;
        if (!IsIdle) return null;
        if (cli() is not { } invocation)
        {
            Outcome?.Invoke(new SignInOutcome.Failed(server, "the varlatch CLI was not found.", Browser: false));
            return null;
        }
        Begin(new SignInState.Browser(server, null));
        var linkSeen = false;
        var started = invocation.With(NoBrowserVariable, "1").Process(
            ["login", "--server", server, .. ttlArguments ?? []],
            stdout =>
            {
                if (linkSeen || EnrollLink(stdout) is not { } link) return;
                linkSeen = true;
                Post(() => Found(link, server));
            });
        process = started;
        started.Start();
        _ = Complete();
        return null;

        async Task Complete()
        {
            var result = await started.WaitAsync();
            Post(() =>
            {
                SignInOutcome outcome;
                if (result.Succeeded) outcome = new SignInOutcome.SignedIn(server, Warning(result));
                else if (result.Signaled && !result.TimedOut) outcome = new SignInOutcome.Cancelled(server);
                else outcome = new SignInOutcome.Failed(server, result.LastErrorLines(), Browser: true);
                Finish(outcome);
            });
        }
    }

    private void Found(string link, string server)
    {
        if (State is SignInState.Browser { Link: null } b && b.Server == server)
        {
            State = new SignInState.Browser(server, link);
            LinkFound?.Invoke(server, link);
        }
    }

    // Another device

    /// <summary>
    /// Starts a device sign-in. A browser sign-in still waiting makes way for
    /// it quietly; a device sign-in already under way is left alone.
    /// </summary>
    public async Task SignInFromAnotherDeviceAsync(string server, IReadOnlyList<string>? ttlArguments = null)
    {
        if (State is SignInState.Device) return;
        if (State is SignInState.Browser)
        {
            Cancel(quietly: true);
            await WaitUntilIdleAsync();
        }
        if (!IsIdle) return;
        if (cli() is not { } invocation)
        {
            Outcome?.Invoke(new SignInOutcome.Failed(server, "the varlatch CLI was not found.", Browser: false));
            return;
        }
        Begin(new SignInState.Device(server, null));

        var start = invocation.Process(["login", "--server", server, "--start", "--json", .. ttlArguments ?? []]);
        process = start;
        start.Start(StartTimeout);
        var started = await start.WaitAsync();
        if (cancelled)
        {
            Finish(new SignInOutcome.Cancelled(server));
            return;
        }
        if (!started.Succeeded)
        {
            Finish(new SignInOutcome.Failed(server, started.LastErrorLines(), Browser: false));
            return;
        }
        if (DeviceCode.Parse(started.Stdout.Trim()) is not { } code)
        {
            Finish(new SignInOutcome.Failed(server, "the CLI gave no address and code.", Browser: false));
            return;
        }
        State = new SignInState.Device(server, code);

        // `--wait` gives up at its timeout with 75 while the code still waits;
        // ask again until it is approved, denied, or expired.
        while (true)
        {
            var wait = invocation.Process(["login", "--server", server, "--wait",
                                           "--timeout", WaitSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "--json"]);
            process = wait;
            wait.Start();
            var result = await wait.WaitAsync();
            if (cancelled || (result.Signaled && !result.TimedOut))
            {
                Finish(new SignInOutcome.Cancelled(server));
                return;
            }
            if (result.ExitCode == PendingExit && result.LaunchError is null) continue;
            if (result.Succeeded)
            {
                Finish(new SignInOutcome.SignedIn(server, Warning(result)));
                return;
            }
            Finish(WaitState(result.Stdout) switch
            {
                "denied" => new SignInOutcome.Denied(server),
                "expired" => new SignInOutcome.CodeExpired(server),
                _ => new SignInOutcome.Failed(server, result.LastErrorLines(), Browser: false),
            });
            return;
        }
    }

    /// <summary>The <c>state</c> in the last JSON line <c>--wait</c> printed.</summary>
    internal static string? WaitState(string stdout)
    {
        var line = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (line is null) return null;
        try
        {
            using var json = JsonDocument.Parse(line);
            return json.RootElement.ValueKind == JsonValueKind.Object
                   && json.RootElement.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Both

    /// <summary>Stops the sign-in under way. <paramref name="quietly"/>: another sign-in takes over, so no "cancelled" outcome.</summary>
    public void Cancel(bool quietly = false)
    {
        if (!running) return;
        cancelled = true;
        quiet = quietly;
        process?.Terminate();
    }

    /// <summary>Completes once no sign-in is under way.</summary>
    public Task WaitUntilIdleAsync() => idle.Task;

    private void Begin(SignInState next)
    {
        State = next;
        running = true;
        cancelled = false;
        quiet = false;
        idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void Finish(SignInOutcome outcome)
    {
        process = null;
        running = false;
        State = new SignInState.Idle();
        if (!(quiet && outcome is SignInOutcome.Cancelled)) Outcome?.Invoke(outcome);
        Finished?.Invoke();
        idle.TrySetResult();
    }

    private void Post(Action action)
    {
        if (context is null) action();
        else context.Post(_ => action(), null);
    }

    private static TaskCompletionSource Completed()
    {
        var done = new TaskCompletionSource();
        done.SetResult();
        return done;
    }

    /// <summary>The CLI's <c>varlatch:</c> lines, such as a credential it could not revoke.</summary>
    internal static string? Warning(CliResult result)
    {
        var lines = (result.Stdout + "\n" + result.Stderr).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.StartsWith("varlatch:", StringComparison.Ordinal))
            .ToList();
        return lines.Count == 0 ? null : string.Join(" ", lines);
    }

    private static readonly Regex EnrollPattern = new(@"https?://\S+/enroll\?\S+", RegexOptions.CultureInvariant);

    /// <summary>The sign-in page's address in the CLI's output: <c>https://…/enroll?…</c>.</summary>
    public static string? EnrollLink(string output)
    {
        var match = EnrollPattern.Match(output);
        return match.Success ? match.Value : null;
    }
}
