namespace VarlatchKit;

/// <summary>Where the app keeps things.</summary>
public static class AppPaths
{
    /// <summary>
    /// <c>%LOCALAPPDATA%\Varlatch for Windows</c>: <c>settings.json</c>,
    /// <c>servers.json</c>, <c>update.json</c>, and with debug hooks on,
    /// <c>debug-state.json</c>. <c>VARLATCH_WINDOWS_STATE_DIR</c> points it
    /// elsewhere, for trying a build without touching the installed app's
    /// state.
    /// </summary>
    public static string StateDirectory =>
        Environment.GetEnvironmentVariable("VARLATCH_WINDOWS_STATE_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Varlatch for Windows");

    /// <summary><c>%LOCALAPPDATA%\Programs\Varlatch for Windows</c>, where <c>install.ps1</c> puts the app.</summary>
    public static string AppInstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Varlatch for Windows");
}
