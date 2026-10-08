using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VarlatchKit;

/// <summary>
/// <c>varlatch status --json</c>: the stored credentials, read offline. Every
/// server field except <c>server</c> may be null; credentials stored with
/// <c>--token</c> have no expiry metadata.
/// </summary>
public sealed record StatusDocument(int? Version, IReadOnlyList<ServerStatus> Servers)
{
    public bool Equals(StatusDocument? other) =>
        other is not null && Version == other.Version && Servers.SequenceEqual(other.Servers);

    public override int GetHashCode() => HashCode.Combine(Version, Servers.Count);

    /// <summary>Null for anything that is not a status document.</summary>
    public static StatusDocument? Parse(string text)
    {
        try
        {
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            int? version = null;
            if (root.TryGetProperty("version", out var v) && v.ValueKind != JsonValueKind.Null)
            {
                if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var number)) return null;
                version = number;
            }

            var servers = new List<ServerStatus>();
            if (root.TryGetProperty("servers", out var list) && list.ValueKind != JsonValueKind.Null)
            {
                if (list.ValueKind != JsonValueKind.Array) return null;
                foreach (var item in list.EnumerateArray())
                {
                    var server = ServerStatus.Read(item);
                    if (server is null) return null;
                    servers.Add(server);
                }
            }
            return new StatusDocument(version, servers);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public enum ProbeState
{
    Valid,
    Invalid,
    Unreachable,
}

/// <summary>From <c>status --probe --json</c> only.</summary>
public sealed record Probe(ProbeState State, string? Detail = null)
{
    internal static Probe? Read(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        // An unknown state from a newer CLI reads as unreachable rather than
        // failing the whole document.
        var state = ProbeState.Unreachable;
        if (element.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String)
        {
            state = s.GetString() switch
            {
                "valid" => ProbeState.Valid,
                "invalid" => ProbeState.Invalid,
                _ => ProbeState.Unreachable,
            };
        }
        string? detail = null;
        if (element.TryGetProperty("detail", out var d) && d.ValueKind != JsonValueKind.Null)
        {
            // A detail that is not text makes the probe unreadable.
            if (d.ValueKind != JsonValueKind.String) return null;
            detail = d.GetString();
        }
        return new Probe(state, detail);
    }
}

public sealed record ServerStatus(string Server)
{
    public string? Name { get; init; }
    public DateTimeOffset? IssuedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? CredentialId { get; init; }
    public bool? Expired { get; init; }

    /// <summary>
    /// The CLI's own "expiring" rule (from 0.10.0). Null either when the field
    /// is null or when an older CLI left it out; <see cref="ReportsExpiring"/>
    /// tells the two apart.
    /// </summary>
    public bool? Expiring { get; init; }

    public bool ReportsExpiring { get; init; } = true;

    /// <summary>From <c>status --probe --json</c> only.</summary>
    public Probe? Probe { get; init; }

    internal static ServerStatus? Read(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty("server", out var server) || server.ValueKind != JsonValueKind.String) return null;
        return new ServerStatus(server.GetString()!)
        {
            Name = Text(element, "name"),
            IssuedAt = ParseDate(Text(element, "issuedAt")),
            ExpiresAt = ParseDate(Text(element, "expiresAt")),
            CredentialId = Text(element, "credentialId"),
            Expired = Flag(element, "expired"),
            ReportsExpiring = element.TryGetProperty("expiring", out _),
            Expiring = Flag(element, "expiring"),
            Probe = element.TryGetProperty("probe", out var probe) ? Probe.Read(probe) : null,
        };

        static string? Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        static bool? Flag(JsonElement e, string name) =>
            e.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? p.GetBoolean()
                : null;
    }

    private static readonly Regex InternetDateTime = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$",
        RegexOptions.CultureInvariant);

    /// <summary>ISO 8601, with or without fractional seconds; null for anything else.</summary>
    public static DateTimeOffset? ParseDate(string? text)
    {
        if (text is null || !InternetDateTime.IsMatch(text)) return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
            ? date
            : null;
    }
}
