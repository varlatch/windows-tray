namespace VarlatchKit;

/// <summary>How the CLI the app runs goes to a newer release.</summary>
public enum CliUpdateWay
{
    /// <summary><c>varlatch self-update &lt;version&gt;</c>: varlatch.exe.</summary>
    SelfUpdate,
    /// <summary>
    /// <c>install-cli.ps1 -Update</c>: the single file that Node.js runs. Its
    /// own self-update cannot go to a release that has varlatch.exe instead,
    /// so the script looks at what the release has.
    /// </summary>
    Installer,
    /// <summary><c>winget upgrade varlatch</c>.</summary>
    Winget,
    /// <summary>Release notes only: updated the way it was installed.</summary>
    Manual,
}

public static class CliUpdate
{
    public static CliUpdateWay Way(CliInstallKind? kind, CliCommand? command, ReleaseVersion? version)
    {
        if (kind == CliInstallKind.Winget) return CliUpdateWay.Winget;
        if (kind != CliInstallKind.Release || command is null || version is not { } v) return CliUpdateWay.Manual;
        // Older CLIs have no self-update, and development builds refuse it.
        if (v < ReleaseVersion.SelfUpdate || v.Suffix.EndsWith("dev", StringComparison.Ordinal)) return CliUpdateWay.Manual;
        return command.Script is null ? CliUpdateWay.SelfUpdate : CliUpdateWay.Installer;
    }

    /// <summary>
    /// The install script's update of the varlatch.cjs at <paramref name="path"/>
    /// to <paramref name="version"/> (null: the latest release), run by
    /// Windows PowerShell.
    /// </summary>
    /// <param name="node">The node.exe that runs it, when there is one.</param>
    public static IReadOnlyList<string> Installer(string powerShell, string script, string path,
                                                  ReleaseVersion? version, string? node)
    {
        List<string> command = [powerShell, "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Update", path];
        if (version is { } v) command.AddRange(["-Version", v.ToString()]);
        if (node is not null) command.AddRange(["-Node", node]);
        return command;
    }
}
