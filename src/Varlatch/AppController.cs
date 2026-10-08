using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using VarlatchKit;
using Windows.ApplicationModel.DataTransfer;

namespace Varlatch;

/// <summary>
/// Ties the session store to the rest of the app: settings, notifications,
/// launch at login, the tray icon, and the panel.
/// </summary>
internal sealed class AppController
{
    public static AppController Shared { get; } = new();

    private DispatcherQueue dispatcher = null!;
    private DispatcherQueueTimer? releaseTimer;
    private FileSystemWatcher? settingsWatcher;
    private bool quitting;

    public SettingsFile Settings { get; } = new(Path.Combine(AppPaths.StateDirectory, "settings.json"));
    public SessionStore Store { get; private set; } = null!;
    public SignInController SignIn { get; private set; } = null!;
    public ReleaseChecker Releases { get; } = new(Path.Combine(AppPaths.StateDirectory, "update.json"));
    public Notifier Notifier { get; } = new();
    public TrayIcon Tray { get; private set; } = null!;
    public PanelWindow Panel { get; private set; } = null!;
    public LaunchAtLogin LoginItem { get; } = new(new CurrentUserRegistry(), Environment.ProcessPath!);
    private SettingsWindow? settingsWindow;

    /// <summary>Servers with a logout running.</summary>
    public HashSet<string> LoggingOut { get; } = [];

    /// <summary>After anything the panel shows changed.</summary>
    public event Action? Changed;

