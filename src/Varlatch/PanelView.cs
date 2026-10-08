using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using VarlatchKit;
using Windows.System;
using static Varlatch.Ui;

namespace Varlatch;

/// <summary>
/// The panel above the tray icon: one row per session with a live countdown
/// and its actions, or what stands in the way. The only network requests
/// are the ones these buttons start.
/// </summary>
internal sealed class PanelView
{
    public const double Width = 360;

    private readonly AppController app;

    private readonly Button verify;
    private readonly Button dashboard;
    private readonly StackPanel body = new() { Padding = new Thickness(0, 6, 0, 6) };
    private readonly TextBlock version = Caption("");
    private readonly TextBlock versionUpdate = Text("", "VarlatchExpiringText");
    private readonly TextBlock verifyError = new() { Style = Style("VarlatchErrorText"), Margin = new Thickness(16, 2, 16, 4), IsTextSelectionEnabled = true };
    private readonly Dictionary<string, SessionRow> rows = [];
    private readonly Dictionary<string, UIElement> messages = [];
    private readonly PendingSignInCard pending;
    private readonly ConnectForm connect;
    private readonly Border trayHint;
    private Button? addServer;
    private bool connecting;

    public Grid Root { get; }

    public PanelView(AppController app)
    {
        this.app = app;
        pending = new PendingSignInCard(app);
        connect = new ConnectForm(app, () =>
        {
            connecting = false;
            Render();
        });
        trayHint = TrayHint();

        Root = new Grid { Width = Width };
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Header: the mark, the name, and with sessions, Verify and Dashboard.
        var header = new Grid { Padding = new Thickness(16, 12, 12, 12), ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var mark = new BitmapIcon
        {
            UriSource = new Uri("ms-appx:///Assets/TrayMark.png"),
            ShowAsMonochrome = true,
            Style = Style("VarlatchSecondaryIcon"),
            Width = 18,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
        };
        header.Children.Add(mark);
        var name = Title("Varlatch");
        name.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(name, 1);
        header.Children.Add(name);
        verify = Button("Verify", app.Verify, tip: "Check each stored credential with its server");
        dashboard = Button("Dashboard", () => app.OpenDashboard());
        var headerButtons = Buttons(true, verify, dashboard);
        Grid.SetColumn(headerButtons, 2);
        header.Children.Add(headerButtons);
        Root.Children.Add(header);

        var top = Divider();
        Grid.SetRow(top, 1);
        Root.Children.Add(top);

        var scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Grid.SetRow(scroll, 2);
        Root.Children.Add(scroll);

        var bottom = Divider();
        Grid.SetRow(bottom, 3);
        Root.Children.Add(bottom);

        // Footer: the CLI version (and a newer one), Settings, and Quit.
        var footer = new Grid { Padding = new Thickness(16, 6, 8, 6) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var versions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        version.TextWrapping = TextWrapping.NoWrap;
        versionUpdate.TextWrapping = TextWrapping.NoWrap;
        versions.Children.Add(version);
        versions.Children.Add(versionUpdate);
        footer.Children.Add(versions);
        var footerButtons = Buttons(true,
            IconButton(Glyphs.Settings, "Settings", app.OpenSettings),
            IconButton(Glyphs.Power, "Quit Varlatch", app.Quit));
        footerButtons.Spacing = 2;
        Grid.SetColumn(footerButtons, 1);
        footer.Children.Add(footerButtons);
        Grid.SetRow(footer, 4);
        Root.Children.Add(footer);

    }

    /// <summary>Puts the focus in the address form, when it shows.</summary>
    public bool FocusForm() => connect.Root.Parent is not null && connect.Focus();

    /// <summary>The panel closed: the address form goes with it.</summary>
    public void Closed()
    {
        connecting = false;
    }

    public void Render()
    {
        var store = app.Store;
        var signIn = app.SignIn;
        var now = DateTimeOffset.Now;

        var withSessions = store.CliState is CliState.Ready && store.Servers.Count > 0;
        verify.Visibility = dashboard.Visibility = withSessions ? Visibility.Visible : Visibility.Collapsed;
        verify.IsEnabled = !store.Verifying;
        verify.Content = store.Verifying ? VerifyingContent() : "Verify";
        ToolTipService.SetToolTip(dashboard, $"Open {(store.KnownServer is { } known ? Sessions.Host(known) : "the server")} in your browser");

        var children = new List<UIElement>();
        if (store.VerifyError is { } error)
        {
            verifyError.Text = error;
            children.Add(verifyError);
        }
        if (ShowTrayHint) children.Add(trayHint);
        if (!signIn.IsIdle)
        {
            pending.Update(signIn.State, now);
            children.Add(pending.Root);
        }
        children.AddRange(Content(now));
        if (app.AvailableRelease is { } release)
        {
            children.Add(Cached($"release-divider", () => Divider(16, 4)));
            children.Add(Cached($"release:{release}:{app.UpdateFor(release)}", () => UpdateView(release)));
        }
        SetChildren(body, children);

        if (store.CliVersion is { } cli)
        {
            version.Text = $"CLI {cli}";
            versionUpdate.Text = app.AvailableRelease is { } newer ? $" · {newer} available" : "";
        }
        else
        {
            version.Text = store.NodeVersion is null ? "" : $"Node.js {store.NodeVersion}";
            versionUpdate.Text = "";
        }
    }

    private object? verifyingContent;

    private object VerifyingContent()
    {
        if (verifyingContent is not null) return verifyingContent;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(Spinner(12));
        content.Children.Add(new TextBlock { Text = "Verifying" });
        return verifyingContent = content;
    }

    private UIElement Cached(string key, Func<UIElement> make)
    {
        if (!messages.TryGetValue(key, out var element)) messages[key] = element = make();
        return element;
    }

    private IEnumerable<UIElement> Content(DateTimeOffset now)
    {
        var store = app.Store;
        switch (store.CliState)
        {
            case CliState.Unknown:
                yield return Cached("loading", () => Loading());
                yield break;
            case CliState.Missing missing:
                yield return Cached("missing:" + string.Join("|", missing.Searched), () => InstallCliView(missing.Searched));
                yield break;
            case CliState.PathUnusable unusable:
                yield return Cached("unusable:" + unusable.Path, () => Message(Glyphs.Terminal, "The CLI set in Settings cannot run",
                    Abbreviated(unusable.Path), error: true, Button("Settings", app.OpenSettings)));
                yield break;
            case CliState.NodeMissing node:
                yield return Cached($"node:{node.Path}:{node.Found}", () => NodeView(node));
                yield break;
            case CliState.Unsupported:
                yield return Cached("unsupported", () => Message(Glyphs.Warning, "This varlatch CLI is too old",
                    "It has no status command. Update the CLI to use it here.", error: true));
                yield break;
            case CliState.Unavailable unavailable:
                yield return Cached("unavailable:" + unavailable.Detail, () => Message(Glyphs.Warning, "The varlatch CLI did not answer",
                    unavailable.Detail, error: true));
                yield break;
        }

        // Ready.
        var servers = store.Servers;
        foreach (var server in servers)
        {
            if (!rows.TryGetValue(server.Server, out var row)) rows[server.Server] = row = new SessionRow(app);
            row.Update(server, store.ProbeFor(server), now);
            yield return row.Root;
        }
        foreach (var gone in rows.Keys.Except(servers.Select(s => s.Server)).ToList()) rows.Remove(gone);

        var connectShown = app.SignIn.IsIdle && (connecting || (servers.Count == 0 && store.KnownServer is null));
        if (servers.Count == 0 && store.KnownServer is { } known && !connectShown)
        {
            yield return SignedOut(known);
        }
        if (connectShown)
        {
            connect.Update(cancellable: servers.Count > 0 || store.KnownServer is not null, another: servers.Count > 0);
            yield return connect.Root;
        }
        else if (servers.Count > 0 && app.SignIn.IsIdle)
        {
            addServer ??= AddServerButton();
            yield return addServer;
        }
    }

    private Button AddServerButton()
    {
        var button = LinkButton(Glyphs.Add, "Add server", () =>
        {
            connecting = true;
            Render();
        }, tip: "Sign in to another Varlatch server");
        button.HorizontalAlignment = HorizontalAlignment.Right;
        button.Margin = new Thickness(0, 0, 10, 0);
        return button;
    }

    private string signedOutKey = "";
    private UIElement? signedOutView;

    private UIElement SignedOut(string known)
    {
        var key = $"{known}:{app.SignIn.IsIdle}:{app.DeviceSignIn}";
        if (signedOutView is not null && key == signedOutKey) return signedOutView;
        signedOutKey = key;
        var buttons = new List<UIElement>();
        if (app.SignIn.IsIdle)
        {
            buttons.Add(Button("Log in", () => app.SignInTo(known), accent: true));
            if (app.DeviceSignIn)
                buttons.Add(Button("Other device", () => app.SignInFromAnotherDevice(known),
                                   tip: "Approve with a passkey on another device, such as a phone"));
            buttons.Add(Button("Other server", () =>
            {
                connecting = true;
                Render();
            }));
        }
        return signedOutView = Message(Glyphs.Contact, $"Not logged in to {Sessions.Host(known)}", null, false, buttons.ToArray());
    }

    private static UIElement Loading()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(16, 10, 16, 10) };
        row.Children.Add(Spinner());
        row.Children.Add(Text("Reading sessions…"));
        return row;
    }

