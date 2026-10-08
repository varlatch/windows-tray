using System.Security;
using System.Security.Cryptography;
using System.Text;
using VarlatchKit;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Varlatch;

/// <summary>
/// Windows notifications: expiry, with a button to renew or log in, and the
/// outcome of sign-ins and logouts. When notifications are off for the app,
/// it works the same, just silently.
/// </summary>
internal sealed class Notifier
{
    private static class Action
    {
        public const string Open = "open";
        public const string Renew = "renew";
        public const string Login = "login";
        public const string Device = "device";
        public const string Update = "update";
        public const string Notes = "notes";
    }

    private const string ExpiryGroup = "expiry";
    private AppController controller = null!;
    private ToastNotifier? notifier;

    public void Install(AppController app)
    {
        controller = app;
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "VarlatchLogo.png");
            ToastActivator.Install("Varlatch", icon, arguments => controller.Post(() => Answer(Parse(arguments))));
            notifier = ToastNotificationManager.CreateToastNotifier(ToastActivator.AppUserModelId);
        }
        catch (Exception e)
        {
            Log.Write("notifications unavailable: " + e);
        }
    }

    /// <summary>Whether Windows shows the app's notifications, for the debug state.</summary>
    public string Setting
    {
        get
        {
            if (notifier is null) return "unavailable";
            try
            {
                return notifier.Setting.ToString();
            }
            catch (Exception e)
            {
                return "unknown: " + e.Message;
            }
        }
    }

    // Posting

    /// <summary>One per server, so expired replaces expiring.</summary>
    private static string ServerTag(string server) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server)))[..16];

    /// <param name="device">The CLI can sign in from another device.</param>
    public void Post(IReadOnlyList<ExpiryEvent> events, bool device)
    {
        foreach (var e in events)
        {
            var expired = e.Health == SessionHealth.Expired;
            var buttons = new List<(string, string)> { (expired ? "Log in" : "Renew now", Args(expired ? Action.Login : Action.Renew, e.Server)) };
            if (device) buttons.Add(("Another device", Args(Action.Device, e.Server)));
            Show(e.Title, e.Body(), Args(Action.Open, e.Server), buttons, sound: expired, ServerTag(e.Server), ExpiryGroup);
            Record($"[{(expired ? "expired" : "expiring")}{(device ? "-device" : "")}] {e.Health.ToString().ToLowerInvariant()} {e.Server}");
        }
    }

    /// <param name="device">The CLI can sign in from another device, which a failed browser sign-in then offers.</param>
    public void Post(SignInOutcome outcome, bool device)
    {
        var (title, sound) = outcome switch
        {
            SignInOutcome.SignedIn => ("Logged in", false),
            SignInOutcome.Cancelled => ("Sign-in cancelled", false),
            SignInOutcome.Denied => ("Sign-in denied", true),
            SignInOutcome.CodeExpired => ("Sign-in code expired", true),
            _ => ("Sign-in failed", true),
        };
        var buttons = new List<(string, string)>();
        var category = "outcome";
        if (outcome is SignInOutcome.Failed { Browser: true } && device)
        {
            buttons.Add(("Use another device", Args(Action.Device, outcome.Server)));
            category = "failed-device";
        }
        Show(title, outcome.Text, Args(Action.Open, outcome.Server), buttons, sound, ServerTag(outcome.Server), "signin");
        if (outcome is SignInOutcome.SignedIn) Withdraw(ServerTag(outcome.Server), ExpiryGroup);
        Record($"[{category}] {outcome.Text}");
    }

    public void PostLogout(string server, string message, bool succeeded)
    {
        Show(succeeded ? "Logged out" : "Log out failed", message, Args(Action.Open, server), [], sound: !succeeded,
             ServerTag(server), "logout");
        Record(message);
    }

    /// <summary>A newer CLI release, once per release.</summary>
    public void PostRelease(ReleaseVersion version, ReleaseVersion? current, AppController.Update update)
    {
        var have = current is { } c ? $" (you have {c})" : "";
        var selfUpdate = update == AppController.Update.SelfUpdate;
        var body = selfUpdate ? $"Update with varlatch self-update{have}." : $"A new release of the CLI is out{have}.";
        var notes = $"action={Action.Notes}&version={version}";
        var buttons = new List<(string, string)>();
        if (selfUpdate) buttons.Add(("Update", $"action={Action.Update}&version={version}"));
        buttons.Add(("Release notes", notes));
        Show($"Varlatch CLI {version} is available", body, notes, buttons, sound: false, "release-" + version, "release");
        Record($"[release{(selfUpdate ? "-update" : "")}] Varlatch CLI {version} is available");
    }

    private static string Args(string action, string server) => $"action={action}&server={Uri.EscapeDataString(server)}";

    /// <summary>key=value&amp;… as the buttons carry them.</summary>
    internal static Dictionary<string, string> Parse(string arguments)
    {
        var result = new Dictionary<string, string>();
        foreach (var pair in arguments.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0) result[pair[..eq]] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return result;
    }

    private void Show(string title, string body, string launch, IReadOnlyList<(string Text, string Arguments)> buttons,
                      bool sound, string tag, string group)
    {
        if (notifier is null) return;
        try
        {
            var xml = new StringBuilder();
            xml.Append($"<toast launch=\"{Escape(launch)}\" activationType=\"foreground\">");
            xml.Append("<visual><binding template=\"ToastGeneric\">");
            xml.Append($"<text>{Escape(title)}</text><text>{Escape(body)}</text>");
            xml.Append("</binding></visual>");
            if (buttons.Count > 0)
            {
                xml.Append("<actions>");
                foreach (var (text, arguments) in buttons)
                    xml.Append($"<action content=\"{Escape(text)}\" arguments=\"{Escape(arguments)}\" activationType=\"foreground\"/>");
                xml.Append("</actions>");
            }
            if (!sound) xml.Append("<audio silent=\"true\"/>");
            xml.Append("</toast>");
            var document = new XmlDocument();
            document.LoadXml(xml.ToString());
            notifier.Show(new ToastNotification(document) { Tag = Limit(tag), Group = group });
        }
        catch (Exception e)
        {
            Log.Write("cannot notify: " + e.Message);
        }
    }

    private static string Escape(string text) => SecurityElement.Escape(text) ?? "";

    /// <summary>Windows keeps tags to 64 characters.</summary>
    private static string Limit(string tag) => tag.Length > 64 ? tag[..64] : tag;

    private static void Withdraw(string tag, string group)
    {
        try
        {
            ToastNotificationManager.History.Remove(Limit(tag), group, ToastActivator.AppUserModelId);
        }
        catch (Exception e)
        {
            Log.Write("cannot withdraw a notification: " + e.Message);
        }
    }

    /// <summary>Removes the expiry notifications of servers that are fine again or gone.</summary>
    public void WithdrawResolved(IReadOnlyList<ServerStatus> servers)
    {
        if (notifier is null) return;
        try
        {
            var stillBad = servers.Where(s => s.Health() != SessionHealth.Ok).Select(s => ServerTag(s.Server)).ToHashSet();
            var shown = ToastNotificationManager.History.GetHistory(ToastActivator.AppUserModelId);
            foreach (var n in shown.Where(n => n.Group == ExpiryGroup && !stillBad.Contains(n.Tag)).ToList())
                Withdraw(n.Tag, ExpiryGroup);
        }
        catch (Exception e)
        {
            Log.Write("cannot withdraw notifications: " + e.Message);
        }
    }

    /// <summary>For the uninstaller: the notifications go, and Windows forgets the app.</summary>
    public static void Uninstall()
    {
        try
        {
            ToastNotificationManager.History.Clear(ToastActivator.AppUserModelId);
        }
        catch (Exception e)
        {
            Log.Write("cannot clear notifications: " + e.Message);
        }
        ToastActivator.Uninstall();
    }

    private static void Record(string line) => DebugHooks.Record("notified", line);

    // Answers

    public void Answer(IDictionary<string, string> arguments)
    {
        var action = arguments.TryGetValue("action", out var a) ? a : Action.Open;
        var server = arguments.TryGetValue("server", out var s) ? s : null;
        var version = arguments.TryGetValue("version", out var v) ? ReleaseVersion.Parse(v) : null;
        DebugHooks.Record("answered", $"{action} {server ?? version?.ToString() ?? ""}".Trim());
        switch (action)
        {
            case Action.Renew or Action.Login when server is not null:
                controller.SignInTo(server);
                controller.ShowPanel();
                break;
            case Action.Device when server is not null:
                controller.SignInFromAnotherDevice(server);
                controller.ShowPanel();
                break;
            case Action.Update when version is { } update:
                controller.UpdateCli(update);
                break;
            case Action.Notes when version is { } notes:
                controller.Open(ReleaseChecker.ReleaseUrl(notes));
                break;
            default:
                controller.ShowPanel();
                break;
        }
    }
}
