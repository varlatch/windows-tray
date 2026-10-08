using Microsoft.Win32;

namespace VarlatchKit;

/// <summary>The keys and values launch at login reads and writes, injectable for the tests.</summary>
public interface IUserRegistry
{
    object? Get(string key, string name);
    void Set(string key, string name, string value);
    void Delete(string key, string name);
    IEnumerable<string> SubKeys(string key);
}

/// <summary><c>HKEY_CURRENT_USER</c>.</summary>
public sealed class CurrentUserRegistry : IUserRegistry
{
    public object? Get(string key, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key);
        return k?.GetValue(name);
    }

    public void Set(string key, string name, string value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(key);
        k.SetValue(name, value, RegistryValueKind.String);
    }

    public void Delete(string key, string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    public IEnumerable<string> SubKeys(string key)
    {
        using var k = Registry.CurrentUser.OpenSubKey(key);
        return k?.GetSubKeyNames() ?? [];
    }
}

/// <summary>
/// Opening at login: a value in the current user's <c>Run</c> key that
/// starts the installed app. Windows lists it in Settings &gt; Apps &gt;
/// Startup and in Task Manager, where it can be turned off; the app then
/// leaves it off and says so, as the macOS app does for a login item that
/// needs approval.
/// </summary>
public sealed class LaunchAtLogin(IUserRegistry registry, string executablePath)
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ValueName = "Varlatch";

    /// <summary>Started at login, the app opens no panel.</summary>
    public const string Argument = "--login-start";

    public enum Status
    {
        On,
        Off,
        /// <summary>Registered, but turned off in Settings or Task Manager.</summary>
        DisabledByUser,
    }

    public string Command => $"\"{executablePath}\" {Argument}";

    public Status Current
    {
        get
        {
            if (registry.Get(RunKey, ValueName) is not string) return Status.Off;
            // Settings and Task Manager keep their switch here: the first byte
            // is even when on, odd when off. No entry means on.
            if (registry.Get(ApprovedKey, ValueName) is byte[] { Length: > 0 } approved && (approved[0] & 1) == 1)
                return Status.DisabledByUser;
            return Status.On;
        }
    }

    /// <summary>Makes the registry match <paramref name="wanted"/>; a value for another copy of the app is replaced.</summary>
    public void Set(bool wanted)
    {
        if (wanted)
        {
            if (registry.Get(RunKey, ValueName) as string != Command) registry.Set(RunKey, ValueName, Command);
        }
        else
        {
            registry.Delete(RunKey, ValueName);
        }
    }

    /// <summary>
    /// Installed, rather than a build in a checkout, which should not start
    /// at login by itself: under Program Files or the user's Programs folder.
    /// </summary>
    public static bool IsInstalled(string executablePath, string localAppData, string programFiles)
    {
        var path = Path.GetFullPath(executablePath);
        return Under(path, Path.Combine(localAppData, "Programs")) || Under(path, programFiles);

        static bool Under(string path, string dir) =>
            path.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Whether the tray icon is in view: Windows 11 keeps a new app's icon in
/// the overflow menu (^) until it is pinned, and an app cannot pin itself.
/// Read from <c>HKCU\Control Panel\NotifyIconSettings</c>, where Windows keeps
/// each icon's <c>IsPromoted</c>.
/// </summary>
public static class TrayIconVisibility
{
    public const string SettingsKey = @"Control Panel\NotifyIconSettings";

    /// <summary>True when shown on the taskbar, false when in the overflow menu, null when Windows does not say.</summary>
    public static bool? IsPromoted(IUserRegistry registry, string executablePath)
    {
        foreach (var id in registry.SubKeys(SettingsKey))
        {
            var key = SettingsKey + "\\" + id;
            if (registry.Get(key, "ExecutablePath") is string path
                && string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase))
            {
                return registry.Get(key, "IsPromoted") is int promoted ? promoted != 0 : false;
            }
        }
        return null;
    }
}