    /// <summary>A short explanation in place of the sessions, with room for buttons.</summary>
    public static Grid Message(string glyph, string title, string? detail, bool error, params UIElement[] actions)
    {
        var grid = new Grid { Padding = new Thickness(16, 8, 16, 8), ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Glyph(glyph, 16, error ? "VarlatchErrorGlyph" : "VarlatchSecondaryGlyph");
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 0, 0);
        grid.Children.Add(icon);
        var stack = new StackPanel { Spacing = 4 };
        var heading = Text(title);
        heading.FontWeight = SemiBold;
        stack.Children.Add(heading);
        if (detail is not null) stack.Children.Add(Caption(detail, error, select: true));
        if (actions.Length > 0)
        {
            var row = Buttons(false, actions);
            row.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(row);
        }
        Grid.SetColumn(stack, 1);
        grid.Children.Add(stack);
        return grid;
    }

    /// <summary>No CLI where the app looks: what it is for, and installing it.</summary>
    private UIElement InstallCliView(IReadOnlyList<string> searched)
    {
        var view = Message(Glyphs.Terminal, "The varlatch CLI is not installed",
            "Varlatch for Windows shows and renews the sessions of the varlatch CLI. Install it in Terminal: it asks for " +
            "your server's address to install the CLI version your server runs, and checks the download before installing it.",
            false,
            Button("Install in Terminal", app.InstallCli, accent: true),
            Button("Learn more", () => app.Open("https://github.com/varlatch/varlatch/blob/main/docs/getting-started.md")));
        var stack = (StackPanel)view.Children[1];
        var looked = Caption("Looked for it in " + List(searched.Select(Abbreviated).ToList())
                             + ". Installed somewhere else? Set its path in Settings.");
        looked.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(looked);
        return view;
    }

