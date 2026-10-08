using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VarlatchKit;

/// <summary>What the last release checks found, kept in <c>update.json</c> in the app's state directory.</summary>
public sealed record ReleaseCache
{
    /// <summary>The latest release, without the "v".</summary>
    [JsonPropertyName("latest")] public string? Latest { get; set; }

    /// <summary>The last check that got an answer.</summary>
    [JsonPropertyName("checkedAt")] public DateTimeOffset? CheckedAt { get; set; }

    /// <summary>The last check, answered or not.</summary>
    [JsonPropertyName("attemptedAt")] public DateTimeOffset? AttemptedAt { get; set; }

    /// <summary>The release a notification was shown for.</summary>
    [JsonPropertyName("notified")] public string? Notified { get; set; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static ReleaseCache Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<ReleaseCache>(File.ReadAllText(path), Options) ?? new ReleaseCache();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ReleaseCache();
        }
    }

    public void Save(string path) => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, Options) + "\n");
}

/// <summary>
/// When to ask GitHub for the latest CLI release (only with "check for new
/// CLI releases" on): when the last answer is over 12 hours old, or over an
/// hour when the panel opens, or older than the CLI installed here; never
/// more than once an hour, failed checks included.
/// </summary>
public static class ReleaseRules
{
    public static readonly TimeSpan BackgroundMaxAge = TimeSpan.FromHours(12);
    public static readonly TimeSpan PanelMaxAge = TimeSpan.FromHours(1);
    public static readonly TimeSpan MinimumGap = TimeSpan.FromHours(1);

    public static bool IsStale(ReleaseCache cache, ReleaseVersion? current, TimeSpan maxAge, DateTimeOffset now)
    {
        if (cache.CheckedAt is not { } checkedAt || now - checkedAt >= maxAge) return true;
        // A release came out, and was installed, since the last check.
        return current is { } c && ReleaseVersion.Parse(cache.Latest) is { } latest && c > latest;
    }

    public static bool ShouldFetch(ReleaseCache cache, ReleaseVersion? current, TimeSpan maxAge, DateTimeOffset now,
                                   bool force = false)
    {
        if (force) return true;
        if (!IsStale(cache, current, maxAge, now)) return false;
        return cache.AttemptedAt is not { } attemptedAt || now - attemptedAt >= MinimumGap;
    }

    /// <summary>The newer release, if there is one.</summary>
    public static ReleaseVersion? Available(ReleaseCache cache, ReleaseVersion? current) =>
        current is { } c && ReleaseVersion.Parse(cache.Latest) is { } latest && latest > c ? latest : null;
}

/// <summary>
/// The opt-in check for new CLI releases: an anonymous request to GitHub's
/// API for varlatch/varlatch's latest release.
/// </summary>
public sealed class ReleaseChecker : ObservableObject
{
    public const string Repository = "varlatch/varlatch";

    private readonly string? cachePath;
    private readonly Func<Task<string?>> fetch;
    private readonly Func<DateTimeOffset> now;
    private ReleaseCache cache;
    private bool checking;

    /// <param name="cachePath">Where to keep the cache; null keeps it in memory only.</param>
    /// <param name="fetch">The latest release's tag, or null when it cannot be read.</param>
    public ReleaseChecker(string? cachePath, Func<Task<string?>>? fetch = null, Func<DateTimeOffset>? now = null)
    {
        this.cachePath = cachePath;
        this.fetch = fetch ?? LatestTagAsync;
        this.now = now ?? (() => DateTimeOffset.Now);
        cache = cachePath is null ? new ReleaseCache() : ReleaseCache.Load(cachePath);
    }

    public ReleaseCache Cache { get => cache; private set => Set(ref cache, value); }

    public bool Checking { get => checking; private set => Set(ref checking, value); }

    /// <summary>Called once per release that is newer than the installed CLI.</summary>
    public event Action<ReleaseVersion>? NewRelease;

    public ReleaseVersion? Available(ReleaseVersion? current) => ReleaseRules.Available(Cache, current);

    /// <summary>Checks when the rules say so, then notifies once for a new release.</summary>
    public async Task CheckAsync(ReleaseVersion? current, TimeSpan maxAge, bool force = false)
    {
        if (Checking) return;
        var started = now();
        if (ReleaseRules.ShouldFetch(Cache, current, maxAge, started, force))
        {
            Checking = true;
            Cache = Cache with { AttemptedAt = started };
            Save();
            var tag = await fetch();
            Checking = false;
            if (ReleaseVersion.Parse(tag) is { } version)
                Cache = Cache with { Latest = version.ToString(), CheckedAt = started };
        }
        if (Available(current) is { } newer && Cache.Notified != newer.ToString())
        {
            Cache = Cache with { Notified = newer.ToString() };
            NewRelease?.Invoke(newer);
        }
        Save();
    }

    private void Save()
    {
        if (cachePath is not null) Cache.Save(cachePath);
    }

    public static string ReleaseUrl(ReleaseVersion version) =>
        $"https://github.com/{Repository}/releases/tag/v{version}";

    /// <summary><c>GET https://api.github.com/repos/varlatch/varlatch/releases/latest</c>, without cookies or credentials.</summary>
    public static async Task<string?> LatestTagAsync()
    {
        using var handler = new HttpClientHandler { UseCookies = false, UseDefaultCredentials = false, Credentials = null };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("Varlatch-for-Windows");
        try
        {
            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String
                ? tag.GetString()
                : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }
}
