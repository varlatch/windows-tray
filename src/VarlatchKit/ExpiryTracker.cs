namespace VarlatchKit;

/// <summary>One notification for each move into <i>expiring</i> or <i>expired</i>.</summary>
public sealed record ExpiryEvent(string Server, SessionHealth Health, DateTimeOffset? ExpiresAt)
{
    public string Title => Health == SessionHealth.Expired ? "Session expired" : "Session expiring";

    public string Body(DateTimeOffset now)
    {
        var host = Sessions.Host(Server);
        if (Health == SessionHealth.Expired) return $"Credential for {host} has expired. Log in again.";
        if (ExpiresAt is not { } expiresAt) return $"Credential for {host} expires soon.";
        return $"Credential for {host} expires soon ({Sessions.Remaining(expiresAt, now)}).";
    }

    public string Body() => Body(DateTimeOffset.Now);
}

/// <summary>
/// Remembers which state each server was last notified for, so each
/// transition notifies exactly once, as the other Varlatch apps do. A server
/// that is fine again, or gone, is forgotten, so it notifies again next time.
/// </summary>
public sealed class ExpiryTracker
{
    private Dictionary<string, SessionHealth> notified = new();

    public IReadOnlyDictionary<string, SessionHealth> Notified => notified;

    /// <summary>The events for <paramref name="servers"/> that were not notified yet.</summary>
    public IReadOnlyList<ExpiryEvent> Update(IEnumerable<ServerStatus> servers, DateTimeOffset now)
    {
        var next = new Dictionary<string, SessionHealth>();
        var events = new List<ExpiryEvent>();
        foreach (var server in servers)
        {
            var health = server.Health(now);
            if (health == SessionHealth.Ok) continue;
            next[server.Server] = health;
            if (!notified.TryGetValue(server.Server, out var before) || before != health)
                events.Add(new ExpiryEvent(server.Server, health, server.ExpiresAt));
        }
        notified = next;
        return events;
    }
}
