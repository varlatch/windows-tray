using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VarlatchKit;

/// <summary>
/// How to run the CLI: <see cref="Path"/> is the CLI as installed (what the
/// app shows), <see cref="Executable"/> and <see cref="Prefix"/> are what it
/// starts. The release CLI is a single <c>.cjs</c> file that Node.js runs,
/// so for it, and for a <c>varlatch.cmd</c> shim that runs one, the app
/// starts <c>node.exe</c> on the script itself: a cancel then ends the CLI
/// rather than a <c>cmd.exe</c> in front of it.
/// </summary>
public sealed record CliCommand(string Path, string Executable, IReadOnlyList<string> Prefix, string? Script = null)
{
    public bool Equals(CliCommand? other) =>
        other is not null && Path == other.Path && Executable == other.Executable
        && Prefix.SequenceEqual(other.Prefix) && Script == other.Script;

    public override int GetHashCode() => HashCode.Combine(Path, Executable, Script);

    /// <summary>The file that holds the CLI's code: the script Node.js runs, or the CLI itself.</summary>
    public string CodePath => Script ?? Path;
}

public abstract record CliResolution
{
    /// <summary>The CLI to run.</summary>
    public sealed record Found(CliCommand Command) : CliResolution;

    /// <summary>The path set in the settings does not exist or cannot run.</summary>
    public sealed record OverrideUnusable(string Path) : CliResolution;

    /// <summary>No CLI in any of these places.</summary>
    public sealed record NotFound(IReadOnlyList<string> Searched) : CliResolution
    {
        public bool Equals(NotFound? other) => other is not null && Searched.SequenceEqual(other.Searched);
        public override int GetHashCode() => Searched.Count;
    }

    /// <summary>The CLI is a Node.js script, and there is no Node.js to run it.</summary>
    public sealed record NodeMissing(string Path) : CliResolution;
}

/// <summary>
/// What the app knows about the machine, injectable for the tests: the
/// environment the CLI inherits, the directories to look for programs in,
/// and where things are installed.
/// </summary>
public sealed record SystemContext(
    IReadOnlyDictionary<string, string> Environment,
    Func<IReadOnlyList<string>> SearchPath,
    string LocalAppData,
    string ProgramFiles,
    Func<string, bool> FileExists)
{
    /// <summary>
    /// This machine. The search path is read again each time: an app started
    /// at login keeps the <c>PATH</c> it got then, so a CLI or Node.js
    /// installed since would not be found on it. The current user and
    /// machine <c>PATH</c> come from the registry, ahead of the inherited one.
    /// </summary>
    public static SystemContext Current()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
            environment[(string)entry.Key] = (string?)entry.Value ?? "";
        return new SystemContext(
            environment,
            () => PathDirectories.Merge(
                RegistryPath(Registry.CurrentUser, "Environment"),
                RegistryPath(Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"),
                System.Environment.GetEnvironmentVariable("PATH")),
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles),
            File.Exists);
    }

    private static string? RegistryPath(RegistryKey hive, string key)
    {
        try
        {
            using var k = hive.OpenSubKey(key);
            return k?.GetValue("Path") as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

public static class PathDirectories
{
    /// <summary>The directories of several <c>PATH</c> values, in order, expanded, without repeats.</summary>
    public static IReadOnlyList<string> Merge(params string?[] paths)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            foreach (var raw in path.Split(';'))
            {
                var dir = System.Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"')).TrimEnd('\\');
                if (dir.Length == 0) continue;
                if (dir.EndsWith(':')) dir += "\\";
                if (seen.Add(dir)) result.Add(dir);
            }
        }
        return result;
    }
}

/// <summary>
/// Finds the <c>varlatch</c> CLI and the environment to run it with: the
/// path set in the settings, else the release CLI where the app installs it
/// (<c>%LOCALAPPDATA%\Programs\Varlatch\varlatch.cjs</c>), else
/// <c>varlatch</c> on the <c>PATH</c>.
/// </summary>
public sealed class CliLocator(SystemContext system, string? overridePath = null)
{
    private static readonly string[] PathExtensions = [".exe", ".cmd", ".bat"];

    /// <summary>Where the app installs the release CLI, as the Varlatch docs do on Windows.</summary>
    public static string InstallPath(string localAppData) =>
        System.IO.Path.Combine(localAppData, "Programs", "Varlatch", "varlatch.cjs");

    /// <summary>How the "not found" message names the places looked in.</summary>
    public const string OnPathDescription = "varlatch on your PATH";

    public CliResolution Resolve()
    {
        var raw = overridePath?.Trim().Trim('"');
        if (!string.IsNullOrEmpty(raw))
        {
            var path = Expand(raw);
            if (!system.FileExists(path)) return new CliResolution.OverrideUnusable(path);
            return Command(path) ?? (CliResolution)new CliResolution.OverrideUnusable(path);
        }
        var installed = InstallPath(system.LocalAppData);
        if (system.FileExists(installed)) return Command(installed)!;
        foreach (var dir in system.SearchPath())
        {
            foreach (var extension in PathExtensions)
            {
                var candidate = System.IO.Path.Combine(dir, "varlatch" + extension);
                if (system.FileExists(candidate) && Command(candidate) is { } found) return found;
            }
        }
        return new CliResolution.NotFound([installed, OnPathDescription]);
    }

