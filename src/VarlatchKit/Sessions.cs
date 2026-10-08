using System.Text.RegularExpressions;

namespace VarlatchKit;

/// <summary>How one stored credential is doing.</summary>
public enum SessionHealth
{
    Ok,
    /// <summary>Less than 20% of the credential's lifetime left.</summary>
    Expiring,
    Expired,
}

/// <summary>What the tray icon shows, worst first.</summary>
public enum OverallState
{
    /// <summary>Not read yet.</summary>
    Loading,
    /// <summary>No CLI where the app looks.</summary>
    CliMissing,
    /// <summary>The CLI predates <c>status</c>.</summary>
    Unsupported,
    /// <summary>The CLI failed, printed something unreadable, or has no Node.js to run on.</summary>
    Unavailable,
    /// <summary>No credentials stored (or only hidden localhost ones).</summary>
    SignedOut,
    Expired,
    Expiring,
    Ok,
}

public enum Badge
{
    None,
    Warning,
    Error,
}

public static class OverallStateExtensions
{
    /// <summary>
    /// The tray badge: amber while a credential is expiring, red when signed
    /// out, expired, or the CLI is unusable.
    /// </summary>
    public static Badge Badge(this OverallState state) => state switch
    {
        OverallState.Loading or OverallState.Ok => VarlatchKit.Badge.None,
        OverallState.Expiring => VarlatchKit.Badge.Warning,
        _ => VarlatchKit.Badge.Error,
    };

    /// <summary>The CLI cannot be used at all.</summary>
    public static bool CliUnusable(this OverallState state) =>
        state is OverallState.CliMissing or OverallState.Unsupported or OverallState.Unavailable;
}

public static class Sessions
{
    /// <summary>
    /// The CLI decides "expiring"; with a CLI before 0.10.0, which leaves the
    /// field out, the same rule is worked out here. A credential whose expiry
    /// has passed since the last read counts as expired.
    /// </summary>
    public static SessionHealth Health(this ServerStatus server, DateTimeOffset now)
    {
        if (server.Expired == true) return SessionHealth.Expired;
        if (server.ExpiresAt is { } expires && expires <= now) return SessionHealth.Expired;
        if (server.ReportsExpiring) return server.Expiring == true ? SessionHealth.Expiring : SessionHealth.Ok;
        if (server.IssuedAt is { } issued && server.ExpiresAt is { } end && end > issued
            && (end - now).TotalSeconds < (end - issued).TotalSeconds * 0.2)
        {
            return SessionHealth.Expiring;
        }
        return SessionHealth.Ok;
    }

    public static SessionHealth Health(this ServerStatus server) => server.Health(DateTimeOffset.Now);

    /// <summary>"vl.example.com" from "https://vl.example.com/some/path".</summary>
    public static string Host(string url)
    {
        var s = url;
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        var slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];
        return s;
    }

    private static readonly Regex LocalhostPattern = new(
        @"^[a-z][a-z0-9+.-]*://(localhost([:/]|$)|127\.)", RegexOptions.CultureInvariant);

    /// <summary>A local development server: <c>localhost</c> or <c>127.*</c>.</summary>
    public static bool IsLocalhost(string url) => LocalhostPattern.IsMatch(url.ToLowerInvariant());

    /// <summary>The servers to show: localhost ones only when asked for.</summary>
    public static IReadOnlyList<ServerStatus> Visible(IReadOnlyList<ServerStatus> servers, bool showLocalhost) =>
        showLocalhost ? servers : servers.Where(s => !IsLocalhost(s.Server)).ToList();

    /// <summary>The worst state among the shown servers.</summary>
    public static OverallState Overall(IReadOnlyList<ServerStatus> servers, DateTimeOffset now)
    {
        if (servers.Count == 0) return OverallState.SignedOut;
        var health = servers.Select(s => s.Health(now)).ToList();
        if (health.Contains(SessionHealth.Expired)) return OverallState.Expired;
        if (health.Contains(SessionHealth.Expiring)) return OverallState.Expiring;
        return OverallState.Ok;
    }

    /// <summary>"3h 12m left", "12m left", or "expired".</summary>
    public static string Remaining(DateTimeOffset expiresAt, DateTimeOffset now)
    {
        var seconds = (expiresAt - now).TotalSeconds;
        if (seconds <= 0) return "expired";
        var whole = (long)seconds;
        var h = whole / 3600;
        var m = whole % 3600 / 60;
        return h > 0 ? $"{h}h {m}m left" : $"{m}m left";
    }

    /// <summary>What a <c>--probe</c> found, after the line under a server's name.</summary>
    public static string ProbeText(Probe probe) => probe.State switch
    {
        ProbeState.Valid => "verified",
        ProbeState.Invalid => "invalid" + (probe.Detail is { } d ? $" ({d})" : ""),
        _ => "server unreachable",
    };

    /// <summary>The line under a server's name in the panel.</summary>
    public static string Detail(ServerStatus server, DateTimeOffset now)
    {
        if (server.Health(now) == SessionHealth.Expired) return "Session expired";
        if (server.ExpiresAt is not { } expiresAt) return "Logged in, no expiry recorded";
        return "Logged in, " + Remaining(expiresAt, now);
    }
}
