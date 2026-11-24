using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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
    private static readonly Regex SumsLine = new(@"^([0-9a-f]{64}) [ *](.+)$", RegexOptions.CultureInvariant);

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

    /// <summary>The native Windows asset name for <paramref name="version"/> on <paramref name="architecture"/>.</summary>
    public static string? NativeAssetName(ReleaseVersion version, Architecture architecture) => architecture switch
    {
        Architecture.X64 => $"varlatch-{version}-windows-amd64.exe",
        Architecture.Arm64 => $"varlatch-{version}-windows-arm64.exe",
        _ => null,
    };

    /// <summary>Whether <paramref name="sumsText"/> lists <paramref name="assetName"/> exactly once with a valid SHA-256.</summary>
    public static bool HasAssetInSums(string? sumsText, string? assetName)
    {
        if (string.IsNullOrEmpty(sumsText) || string.IsNullOrEmpty(assetName)) return false;
        var count = 0;
        foreach (var raw in sumsText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = SumsLine.Match(line);
            if (m.Success && string.Equals(m.Groups[2].Value, assetName, StringComparison.Ordinal)) count++;
        }
        return count == 1;
    }

    /// <summary>
    /// Resolves a native target release when a CLI cannot run because Node.js
    /// is missing or too old: carries both the wrapper/invoked <paramref name="path"/>
    /// and the resolved <paramref name="script"/> through, applies the normal
    /// eligibility rules (source version &gt;= 0.11.0 and not -dev when known,
    /// not a checkout, not winget/package-managed, release build, named
    /// <c>varlatch.cjs</c> directly or behind <c>varlatch.cmd</c>), and
    /// requires that the target release's <c>SHA256SUMS</c> lists the native
    /// asset for this PC's architecture.
    /// </summary>
    public static ReleaseVersion? NativeTargetWithoutNode(
        string path,
        string script,
        ReleaseVersion? sourceVersion,
        ReleaseVersion? latestRelease,
        bool nativeAssetInSums,
        Func<string, bool>? fileExists = null)
    {
        if (!nativeAssetInSums || latestRelease is not { } target || target < ReleaseVersion.NativeCli) return null;
        if (sourceVersion is { } sv && (sv < ReleaseVersion.SelfUpdate || sv.Suffix.EndsWith("dev", StringComparison.Ordinal)))
            return null;
        var exists = fileExists ?? File.Exists;
        var wrapperName = Path.GetFileName(path);
        var scriptName = Path.GetFileName(script);
        if (!scriptName.Equals("varlatch.cjs", StringComparison.OrdinalIgnoreCase)) return null;
        if (!wrapperName.Equals("varlatch.cjs", StringComparison.OrdinalIgnoreCase)
            && !wrapperName.Equals("varlatch.cmd", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!exists(script)) return null;
        // Reject package-managed or checkout copies on BOTH wrapper and resolved script.
        var scriptCmd = new CliCommand(path, "node.exe", [script], script);
        if (CliInstall.Kind(scriptCmd) != CliInstallKind.Release) return null;
        var wrapperCmd = new CliCommand(path, path, []);
        var wrapperKind = CliInstall.Kind(wrapperCmd);
        if (wrapperKind is CliInstallKind.Checkout or CliInstallKind.Winget) return null;
        return target;
    }

    /// <summary>
    /// Fetches <c>SHA256SUMS</c> for <paramref name="version"/> from GitHub
    /// anonymously and returns whether it lists the native Windows asset for
    /// <paramref name="architecture"/>.
    /// </summary>
    public static async Task<bool> CheckNativeAssetAsync(ReleaseVersion version, Architecture architecture, HttpClient? client = null)
    {
        if (NativeAssetName(version, architecture) is not { } asset) return false;
        using var handler = client is null ? new HttpClientHandler { UseCookies = false, UseDefaultCredentials = false, Credentials = null } : null;
        using var owned = handler is not null ? new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) } : null;
        var http = client ?? owned!;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://github.com/{ReleaseChecker.Repository}/releases/download/v{version}/SHA256SUMS");
            req.Headers.UserAgent.ParseAdd("Varlatch-for-Windows");
            using var res = await http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return false;
            var body = await res.Content.ReadAsStringAsync();
            return HasAssetInSums(body, asset);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// The install script's update of the varlatch.cjs at <paramref name="path"/>
    /// to <paramref name="version"/> (null: the latest release), run by
    /// Windows PowerShell.
    /// </summary>
    /// <param name="node">The node.exe that runs it, when there is one.</param>
    /// <param name="resultFile">Per-run result file written by the script; the unelevated app merges it into settings.json.</param>
    public static IReadOnlyList<string> Installer(string powerShell, string script, string path,
                                                  ReleaseVersion? version, string? node, string? resultFile = null)
    {
        List<string> command = [powerShell, "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-Update", path];
        if (version is { } v) command.AddRange(["-Version", v.ToString()]);
        if (node is not null) command.AddRange(["-Node", node]);
        if (!string.IsNullOrEmpty(resultFile)) command.AddRange(["-ResultFile", resultFile]);
        return command;
    }

    /// <summary>
    /// Reads a per-run migration result file written by <c>install-cli.ps1</c>,
    /// merges <c>migratedCli</c> into <paramref name="settings"/> atomically
    /// in the unelevated app, and removes the result file.
    /// </summary>
    public static bool TryMergeMigrationResult(string resultFile, SettingsFile settings, Func<string, bool>? fileExists = null)
    {
        var exists = fileExists ?? File.Exists;
        if (!exists(resultFile)) return false;
        string? migratedTo = null;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(resultFile)) as JsonObject;
            if (node?["migratedTo"] is JsonValue v && v.TryGetValue<string>(out var s)) migratedTo = s;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(migratedTo) || !exists(migratedTo)) return false;
        settings.Update(o => o[Preferences.Key.MigratedCli] = migratedTo);
        try { File.Delete(resultFile); } catch (IOException) { }
        return true;
    }
}
