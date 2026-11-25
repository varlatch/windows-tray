namespace VarlatchKit;

/// <summary>How the CLI is installed, which decides how it is updated.</summary>
public enum CliInstallKind
{
    /// <summary>
    /// A release's CLI: <c>varlatch.exe</c> (0.50 and later), or the single
    /// file that Node.js runs.
    /// </summary>
    Release,
    /// <summary>Installed with winget, which updates it.</summary>
    Winget,
    /// <summary>Built in a source checkout: updated there, with git.</summary>
    Checkout,
    /// <summary>Anything else.</summary>
    Custom,
}

public static class CliInstall
{
    /// <param name="command">The CLI the app runs.</param>
    public static CliInstallKind Kind(CliCommand command)
    {
        var code = command.CodePath;
        string full;
        try
        {
            full = Path.GetFullPath(code);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return CliInstallKind.Custom;
        }
        for (var dir = Path.GetDirectoryName(full); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
                return CliInstallKind.Checkout;
        }
        // A copy winget installed is winget's, told as `varlatch self-update` tells it.
        var slashed = full.Replace('\\', '/').ToLowerInvariant();
        if (slashed.Contains("/winget/packages/") || slashed.Contains("/winget/links/")) return CliInstallKind.Winget;
        // The single file names itself in its first lines; the CLI as a
        // Windows program is the native one.
        var head = Shim.Head(full, 512);
        if (command.Script is null && Path.GetExtension(full).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return head.StartsWith("MZ") ? CliInstallKind.Release : CliInstallKind.Custom;
        return head.Contains("Varlatch CLI") ? CliInstallKind.Release : CliInstallKind.Custom;
    }

    /// <summary>Whether this user can replace <paramref name="path"/> without an administrator.</summary>
    public static bool DirectoryWritable(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is null) return false;
        var probe = Path.Combine(dir, $".varlatch-write-test-{Environment.ProcessId}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
