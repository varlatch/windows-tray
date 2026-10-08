using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VarlatchKit;
using Windows.Graphics;
using static Varlatch.Ui;

namespace Varlatch;

/// <summary>
/// Settings, stored in settings.json in the app's state directory. Every
/// change applies at once.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly AppController app;
    private readonly ToggleSwitch launchAtLogin = Toggle();
    private readonly TextBlock loginNote = Caption("");
    private readonly ComboBox sessionLength = new() { MinWidth = 200 };
    private readonly NumberBox interval = new()
    {
        Minimum = Preferences.MinimumRefreshInterval,
        Maximum = 3600,
        SmallChange = 15,
        LargeChange = 60,
        SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        MinWidth = 140,
    };
    private readonly ToggleSwitch notifyExpiry = Toggle();
    private readonly ToggleSwitch showWhenLoggedOut = Toggle();
    private readonly ToggleSwitch showLocalhost = Toggle();
    private readonly TextBox cliPath = new() { PlaceholderText = "Automatic", MinWidth = 0 };
    private readonly TextBlock cliInUse = Caption("", select: true);
    private readonly ToggleSwitch checkUpdates = Toggle();
    private bool loading;

    public SettingsWindow(AppController app)
    {
        this.app = app;
        Title = "Varlatch Settings";
        SystemBackdrop = new MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Varlatch.ico"));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
        }

        var stack = new StackPanel { Spacing = 4, Padding = new Thickness(28, 20, 28, 28), MaxWidth = 720 };
        var heading = Text("Settings");
        heading.FontSize = 28;
        heading.FontWeight = SemiBold;
        heading.Margin = new Thickness(0, 0, 0, 12);
        stack.Children.Add(heading);

        stack.Children.Add(Card("Open at login", "Starts Varlatch in the notification area when you sign in to Windows.",
                                launchAtLogin, loginNote));

        sessionLength.Items.Add(new ComboBoxItem { Content = "Server default (12 hours)", Tag = 0 });
        for (var hours = 1; hours <= 24; hours++)
            sessionLength.Items.Add(new ComboBoxItem { Content = hours == 1 ? "1 hour" : $"{hours} hours", Tag = hours });
        stack.Children.Add(Section("Sign-in"));
        stack.Children.Add(Card("Session length",
                                "How long a new sign-in lasts. Applies to every sign-in from this app, renewals included.",
                                sessionLength));

        stack.Children.Add(Section("Sessions"));
        stack.Children.Add(Card("Check sessions every (seconds)",
                                "Checking sessions reads local files only; nothing goes over the network.", interval));
        stack.Children.Add(Card("Notify before a session expires",
                                "One notification when less than a fifth of a session is left, and one when it has expired.",
                                notifyExpiry));
        stack.Children.Add(Card("Show in the notification area when logged out",
                                "With this off, the icon goes away while no credentials are stored. Open Varlatch from Start to get back here.",
                                showWhenLoggedOut));
        stack.Children.Add(Card("Show localhost servers",
                                "Sessions on localhost or 127.* servers, for development.", showLocalhost));

        stack.Children.Add(Section("The varlatch CLI"));
        var browse = Button("Browse…", () => _ = Browse());
        var pathRow = new Grid { ColumnSpacing = 8 };
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pathRow.Children.Add(cliPath);
        Grid.SetColumn(browse, 1);
        pathRow.Children.Add(browse);
        var pathStack = new StackPanel { Spacing = 8 };
        pathStack.Children.Add(Text("CLI path", "VarlatchBodyText"));
        pathStack.Children.Add(Caption(
            @"Leave it empty to use %LOCALAPPDATA%\Programs\Varlatch\varlatch.cjs, or varlatch on your PATH. " +
            "A .cjs file runs with Node.js; a .cmd, .exe, or .ps1 runs as it is."));
        pathStack.Children.Add(pathRow);
        pathStack.Children.Add(cliInUse);
        stack.Children.Add(new Border { Style = Style("VarlatchSettingsCard"), Child = pathStack });
        stack.Children.Add(Card("Check for new CLI releases",
                                "Asks GitHub for the latest release, anonymously, at most once an hour.", checkUpdates));

        var about = Caption(AppVersion, select: true);
        about.Margin = new Thickness(0, 16, 0, 0);
        stack.Children.Add(about);
        var links = Buttons(false,
            LinkButton(Ui.Glyphs.Link, "Settings file", () => AppController.OpenUri(app.Settings.Path)),
            LinkButton(Ui.Glyphs.Link, "Log", () => AppController.OpenUri(Log.FilePath)));
        links.Margin = new Thickness(-8, 0, 0, 0);
        stack.Children.Add(links);

        Content = new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        Load();
        launchAtLogin.Toggled += (_, _) => Apply();
        sessionLength.SelectionChanged += (_, _) => Apply();
        interval.ValueChanged += (_, _) => Apply();
        notifyExpiry.Toggled += (_, _) => Apply();
        showWhenLoggedOut.Toggled += (_, _) => Apply();
        showLocalhost.Toggled += (_, _) => Apply();
        checkUpdates.Toggled += (_, _) => Apply();
        // Applied on Enter or on leaving the box, not on every keystroke.
        cliPath.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) Apply();
        };
        cliPath.LostFocus += (_, _) => Apply();
        app.Changed += Refresh;
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) Refresh();
        };
        Closed += (_, _) => app.Changed -= Refresh;
    }

    /// <summary>"Varlatch for Windows 0.1.0 (97d7b10)".</summary>
    public static string AppVersion
    {
        get
        {
            var informational = typeof(SettingsWindow).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?";
            var parts = informational.Split('+', 2);
            return "Varlatch for Windows " + parts[0] + (parts.Length > 1 && parts[1].Length > 0 ? $" ({parts[1]})" : "");
        }
    }

    public void Present()
    {
        var scale = Native.GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        if (scale <= 0) scale = 1;
        if (!AppWindow.IsVisible) AppWindow.Resize(new SizeInt32((int)(620 * scale), (int)(760 * scale)));
        Activate();
        Native.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private static ToggleSwitch Toggle() => new() { OnContent = "On", OffContent = "Off", MinWidth = 0 };

    private static TextBlock Section(string title)
    {
        var text = Text(title);
        text.FontWeight = SemiBold;
        text.Margin = new Thickness(2, 16, 0, 4);
        return text;
    }

    /// <summary>A setting: its name and what it does on the left, its control on the right.</summary>
    private static Border Card(string title, string description, FrameworkElement control, UIElement? below = null)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Text(title));
        text.Children.Add(Caption(description));
        if (below is not null) text.Children.Add(below);
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, title);
        return new Border { Style = Style("VarlatchSettingsCard"), Child = grid };
    }

    private void Load()
    {
        loading = true;
        var p = app.Store.Preferences;
        launchAtLogin.IsOn = p.LaunchAtLogin;
        sessionLength.SelectedIndex = p.SessionHours;
        interval.Value = p.RefreshInterval;
        notifyExpiry.IsOn = p.NotifyExpiry;
        showWhenLoggedOut.IsOn = p.ShowWhenLoggedOut;
        showLocalhost.IsOn = p.ShowLocalhost;
        cliPath.Text = p.CliPath;
        checkUpdates.IsOn = p.CheckUpdates;
        loading = false;
        Refresh();
    }

    private void Apply()
    {
        if (loading) return;
        var hours = sessionLength.SelectedItem is ComboBoxItem { Tag: int h } ? h : 0;
        var seconds = double.IsNaN(interval.Value) ? Preferences.DefaultRefreshInterval : (int)interval.Value;
        app.Save(new Preferences(
            refreshInterval: seconds,
            cliPath: cliPath.Text.Trim(),
            notifyExpiry: notifyExpiry.IsOn,
            showWhenLoggedOut: showWhenLoggedOut.IsOn,
            showLocalhost: showLocalhost.IsOn,
            sessionHours: hours,
            checkUpdates: checkUpdates.IsOn,
            launchAtLogin: launchAtLogin.IsOn));
        Refresh();
    }

    /// <summary>What the app found: the CLI in use, and whether Windows will open the app at login.</summary>
    private void Refresh()
    {
        var store = app.Store;
        if (store.CliCommand is { } command)
        {
            var kind = VarlatchKit.CliInstall.Kind(command) switch
            {
                CliInstallKind.Release => "release build",
                CliInstallKind.Checkout => "source checkout",
                _ => "custom",
            };
            var version = store.CliVersion is { } v ? $" {v}" : "";
            var node = command.Script is not null && store.NodeVersion is { } n ? $", on Node.js {n}" : "";
            cliInUse.Text = $"In use: {PanelView.Abbreviated(command.Path)}{version}, {kind}{node}";
            cliInUse.Style = Style("VarlatchCaptionText");
        }
        else
        {
            cliInUse.Text = store.CliState switch
            {
                CliState.PathUnusable => "In use: none. The path above cannot run.",
                CliState.NodeMissing => "In use: none. Node.js 22 or newer is needed to run it.",
                _ => "In use: none. The CLI was not found.",
            };
            cliInUse.Style = Style("VarlatchErrorText");
        }

        if (!app.IsInstalled)
        {
            loginNote.Text = "Applies to an installed copy of Varlatch, not to this build.";
            loginNote.Visibility = Visibility.Visible;
        }
        else if (launchAtLogin.IsOn && app.LoginItem.Current == LaunchAtLogin.Status.DisabledByUser)
        {
            loginNote.Text = "Turned off in Settings > Apps > Startup. Turn Varlatch on there to open it at login.";
            loginNote.Visibility = Visibility.Visible;
        }
        else
        {
            loginNote.Visibility = Visibility.Collapsed;
        }
    }

    private async Task Browse()
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        foreach (var type in new[] { ".cjs", ".js", ".cmd", ".exe", ".ps1", "*" }) picker.FileTypeFilter.Add(type);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        cliPath.Text = file.Path;
        Apply();
    }
}