    private UIElement NodeView(CliState.NodeMissing node)
    {
        var title = node.Found is { } found ? $"Node.js {found} is too old" : "Node.js is not installed";
        var detail = "The varlatch CLI runs on Node.js 22 or newer"
                     + (AppController.Winget is null ? "." : $", which winget installs as {AppController.NodePackage}.");
        var install = AppController.Winget is null
            ? Button("Get Node.js", app.InstallNode, accent: true)
            : Button("Install in Terminal", app.InstallNode, accent: true);
        return Message(Glyphs.Code, title, detail, false, install);
    }

    /// <summary>A newer CLI release (with release checks on): how to update this CLI.</summary>
    private UIElement UpdateView(ReleaseVersion release)
    {
        var update = app.UpdateFor(release);
        var detail = update == AppController.Update.SelfUpdate
            ? "Update: run varlatch self-update, which checks the download and asks before replacing anything."
            : "Update it the way you installed it.";
        var buttons = new List<UIElement> { Button("Release notes", () => app.Open(ReleaseChecker.ReleaseUrl(release))) };
        if (update == AppController.Update.SelfUpdate) buttons.Add(Button("Update in Terminal", () => app.UpdateCli(release)));
        var view = Message(Glyphs.Download, $"CLI {release} is available", detail, false, buttons.ToArray());
        ((FontIcon)view.Children[0]).Style = Style("VarlatchExpiringGlyph");
        return view;
    }

    /// <summary>
    /// Windows 11 puts a new app's icon in the overflow menu (^) and an app
    /// cannot pin itself, so the panel says how, until it is pinned or the
    /// hint is dismissed.
    /// </summary>
    private bool ShowTrayHint =>
        !app.Settings.Flag("trayHintDismissed")
        && TrayIconVisibility.IsPromoted(new CurrentUserRegistry(), Environment.ProcessPath!) == false;

