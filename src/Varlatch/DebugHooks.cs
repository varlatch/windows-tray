using System.Text.Json;
using System.Text.Json.Nodes;
using VarlatchKit;

namespace Varlatch;

/// <summary>
/// Checks without clicking. With <c>"debugHooks": true</c> in settings.json,
/// the app writes its state to <c>debug-state.json</c> in its state
/// directory whenever it changes, and <c>Varlatch.exe --debug &lt;name&gt;</c>
/// runs these: dump-state, open-panel, close-panel, settings, refresh, and
/// these actions on the first server shown (or the one "Log in" would use):
/// sign-in, device, cancel, verify, logout; quit; check-releases (when the
/// once-an-hour rule allows), check-releases-now, and install-cli (opens
/// Terminal).
/// </summary>
internal static class DebugHooks
{
    private static AppController? app;
    private static readonly Dictionary<string, List<string>> Events = [];
    private static bool writeQueued;

    public static bool Enabled => app?.Settings.Flag("debugHooks") == true;

    public static string StatePath => Path.Combine(AppPaths.StateDirectory, "debug-state.json");

    public static void Install(AppController controller)
    {
        app = controller;
        controller.Changed += WriteState;
        WriteState();
    }

    public static void Run(string name)
    {
        if (app is null || !Enabled) return;
        var first = app.Store.Servers.FirstOrDefault()?.Server ?? app.Store.KnownServer;
        switch (name)
        {
            case "dump-state": break;
            case "open-panel": app.ShowPanel(); break;
            case "close-panel": app.Panel.Hide(); break;
            case "settings": app.OpenSettings(); break;
            case "refresh": _ = app.RefreshAll(); break;
            case "sign-in" when first is not null: app.SignInTo(first); break;
            case "device" when first is not null: app.SignInFromAnotherDevice(first); break;
            case "cancel": app.CancelSignIn(); break;
            case "verify": app.Verify(); break;
            case "logout" when first is not null: app.Logout(first); break;
            case "quit": app.Quit(); return;
            case "check-releases": app.CheckReleases(TimeSpan.Zero); break;
            case "check-releases-now": app.CheckReleases(TimeSpan.Zero, force: true); break;
            case "install-cli": app.InstallCli(); break;
            case "tray-icons": WriteTrayIcons(); break;
            default: Record("unknown", name); break;
        }
        WriteState();
    }

    /// <summary>
    /// Every state of the tray icon, on a light and a dark taskbar, at the
    /// sizes of 100% to 200% scale, side by side in tray-icons.png.
    /// </summary>
    private static void WriteTrayIcons()
    {
        int[] sizes = [16, 20, 24, 32];
        var states = Enum.GetValues<OverallState>();
        const int cell = 40;
        using var sheet = new System.Drawing.Bitmap(cell * sizes.Length * 2, cell * states.Length);
        using var g = System.Drawing.Graphics.FromImage(sheet);
        for (var light = 0; light < 2; light++)
        {
            using var background = new System.Drawing.SolidBrush(light == 1 ? System.Drawing.Color.FromArgb(243, 243, 243) : System.Drawing.Color.FromArgb(32, 32, 32));
            g.FillRectangle(background, light * cell * sizes.Length, 0, cell * sizes.Length, cell * states.Length);
            for (var row = 0; row < states.Length; row++)
            {
                for (var col = 0; col < sizes.Length; col++)
                {
                    using var icon = TrayIconRenderer.Draw(states[row], sizes[col], light == 1);
                    g.DrawImageUnscaled(icon, (light * sizes.Length + col) * cell + (cell - sizes[col]) / 2, row * cell + (cell - sizes[col]) / 2);
                }
            }
        }
        sheet.Save(Path.Combine(AppPaths.StateDirectory, "tray-icons.png"), System.Drawing.Imaging.ImageFormat.Png);
        Record("tray-icons", string.Join(", ", states));
    }

    /// <summary>Keeps the last values of something that happened, such as the notifications posted.</summary>
    public static void Record(string key, string value)
    {
        if (!Enabled) return;
        if (!Events.TryGetValue(key, out var list)) Events[key] = list = [];
        list.Add(value);
        if (list.Count > 20) list.RemoveAt(0);
        WriteState();
    }

    /// <summary>Once per turn of the UI thread, however many changes came in it.</summary>
    public static void WriteState()
    {
        if (app is null || !Enabled || writeQueued) return;
        writeQueued = true;
        app.Post(() => app.After(TimeSpan.FromMilliseconds(50), () =>
        {
            writeQueued = false;
            Write();
        }));
    }

    private static void Write()
    {
        if (app is null) return;
        var store = app.Store;
        var now = DateTimeOffset.Now;
        var state = new JsonObject
        {
            ["executable"] = Environment.ProcessPath,
            ["processId"] = Environment.ProcessId,
            ["environmentPATH"] = Environment.GetEnvironmentVariable("PATH"),
            ["cliPath"] = store.CliPath ?? "",
            ["cliRunsAs"] = store.CliCommand is { } c ? string.Join(" ", c.Prefix.Prepend(c.Executable)) : "",
            ["cliState"] = store.CliState is CliState.Missing missing
                ? $"Missing {{ Searched = {string.Join(", ", missing.Searched)} }}"
                : store.CliState.ToString(),
            ["cliVersion"] = store.CliVersion?.ToString() ?? "",
            ["nodeVersion"] = store.NodeVersion ?? "",
            ["cliInstall"] = app.CliInstall?.ToString() ?? "",
            ["overall"] = store.Overall(now).ToString(),
            ["servers"] = new JsonArray(store.Servers.Select(s =>
            {
                var probe = store.ProbeFor(s) is { } p ? " · " + Sessions.ProbeText(p) : "";
                return (JsonNode)$"{s.Server} {s.Health(now).ToString().ToLowerInvariant()}: {Sessions.Detail(s, now)}{probe}";
            }).ToArray()),
            ["hiddenServers"] = store.AllServers.Count - store.Servers.Count,
            ["knownServer"] = store.KnownServer ?? "",
            ["rememberedServers"] = new JsonArray(store.Memory.Servers.Select(s => (JsonNode)s).ToArray()),
            ["signIn"] = app.SignIn.State.ToString(),
            ["deviceSignIn"] = app.DeviceSignIn,
            ["availableRelease"] = app.AvailableRelease?.ToString() ?? "",
            ["releaseCache"] = JsonSerializer.Serialize(app.Releases.Cache),
            ["verifying"] = store.Verifying,
            ["verifyError"] = store.VerifyError ?? "",
            ["loggingOut"] = new JsonArray(app.LoggingOut.Select(s => (JsonNode)s).ToArray()),
            ["trayShown"] = app.Tray.IsShown,
            ["trayBounds"] = app.Tray.Bounds?.ToString() ?? "",
            ["trayPromoted"] = TrayIconVisibility.IsPromoted(new CurrentUserRegistry(), Environment.ProcessPath!)?.ToString() ?? "unknown",
            ["toolTip"] = app.ToolTip(now),
            ["panelOpen"] = app.Panel.IsOpen,
            ["panelBounds"] = app.Panel.PanelBounds,
            ["notificationSetting"] = app.Notifier.Setting,
            ["installed"] = app.IsInstalled,
            ["launchAtLogin"] = app.LoginItem.Current.ToString(),
            ["preferences"] = store.Preferences.ToString(),
            ["writtenAt"] = now.ToString("o"),
        };
        foreach (var (key, values) in Events)
            state[key] = new JsonArray(values.Select(v => (JsonNode)v).ToArray());
        AtomicFile.WriteAllText(StatePath, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
