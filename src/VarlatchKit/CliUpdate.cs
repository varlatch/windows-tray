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
        if (command.Script is null) return CliUpdateWay.SelfUpdate;
        // The install script replaces varlatch.cjs (directly or via its shim) only.
        if (!Path.GetFileName(command.CodePath).Equals("varlatch.cjs", StringComparison.OrdinalIgnoreCase))
            return CliUpdateWay.Manual;
        return CliUpdateWay.Installer;
    }

    /// <summary>
    /// Resolves a native target release when a CLI cannot run because Node.js
    /// is missing or too old: applies the same eligibility checks as a normal
    /// update (release build, not checkout/winget/custom, named
    /// <c>varlatch.cjs</c> or its <c>varlatch.cmd</c> shim) and requires a
    /// resolved native target (&gt;= 0.50.0). Returns null otherwise.
    /// </summary>
    public static ReleaseVersion? NativeTargetWithoutNode(string path, ReleaseVersion? latestRelease, Func<string, bool>? fileExists = null)
    {
        if (latestRelease is not { } target || target < ReleaseVersion.NativeCli) return null;
        var exists = fileExists ?? File.Exists;
        var name = Path.GetFileName(path);
        string scriptPath;
        if (name.Equals("varlatch.cjs", StringComparison.OrdinalIgnoreCase))
        {
            scriptPath = path;
        }
        else if (name.Equals("varlatch.cmd", StringComparison.OrdinalIgnoreCase))
        {
            if (Shim.Script(path, exists) is not { } s || !Path.GetFileName(s).Equals("varlatch.cjs", StringComparison.OrdinalIgnoreCase))
                return null;
            scriptPath = s;
        }
        else
        {
            return null;
        }
        if (!exists(scriptPath)) return null;
        var command = new CliCommand(path, "node.exe", [scriptPath], scriptPath);
        return CliInstall.Kind(command) == CliInstallKind.Release ? target : null;
    }

    /// <summary>
    /// The install script's update of the varlatch.cjs at <paramref name="path"/>
    /// to <paramref name="version"/> (null: the latest release), run by
    /// Windows PowerShell.
    /// </summary>
    /// <param name="node">The node.exe that runs it, when there is one.</param>
    /// <param name="settingsPath">settings.json where a successful migration is recorded.</param>
    public static IReadOnlyList<string> Installer(string powerShell, string script, string path,
                                                  ReleaseVersion? version, string? node, string? settingsPath = null)
    {
        List<string> command = [powerShell, "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Update", path];
        if (version is { } v) command.AddRange(["-Version", v.ToString()]);
        if (node is not null) command.AddRange(["-Node", node]);
        if (!string.IsNullOrEmpty(settingsPath)) command.AddRange(["-Settings", settingsPath]);
        return command;
    }
}