    private Border TrayHint()
    {
        var view = Message(Glyphs.Pin, "Keep Varlatch in view",
            "Windows put its icon in the ^ menu by the clock. Drag it onto the taskbar, or turn it on under Other system tray icons in the taskbar settings.",
            false,
            Button("Taskbar settings", () => AppController.OpenUri("ms-settings:taskbar")),
            Button("Got it", () =>
            {
                app.Settings.SetFlag("trayHintDismissed", true);
                Render();
            }));
        view.Padding = new Thickness(0);
        return new Border { Style = Style("VarlatchCard"), Margin = new Thickness(10, 4, 10, 6), Child = view };
    }

    /// <summary>"%LOCALAPPDATA%\…" for a path under it.</summary>
    public static string Abbreviated(string path)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return path.StartsWith(local + "\\", StringComparison.OrdinalIgnoreCase) ? "%LOCALAPPDATA%" + path[local.Length..] : path;
    }

    /// <summary>"a, b, and c".</summary>
    public static string List(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + ", and " + items[^1],
    };
}

/// <summary>One stored credential: its server, how long it has left, and what can be done with it.</summary>
internal sealed class SessionRow
{
    private readonly AppController app;
    private readonly Microsoft.UI.Xaml.Shapes.Ellipse dot = Dot("VarlatchOkDot");
    private readonly TextBlock host = Title("");
    private readonly TextBlock detail = Caption("");
    private readonly TextBlock probe = Caption("");
    private readonly Button renew;
    private readonly Button more;
    private readonly ProgressRing busy = Spinner();
    private readonly StackPanel actions;
    private ServerStatus server = new("");

    public Grid Root { get; } = new() { Padding = new Thickness(16, 6, 10, 6), ColumnSpacing = 10, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };

    public SessionRow(AppController app)
    {
        this.app = app;
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        dot.VerticalAlignment = VerticalAlignment.Center;
        Root.Children.Add(dot);

        var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(host);
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        detail.TextWrapping = probe.TextWrapping = TextWrapping.NoWrap;
        line.Children.Add(detail);
        line.Children.Add(probe);
        text.Children.Add(line);
        Grid.SetColumn(text, 1);
        Root.Children.Add(text);

        // Renewing is signing in again: the CLI revokes the old credential
        // only once the new one is saved.
        renew = Button("Renew", () => app.SignInTo(server.Server));
        more = IconButton(Glyphs.More, "More", () => { });
        more.Flyout = Menu();
        Root.ContextFlyout = Menu();
        actions = Buttons(true, renew, more, busy);
        actions.Spacing = 4;
        actions.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(actions, 2);
        Root.Children.Add(actions);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(dot, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
    }

    public void Update(ServerStatus status, Probe? result, DateTimeOffset now)
    {
        server = status;
        var health = status.Health(now);
        host.Text = Sessions.Host(status.Server);
        ToolTipService.SetToolTip(host, status.Server);
        detail.Text = Sessions.Detail(status, now);
        var (dotStyle, textStyle) = health switch
        {
            SessionHealth.Ok => ("VarlatchOkDot", "VarlatchCaptionText"),
            SessionHealth.Expiring => ("VarlatchExpiringDot", "VarlatchExpiringText"),
            _ => ("VarlatchErrorDot", "VarlatchErrorText"),
        };
        dot.Style = Style(dotStyle);
        detail.Style = Style(textStyle);
        detail.TextWrapping = TextWrapping.NoWrap;
        if (result is not null)
        {
            probe.Text = " · " + Sessions.ProbeText(result);
            probe.Style = Style(result.State == ProbeState.Valid ? "VarlatchCaptionText" : "VarlatchErrorText");
            probe.TextWrapping = TextWrapping.NoWrap;
        }
        else
        {
            probe.Text = "";
        }

        var loggingOut = app.LoggingOut.Contains(status.Server);
        busy.Visibility = loggingOut ? Visibility.Visible : Visibility.Collapsed;
        busy.IsActive = loggingOut;
        more.Visibility = loggingOut ? Visibility.Collapsed : Visibility.Visible;
        renew.Visibility = !loggingOut && app.SignIn.IsIdle ? Visibility.Visible : Visibility.Collapsed;
        renew.Content = health == SessionHealth.Expired ? "Log in" : "Renew";
        ToolTipService.SetToolTip(renew, health == SessionHealth.Expired ? null : "Sign in again for a fresh session");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Root, $"{host.Text}, {detail.Text}{probe.Text}");
    }

