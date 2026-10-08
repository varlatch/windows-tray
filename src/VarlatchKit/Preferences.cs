using System.Text.Json;
using System.Text.Json.Nodes;

namespace VarlatchKit;

/// <summary>The app's settings, stored in <c>settings.json</c> in its state directory.</summary>
public sealed record Preferences
{
    public static class Key
    {
        public const string RefreshInterval = "refreshInterval";
        public const string CliPath = "cliPath";
        public const string NotifyExpiry = "notifyExpiry";
        public const string ShowWhenLoggedOut = "showWhenLoggedOut";
        public const string ShowLocalhost = "showLocalhost";
        public const string SessionHours = "sessionHours";
        public const string CheckUpdates = "checkUpdates";
        public const string LaunchAtLogin = "launchAtLogin";
    }

    public const int DefaultRefreshInterval = 30;
    public const int MinimumRefreshInterval = 15;

    public Preferences(int refreshInterval = DefaultRefreshInterval, string cliPath = "", bool notifyExpiry = true,
                       bool showWhenLoggedOut = true, bool showLocalhost = false, int sessionHours = 0,
                       bool checkUpdates = false, bool launchAtLogin = true)
    {
        RefreshInterval = Math.Max(MinimumRefreshInterval, refreshInterval);
        CliPath = cliPath;
        NotifyExpiry = notifyExpiry;
        ShowWhenLoggedOut = showWhenLoggedOut;
        ShowLocalhost = showLocalhost;
        SessionHours = sessionHours is >= 1 and <= 24 ? sessionHours : 0;
        CheckUpdates = checkUpdates;
        LaunchAtLogin = launchAtLogin;
    }

    /// <summary>Seconds between status reads, 15 or more.</summary>
    public int RefreshInterval { get; }

    /// <summary>The CLI to run; empty finds it automatically.</summary>
    public string CliPath { get; }

    public bool NotifyExpiry { get; }

    /// <summary>Off: no tray icon while no credentials are stored.</summary>
    public bool ShowWhenLoggedOut { get; }

    public bool ShowLocalhost { get; }

    /// <summary>How long a new sign-in lasts: 0 for the server's default, else 1 to 24.</summary>
    public int SessionHours { get; }

    public bool CheckUpdates { get; }

    public bool LaunchAtLogin { get; }

    /// <summary><c>--ttl &lt;seconds&gt;</c> for every sign-in the app starts, or nothing for the server's default.</summary>
    public IReadOnlyList<string> TtlArguments =>
        SessionHours > 0 ? ["--ttl", (SessionHours * 3600).ToString(System.Globalization.CultureInfo.InvariantCulture)] : [];

    /// <summary>Reads the settings from <paramref name="settings"/>; missing or mistyped keys take their defaults.</summary>
    public static Preferences From(JsonObject settings)
    {
        var d = new Preferences();
        return new Preferences(
            refreshInterval: Int(Key.RefreshInterval, d.RefreshInterval),
            cliPath: Text(Key.CliPath, d.CliPath),
            notifyExpiry: Bool(Key.NotifyExpiry, d.NotifyExpiry),
            showWhenLoggedOut: Bool(Key.ShowWhenLoggedOut, d.ShowWhenLoggedOut),
            showLocalhost: Bool(Key.ShowLocalhost, d.ShowLocalhost),
            sessionHours: Int(Key.SessionHours, d.SessionHours),
            checkUpdates: Bool(Key.CheckUpdates, d.CheckUpdates),
            launchAtLogin: Bool(Key.LaunchAtLogin, d.LaunchAtLogin));

        bool Bool(string key, bool fallback) =>
            settings[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;

        int Int(string key, int fallback) =>
            settings[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : fallback;

        string Text(string key, string fallback) =>
            settings[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;
    }

    /// <summary>Writes these settings into <paramref name="settings"/>, keeping any other keys.</summary>
    public void WriteTo(JsonObject settings)
    {
        settings[Key.RefreshInterval] = RefreshInterval;
        settings[Key.CliPath] = CliPath;
        settings[Key.NotifyExpiry] = NotifyExpiry;
        settings[Key.ShowWhenLoggedOut] = ShowWhenLoggedOut;
        settings[Key.ShowLocalhost] = ShowLocalhost;
        settings[Key.SessionHours] = SessionHours;
        settings[Key.CheckUpdates] = CheckUpdates;
        settings[Key.LaunchAtLogin] = LaunchAtLogin;
    }
}

/// <summary>
/// <c>settings.json</c>: the preferences and a few flags of the app's own
/// (<c>hasLaunched</c>, <c>debugHooks</c>). Keys it does not know are kept.
/// Edited by hand while the app runs, it is read again.
/// </summary>
public sealed class SettingsFile(string path)
{
    public string Path { get; } = path;

    public JsonObject Read()
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(Path)) as JsonObject ?? new JsonObject();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new JsonObject();
        }
    }

    public Preferences Preferences => Preferences.From(Read());

    public bool Flag(string key) => Read()[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public void Save(Preferences preferences) => Update(o => preferences.WriteTo(o));

    public void SetFlag(string key, bool value) => Update(o => o[key] = value);

    public void Update(Action<JsonObject> change)
    {
        var settings = Read();
        change(settings);
        AtomicFile.WriteAllText(Path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
