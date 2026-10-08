namespace VarlatchKit;

/// <summary>How the CLI is installed, which decides how it is updated.</summary>
public enum CliInstallKind
{
    /// <summary>The single-file release build: <c>varlatch self-update</c> (0.11.0+).</summary>
    Release,
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
        // The release build names itself in its first lines.
        return Shim.Head(full, 400).Contains("Varlatch CLI") ? CliInstallKind.Release : CliInstallKind.Custom;
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