    /// <summary>Made again each time it opens, so it matches what can be done now.</summary>
    private MenuFlyout Menu()
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            if (app.DeviceSignIn && app.SignIn.IsIdle)
            {
                menu.Items.Add(Item("Sign in from another device", () => app.SignInFromAnotherDevice(server.Server)));
                menu.Items.Add(new MenuFlyoutSeparator());
            }
            menu.Items.Add(Item("Open dashboard", () => app.OpenDashboard(server.Server)));
            menu.Items.Add(Item("Copy address", () => app.Copy(server.Server)));
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("Log out", () => app.Logout(server.Server)));
        };
        return menu;
    }

    private static MenuFlyoutItem Item(string text, Action click)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => click();
        return item;
    }
}

/// <summary>
/// A sign-in under way. In the browser: the link, for when the page opened
/// in the wrong browser (or not at all), and another device instead. From
/// another device: the address, the code to type there, and a QR code of
/// the address for a phone's camera.
/// </summary>
internal sealed class PendingSignInCard
{
    private readonly AppController app;
    private readonly TextBlock heading = Text("");
    private readonly TextBlock instructions = Caption("");
    private readonly StackPanel content = new() { Spacing = 8 };
    private readonly Grid device = new() { ColumnSpacing = 14 };
    private readonly Image qr = new() { Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
    private readonly Border qrFrame;
    private readonly TextBlock address = Caption("", select: true);
    private readonly TextBlock code = Text("", "VarlatchCodeText");
    private readonly TextBlock expiry = Caption("");
    private readonly StackPanel buttons = Buttons(true);
    private readonly Button openLink;
    private readonly Button otherDevice;
    private readonly Button copyCode;
    private readonly Button openDevice;
    private readonly Button cancel;
    private string? qrFor;
    private double qrScale;
    private SignInState state = new SignInState.Idle();

    public Border Root { get; }

    public PendingSignInCard(AppController app)
    {
        this.app = app;
        heading.FontWeight = SemiBold;
        heading.TextTrimming = TextTrimming.CharacterEllipsis;
        heading.TextWrapping = TextWrapping.NoWrap;
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        top.Children.Add(Spinner());
        top.Children.Add(heading);
        content.Children.Add(top);
        content.Children.Add(instructions);

        qrFrame = new Border { Style = Style("VarlatchQrFrame"), Child = qr, VerticalAlignment = VerticalAlignment.Center };
        device.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        device.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        device.Children.Add(qrFrame);
        var codeStack = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        address.TextWrapping = TextWrapping.Wrap;
        codeStack.Children.Add(address);
        codeStack.Children.Add(code);
        codeStack.Children.Add(expiry);
        Grid.SetColumn(codeStack, 1);
        device.Children.Add(codeStack);
        content.Children.Add(device);

        openLink = Button("Open link", () =>
        {
            if (state is SignInState.Browser { Link: { } link }) app.Open(link);
        }, tip: "Open the sign-in page again, for when it opened in the wrong browser");
        otherDevice = Button("Other device", () =>
        {
            if (state.Target is { } server) app.SignInFromAnotherDevice(server);
        }, tip: "Approve with a passkey on another device, such as a phone");
        copyCode = Button("Copy code", CopyCode);
        openDevice = Button("Open link", () =>
        {
            if (state is SignInState.Device { Code: { } c }) app.Open(c.VerificationUri);
        }, tip: "Open the address here, to approve in this browser");
        cancel = Button("Cancel", app.CancelSignIn);
        content.Children.Add(buttons);

        Root = new Border { Style = Style("VarlatchCard"), Margin = new Thickness(10, 4, 10, 6), Child = content };
    }

    private void CopyCode()
    {
        if (state is not SignInState.Device { Code: { } c }) return;
        app.Copy(c.UserCode);
        copyCode.Content = "Copied";
        app.After(TimeSpan.FromSeconds(1.5), () => copyCode.Content = "Copy code");
    }

    public void Update(SignInState next, DateTimeOffset now)
    {
        state = next;
        if (next.Target is { } server) heading.Text = $"Signing in to {Sessions.Host(server)}";
        var shown = new List<UIElement>();
        switch (next)
        {
            case SignInState.Browser browser:
                instructions.Text = "Finish in your browser.";
                device.Visibility = Visibility.Collapsed;
                openLink.IsEnabled = browser.Link is not null;
                shown.Add(openLink);
                if (app.DeviceSignIn) shown.Add(otherDevice);
                break;
            case SignInState.Device { Code: null }:
                instructions.Text = "Getting a sign-in code…";
                device.Visibility = Visibility.Collapsed;
                break;
            case SignInState.Device { Code: { } c }:
                instructions.Text = "On any device, such as a phone: open this address, sign in with your passkey, and enter the code.";
                device.Visibility = Visibility.Visible;
                address.Text = DisplayAddress(c.VerificationUri);
                code.Text = c.UserCode;
                expiry.Text = c.ExpiryText(now);
                UpdateQr(c.VerificationUri);
                shown.Add(copyCode);
                shown.Add(openDevice);
                break;
        }
        shown.Add(cancel);
        SetChildren(buttons, shown);
    }

    /// <summary>
    /// The QR code at a whole number of screen pixels per module, so its
    /// edges stay sharp for a camera at any display scale.
    /// </summary>
    private void UpdateQr(string text)
    {
        var scale = qr.XamlRoot?.RasterizationScale ?? 1.0;
        if (qrFor == text && Math.Abs(qrScale - scale) < 0.01) return;
        qrFor = text;
        qrScale = scale;
        var modules = QrCode.Modules(text).GetLength(0);
        var perModule = Math.Max(2, (int)Math.Round(96 * scale / modules));
        var (pixels, side) = QrCode.Bgra(text, perModule);
        var bitmap = new WriteableBitmap(side, side);
        using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels, 0, pixels.Length);
        bitmap.Invalidate();
        qr.Source = bitmap;
        qr.Width = qr.Height = side / scale;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(qr, $"QR code of {text}");
    }