    public bool IsInstalled => LaunchAtLogin.IsInstalled(
        Environment.ProcessPath!,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    public void Launch(AppActivationArguments activation)
    {
        dispatcher = DispatcherQueue.GetForCurrentThread();
        var firstLaunch = !Settings.Flag("hasLaunched");
        Settings.SetFlag("hasLaunched", true);
        var preferences = Settings.Preferences;
        // Written out once, so the file shows every setting there is.
        Settings.Save(preferences);

        Store = new SessionStore(preferences, SystemContext.Current(), AppPaths.StateDirectory);
        SignIn = new SignInController(() => Store.ResolveCli());
        Panel = new PanelWindow(this);
        Tray = new TrayIcon();
        Tray.Clicked += () => TogglePanel();
        Tray.MenuRequested += ShowTrayMenu;
        Tray.SessionEnding += Quit;

        Notifier.Install(this);
        Store.ExpiryEvents += events =>
        {
            if (Store.Preferences.NotifyExpiry) Notifier.Post(events, DeviceSignIn);
        };
        Store.Changed += Announce;
        Store.PropertyChanged += (_, e) =>
        {
            // A server that is fine again, or gone, takes its expiry
            // notification with it.
            if (e.PropertyName == nameof(SessionStore.AllServers)) Notifier.WithdrawResolved(Store.AllServers);
        };
        SignIn.Changed += Announce;
        SignIn.Outcome += outcome =>
        {
            if (outcome is SignInOutcome.SignedIn) Store.Remember([outcome.Server]);
            Notifier.Post(outcome, DeviceSignIn);
        };
        SignIn.Finished += () => _ = Store.RefreshAsync();
        // The CLI prints the sign-in page's address; the app opens it.
        SignIn.LinkFound += (_, link) => Open(link);

        Releases.NewRelease += version => Notifier.PostRelease(version, Store.CliVersion, UpdateFor(version));
        Releases.Changed += Announce;
        ReleaseVersion? lastVersion = null;
        Store.PropertyChanged += (_, e) =>
        {
            // A newly installed CLI can make the cached release old news.
            if (e.PropertyName != nameof(SessionStore.CliVersion) || Store.CliVersion == lastVersion) return;
            var first = lastVersion is null;
            lastVersion = Store.CliVersion;
            if (!first) CheckReleases(ReleaseRules.BackgroundMaxAge);
        };

        UpdateTray();
        Store.Start();
        if (IsInstalled) LoginItem.Set(preferences.LaunchAtLogin);

        // Hourly; the rules decide whether that is a request.
        releaseTimer = dispatcher.CreateTimer();
        releaseTimer.Interval = TimeSpan.FromHours(1);
        releaseTimer.Tick += (_, _) => CheckReleases(ReleaseRules.BackgroundMaxAge);
        releaseTimer.Start();
        After(TimeSpan.FromSeconds(3), () => CheckReleases(ReleaseRules.BackgroundMaxAge));

        WatchSettings();
        DebugHooks.Install(this);

        var command = Commands.FromActivation(activation);
        if (command is Command.Launch launch)
        {
            // Started at login: stay in the notification area. Started by
            // hand (from Start, or the first time): show where the app lives.
            if (!launch.AtLogin || firstLaunch) After(TimeSpan.FromSeconds(firstLaunch ? 1.5 : 0.5), () => ShowPanel());
        }
        else
        {
            After(TimeSpan.FromSeconds(0.5), () => Handle(command));
        }
    }

    /// <summary>Another launch of the app, handed over by Windows: run it here.</summary>
    public void Activated(AppActivationArguments activation) => Post(() => Handle(Commands.FromActivation(activation)));

    public void Handle(Command command)
    {
        switch (command)
        {
            case Command.Launch:
                // Opening the app again opens the panel, or Settings while the
                // icon is hidden.
                if (Tray.IsShown) ShowPanel();
                else OpenSettings();
                break;
            case Command.Open:
                ShowPanel();
                break;
            case Command.OpenSettings:
                OpenSettings();
                break;
            case Command.Refresh:
                _ = RefreshAll();
                break;
            case Command.SignInTo s:
                if (ServerAddress.Normalize(s.Server) is { } server)
                {
                    Store.Remember([server]);
                    SignInTo(server);
                    ShowPanel();
                }
                break;
            case Command.Quit:
                Quit();
                break;
            case Command.Unregister:
                LoginItem.Set(false);
                Notifier.Uninstall();
                Quit();
                break;
            case Command.Debug d:
                DebugHooks.Run(d.Name);
                break;
        }
    }

    public void Post(Action action)
    {
        if (dispatcher.HasThreadAccess) action();
        else dispatcher.TryEnqueue(() => action());
    }

    public void After(TimeSpan delay, Action action)
    {
        var timer = dispatcher.CreateTimer();
        timer.Interval = delay;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => action();
        timer.Start();
    }

    private bool announceQueued;

    /// <summary>
    /// Brings the tray icon and the panel up to date, once per turn of the UI
    /// thread however many changes arrived in it: a read of the sessions
    /// changes several things at once.
    /// </summary>
    private void Announce()
    {
        if (announceQueued) return;
        announceQueued = true;
        dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            announceQueued = false;
            UpdateTray();
            Changed?.Invoke();
        });
    }

    // Settings

    /// <summary>settings.json changed: from the Settings window, or by hand.</summary>
    public void PreferencesChanged()
    {
        var next = Settings.Preferences;
        var old = Store.Preferences;
        if (next == old) return;
        Store.Update(next);
        if (next.LaunchAtLogin != old.LaunchAtLogin && IsInstalled) LoginItem.Set(next.LaunchAtLogin);
        if (next.CheckUpdates && !old.CheckUpdates) CheckReleases(ReleaseRules.BackgroundMaxAge);
        Announce();
    }

    public void Save(Preferences preferences)
    {
        Settings.Save(preferences);
        PreferencesChanged();
    }

    private void WatchSettings()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.StateDirectory);
            settingsWatcher = new FileSystemWatcher(AppPaths.StateDirectory, "settings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            settingsWatcher.Changed += (_, _) => Post(PreferencesChanged);
            settingsWatcher.Renamed += (_, _) => Post(PreferencesChanged);
            settingsWatcher.Created += (_, _) => Post(PreferencesChanged);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            Log.Write("cannot watch settings: " + e.Message);
        }
    }

    public void OpenSettings()
    {
        Panel.Hide();
        if (settingsWindow is null)
        {
            settingsWindow = new SettingsWindow(this);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
        }
        settingsWindow.Present();
    }

    // The tray icon and the panel

    public bool TrayShown => Store.Preferences.ShowWhenLoggedOut || Store.Overall() != OverallState.SignedOut;

    private void UpdateTray()
    {
        if (Tray is null) return;
        Tray.SetShown(TrayShown);
        Tray.Update(Store.Overall(), ToolTip(DateTimeOffset.Now));
    }

    public void TogglePanel()
    {
        if (Panel.IsOpen || Panel.JustClosed) Panel.Hide();
        else ShowPanel();
    }

    public void ShowPanel()
    {
        Panel.ShowAt(Tray.Bounds);
        PanelOpened();
    }

    private void ShowTrayMenu(int x, int y)
    {
        Panel.Hide();
        var canVerify = Store.CliState is CliState.Ready && Store.Servers.Count > 0 && !Store.Verifying;
        var items = new List<(string, bool)?>
        {
            ("Open Varlatch", true),
            ("Verify sessions", canVerify),
            null,
            ("Settings", true),
            null,
            ("Quit Varlatch", true),
        };
        switch (Tray.ShowMenu(x, y, items, defaultIndex: 0))
        {
            case 0: ShowPanel(); break;
            case 1: Verify(); break;
            case 3: OpenSettings(); break;
            case 5: Quit(); break;
        }
    }

    /// <summary>The tooltip on the tray icon.</summary>
    public string ToolTip(DateTimeOffset now)
    {
        switch (Store.Overall(now))
        {
            case OverallState.Loading: return "Varlatch";
            case OverallState.CliMissing: return "Varlatch: the varlatch CLI was not found";
            case OverallState.Unsupported: return "Varlatch: this varlatch CLI is too old";
            case OverallState.Unavailable:
                return Store.CliState is CliState.NodeMissing
                    ? "Varlatch: Node.js 22 or newer is needed"
                    : "Varlatch: the varlatch CLI did not answer";
            case OverallState.SignedOut: return "Varlatch: not logged in";
            default:
                var lines = Store.Servers.Select(server =>
                {
                    var host = Sessions.Host(server.Server);
                    if (server.Health(now) == SessionHealth.Expired) return $"{host}: expired";
                    if (server.ExpiresAt is not { } expiresAt) return $"{host}: no expiry recorded";
                    return $"{host}: " + Sessions.Remaining(expiresAt, now);
                });
                return string.Join("\n", lines.Prepend("Varlatch"));
        }
    }

    /// <summary>The panel opened: read the CLI version and the sessions again, and maybe check for a release.</summary>
    public void PanelOpened()
    {
        _ = Opened();

        async Task Opened()
        {
            await RefreshAll();
            CheckReleases(ReleaseRules.PanelMaxAge);
        }
    }

    public async Task RefreshAll()
    {
        await Store.RefreshVersionAsync();
        await Store.RefreshAsync();
    }

    // Actions

    /// <summary>Signs in to <paramref name="server"/> in the browser (renewing is the same). While a sign-in waits, opens its link again.</summary>
    public void SignInTo(string server)
    {
        if (SignIn.SignIn(server, Store.Preferences.TtlArguments) is { } link) Open(link);
    }

    /// <summary>Device sign-in (<c>login --start</c> / <c>--wait</c>) arrived in CLI 0.14.0.</summary>
    public bool DeviceSignIn => Store.CliVersion is { } v && v >= ReleaseVersion.DeviceSignIn;

    /// <summary>
    /// Signs in to <paramref name="server"/> from another device: an address and
    /// a code to approve on any device, such as a phone. Takes over a browser
    /// sign-in still waiting.
    /// </summary>
    public void SignInFromAnotherDevice(string server)
    {
        if (!DeviceSignIn) return;
        _ = SignIn.SignInFromAnotherDeviceAsync(server, Store.Preferences.TtlArguments);
    }

    public void CancelSignIn() => SignIn.Cancel();

    public void Logout(string server)
    {
        if (!LoggingOut.Add(server)) return;
        Announce();
        _ = Run();

        async Task Run()
        {
            var (succeeded, message) = await Store.LogoutAsync(server);
            LoggingOut.Remove(server);
            Announce();
            Notifier.PostLogout(server, message, succeeded);
        }
    }

    public void Verify() => _ = Store.VerifyAsync();

    /// <summary>The server's dashboard is the server's own address.</summary>
    public void OpenDashboard(string? server = null)
    {
        if ((server ?? Store.KnownServer) is { } address) Open(address);
    }

    /// <summary>Opens an http(s) address in the default browser.</summary>
    public void Open(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http")) return;
        OpenUri(uri.AbsoluteUri);
    }

    /// <summary>Opens a URI with whatever Windows has for it: a browser, or Settings for <c>ms-settings:</c>.</summary>
    public static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write($"cannot open {uri}: {e.Message}");
        }
    }

    public void Copy(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        // Kept after the app quits.
        Clipboard.Flush();
    }

    public void Quit()
    {
        if (quitting) return;
        quitting = true;
        // A sign-in the app started must not outlive it.
        SignIn?.Cancel(quietly: true);
        Store?.Stop();
        releaseTimer?.Stop();
        settingsWatcher?.Dispose();
        Panel?.Close();
        settingsWindow?.Close();
        Tray?.Dispose();
        ToastActivator.Stop();
        Application.Current.Exit();
    }

    // The CLI and its updates

    public CliInstallKind? CliInstall => Store.CliCommand is { } command ? VarlatchKit.CliInstall.Kind(command) : null;

    /// <summary>The script that installs the CLI, shipped next to the app.</summary>
    public static string InstallScript => Path.Combine(AppContext.BaseDirectory, "Scripts", "install-cli.ps1");

    /// <summary>Installs the release CLI in Terminal: it asks for the server, checks the download, and asks before installing.</summary>
    public void InstallCli()
    {
        Terminal.Run("Installing the Varlatch CLI",
                     [Terminal.PowerShell, "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", InstallScript,
                      "-App", Environment.ProcessPath!]);
    }

    /// <summary>winget, when Windows has it (the App Installer).</summary>
    public static string? Winget
    {
        get
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "Microsoft", "WindowsApps", "winget.exe");
            return File.Exists(path) ? path : null;
        }
    }

    public const string NodePackage = "OpenJS.NodeJS.LTS";

    /// <summary>Installs Node.js LTS with winget, in Terminal.</summary>
    public void InstallNode()
    {
        if (Winget is not { } winget)
        {
            Open("https://nodejs.org/en/download");
            return;
        }
        Terminal.Run("Installing Node.js with winget",
                     [winget, "install", "--id", NodePackage, "--exact", "--source", "winget",
                      "--accept-source-agreements", "--accept-package-agreements"]);
        // Picked up once it is there: winget changes the PATH in the registry.
        After(TimeSpan.FromSeconds(30), () => _ = RefreshAll());
    }

    /// <summary>Checks for a newer CLI release, with "check for new CLI releases" on.</summary>
    public void CheckReleases(TimeSpan maxAge, bool force = false)
    {
        if (!Store.Preferences.CheckUpdates) return;
        _ = Releases.CheckAsync(Store.CliVersion, maxAge, force);
    }

    /// <summary>A newer CLI release, with release checks on.</summary>
    public ReleaseVersion? AvailableRelease => Store.Preferences.CheckUpdates ? Releases.Available(Store.CliVersion) : null;

    public enum Update
    {
        /// <summary><c>varlatch self-update &lt;version&gt;</c> (CLI 0.11.0 and newer).</summary>
        SelfUpdate,
        /// <summary>Release notes only: updated the way it was installed.</summary>
        Manual,
    }

    public Update UpdateFor(ReleaseVersion version) =>
        CliInstall == CliInstallKind.Release && Store.CliVersion is { } v && v >= ReleaseVersion.SelfUpdate
            ? Update.SelfUpdate
            : Update.Manual;

    /// <summary>
    /// Updates the CLI in Terminal with its own self-update, which shows what
    /// it checked and asks before replacing anything. A CLI in a folder this
    /// user cannot write to updates from an administrator PowerShell.
    /// </summary>
    public void UpdateCli(ReleaseVersion version)
    {
        if (UpdateFor(version) != Update.SelfUpdate || Store.CliCommand is not { } command)
        {
            Open(ReleaseChecker.ReleaseUrl(version));
            return;
        }
        string[] run = [command.Executable, .. command.Prefix, "self-update", version.ToString()];
        var elevated = !VarlatchKit.CliInstall.DirectoryWritable(command.CodePath);
        Terminal.Run("Updating the Varlatch CLI", run, elevated);
        After(TimeSpan.FromSeconds(20), () => _ = RefreshAll());
    }
}