    /// <summary>How to run the CLI at <paramref name="path"/>; null when Windows cannot run it.</summary>
    private CliResolution? Command(string path)
    {
        var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
        switch (extension)
        {
            case ".exe":
                return new CliResolution.Found(new CliCommand(path, path, []));
            case ".js" or ".cjs" or ".mjs":
                return Node(path, path);
            case ".cmd" or ".bat":
                // A shim that runs a script: run the script itself.
                if (Shim.Script(path, system.FileExists) is { } script) return Node(path, script);
                return new CliResolution.Found(new CliCommand(path, path, []));
            case ".ps1":
                return new CliResolution.Found(new CliCommand(path, "powershell.exe",
                    ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", path]));
            case "":
                // A script with a `#!/usr/bin/env node` line, as installed on
                // Unix: Node.js runs it here too.
                return Shim.IsNodeScript(path) ? Node(path, path) : null;
            default:
                return null;
        }
    }

    private CliResolution Node(string path, string script)
    {
        var node = FindNode();
        return node is null
            ? new CliResolution.NodeMissing(path)
            : new CliResolution.Found(new CliCommand(path, node, [script], script));
    }

    /// <summary>
    /// <c>node.exe</c>: on the search path, else where its installers put it
    /// (the Node.js installer and winget, nvm-windows, Volta, Scoop).
    /// </summary>
    public string? FindNode()
    {
        foreach (var dir in system.SearchPath())
        {
            var candidate = System.IO.Path.Combine(dir, "node.exe");
            if (system.FileExists(candidate)) return candidate;
        }
        var home = system.Environment.GetValueOrDefault("USERPROFILE") ?? "";
        string?[] known =
        [
            System.IO.Path.Combine(system.ProgramFiles, "nodejs", "node.exe"),
            System.IO.Path.Combine(system.LocalAppData, "Programs", "nodejs", "node.exe"),
            system.Environment.GetValueOrDefault("NVM_SYMLINK") is { Length: > 0 } nvm ? System.IO.Path.Combine(nvm, "node.exe") : null,
            System.IO.Path.Combine(system.LocalAppData, "Volta", "bin", "node.exe"),
            home.Length > 0 ? System.IO.Path.Combine(home, "scoop", "apps", "nodejs-lts", "current", "node.exe") : null,
            home.Length > 0 ? System.IO.Path.Combine(home, "scoop", "apps", "nodejs", "current", "node.exe") : null,
        ];
        return known.FirstOrDefault(p => p is not null && system.FileExists(p));
    }

    /// <summary>
    /// The environment to run <paramref name="command"/> with: the app's own,
    /// with a <c>PATH</c> that starts with the CLI's directory and that of
    /// the Node.js it runs on, then the search path.
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment(CliCommand command)
    {
        var env = new Dictionary<string, string>(system.Environment, StringComparer.OrdinalIgnoreCase);
        var first = new List<string> { System.IO.Path.GetDirectoryName(command.Path) ?? "" };
        if (command.Script is not null) first.Add(System.IO.Path.GetDirectoryName(command.Executable) ?? "");
        env["PATH"] = string.Join(';', PathDirectories.Merge(string.Join(';', first), string.Join(';', system.SearchPath())));
        return env;
    }

    /// <summary><c>%VARIABLES%</c> and a leading <c>~</c> expanded.</summary>
    public string Expand(string path)
    {
        var expanded = Regex.Replace(path, "%([^%]+)%", m =>
            system.Environment.TryGetValue(m.Groups[1].Value, out var value) ? value : m.Value);
        var home = system.Environment.GetValueOrDefault("USERPROFILE");
        if (home is not null && (expanded == "~" || expanded.StartsWith("~/") || expanded.StartsWith("~\\")))
            expanded = home + expanded[1..];
        return expanded;
    }
}

/// <summary>Reading <c>.cmd</c> shims and scripts without running them.</summary>
public static class Shim
{
    // `@node "%~dp0varlatch.cjs" %*` as the Varlatch docs write it, and npm's
    // `"%_prog%" "%dp0%\node_modules\...\main.js" %*`.
    private static readonly Regex ScriptInShim = new(
        @"""%(?:~dp0|dp0%)\\?([^""%]+\.(?:c|m)?js)""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The script a <c>.cmd</c> shim hands to Node.js, when it is one and the script exists.</summary>
    public static string? Script(string shimPath, Func<string, bool> fileExists)
    {
        string text;
        try
        {
            using var reader = new StreamReader(shimPath);
            var buffer = new char[8192];
            text = new string(buffer, 0, reader.ReadBlock(buffer, 0, buffer.Length));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        var match = ScriptInShim.Match(text);
        if (!match.Success) return null;
        var directory = Path.GetDirectoryName(shimPath) ?? "";
        var script = Path.GetFullPath(Path.Combine(directory, match.Groups[1].Value.Replace('/', '\\')));
        return fileExists(script) ? script : null;
    }

    /// <summary>The first bytes of a file, as text; empty when it cannot be read.</summary>
    public static string Head(string path, int bytes = 512)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[bytes];
            var n = stream.ReadAtLeast(buffer, bytes, throwOnEndOfStream: false);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, n);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    public static bool IsNodeScript(string path)
    {
        var head = Head(path, 128);
        var line = head.Split('\n')[0];
        return line.StartsWith("#!") && line.Contains("node");
    }
}
