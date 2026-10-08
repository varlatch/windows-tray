using System.Text.Json;

namespace VarlatchKit;

/// <summary>
/// The servers seen before, newest first, so "Log in" still has a target
/// after a full logout. Kept in <c>servers.json</c> in the app's state
/// directory.
/// </summary>
public sealed record ServerMemory
{
    public const int Limit = 10;

    public IReadOnlyList<string> Servers { get; private init; }

    public ServerMemory(IEnumerable<string>? servers = null)
    {
        Servers = (servers ?? []).Take(Limit).ToList();
    }

    public bool Equals(ServerMemory? other) => other is not null && Servers.SequenceEqual(other.Servers);

    public override int GetHashCode() => Servers.Count;

    /// <summary><paramref name="seen"/> first, then the others still remembered.</summary>
    public ServerMemory Remember(IEnumerable<string> seen)
    {
        var list = new List<string>();
        foreach (var server in seen.Concat(Servers))
            if (!list.Contains(server)) list.Add(server);
        return new ServerMemory(list);
    }

    public ServerMemory Forget(string server) => new(Servers.Where(s => s != server));

    /// <summary>
    /// Where "Log in" goes with nothing stored: a remembered real deployment
    /// before a localhost one, which counts only with "show localhost" on.
    /// </summary>
    public string? Preferred(bool showLocalhost)
    {
        var usable = Servers.Where(s => showLocalhost || !Sessions.IsLocalhost(s)).ToList();
        return usable.FirstOrDefault(s => !Sessions.IsLocalhost(s)) ?? usable.FirstOrDefault();
    }

    public static ServerMemory Load(string path)
    {
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
            return new ServerMemory(list ?? []);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ServerMemory();
        }
    }

    public void Save(string path) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(Servers));
}