    /// <summary>"vl.example.com/device": the address without its https://.</summary>
    public static string DisplayAddress(string address) =>
        address.StartsWith("https://", StringComparison.Ordinal) ? address["https://".Length..] : address;
}

/// <summary>The server's address, for the first sign-in or another server. Connect starts the browser sign-in.</summary>
internal sealed class ConnectForm
{
    private readonly AppController app;
    private readonly Action done;
    private readonly TextBlock heading = Text("");
    private readonly TextBox address = new() { PlaceholderText = "varlatch.example.com", MinWidth = 0 };
    private readonly Button cancel;
    private readonly TextBlock hint = Caption("");
    private bool cancellable;

    public StackPanel Root { get; } = new() { Spacing = 6, Padding = new Thickness(16, 8, 16, 8) };

    public ConnectForm(AppController app, Action done)
    {
        this.app = app;
        this.done = done;
        heading.FontWeight = SemiBold;
        Root.Children.Add(heading);
        var row = new Grid { ColumnSpacing = 6 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(address, "Server address");
        address.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                Connect();
                e.Handled = true;
            }
        };
        address.TextChanged += (_, _) => ShowHint(null);
        row.Children.Add(address);
        var go = Button("Connect", Connect, accent: true);
        Grid.SetColumn(go, 1);
        row.Children.Add(go);
        cancel = Button("Cancel", Cancel);
        Grid.SetColumn(cancel, 2);
        row.Children.Add(cancel);
        Root.Children.Add(row);
        Root.Children.Add(hint);
        ShowHint(null);
        address.Loaded += (_, _) => address.Focus(FocusState.Programmatic);
    }

    public bool Focus() => address.Focus(FocusState.Programmatic);

    public void Update(bool cancellable, bool another)
    {
        this.cancellable = cancellable;
        heading.Text = another ? "Connect another server" : "Connect to your Varlatch server";
        cancel.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowHint(string? error)
    {
        hint.Text = error ?? "Your browser opens for the passkey sign-in.";
        hint.Style = Style(error is null ? "VarlatchCaptionText" : "VarlatchErrorText");
    }

    private void Connect()
    {
        if (ServerAddress.Normalize(address.Text) is not { } server)
        {
            ShowHint("Give the server's address, such as varlatch.example.com");
            return;
        }
        app.Store.Remember([server]);
        app.SignInTo(server);
        address.Text = "";
        done();
    }

    private void Cancel()
    {
        if (!cancellable) return;
        address.Text = "";
        done();
    }
}
