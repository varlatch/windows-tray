using System.Diagnostics;
using System.Text;
using VarlatchKit;

namespace Varlatch;

/// <summary>
/// Runs a command in a new terminal window, for the person to watch and
/// answer: a temporary PowerShell script (<see cref="TerminalScript"/>) that
/// removes itself, in Windows Terminal when it is there, else in a console
/// window.
/// </summary>
internal static class Terminal
{
    public static string PowerShell =>
        Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private static string? WindowsTerminal
    {
        get
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                    "Microsoft", "WindowsApps", "wt.exe");
            return File.Exists(path) ? path : null;
        }
    }

    /// <param name="title">What the window says it does.</param>
    /// <param name="command">The program and its arguments.</param>
    /// <param name="elevated">Run as administrator, after Windows asks.</param>
    public static void Run(string title, IReadOnlyList<string> command, bool elevated = false)
    {
        var script = Path.Combine(Path.GetTempPath(), $"varlatch-{Guid.NewGuid().ToString("N")[..8]}.ps1");
        try
        {
            // With a BOM, so Windows PowerShell reads it as UTF-8.
            File.WriteAllText(script, TerminalScript.Build(title, command), new UTF8Encoding(true));
            string[] arguments = ["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script];
            if (elevated)
            {
                Start(PowerShell, arguments, shell: true, verb: "runas");
            }
            else if (WindowsTerminal is { } wt)
            {
                Start(wt, ["-w", "new", "--title", title, PowerShell, .. arguments], shell: false);
            }
            else
            {
                Start(PowerShell, arguments, shell: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Includes "no" at the administrator prompt.
            Log.Write($"cannot run {title}: {e.Message}");
            try { File.Delete(script); } catch (IOException) { }
        }
    }

    private static void Start(string file, IReadOnlyList<string> arguments, bool shell, string? verb = null)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = shell, Verb = verb ?? "" };
        if (shell)
        {
            info.Arguments = string.Join(" ", arguments.Select(Quote));
        }
        else
        {
            foreach (var a in arguments) info.ArgumentList.Add(a);
        }
        Process.Start(info)?.Dispose();
    }

    /// <summary>An argument for a Windows command line.</summary>
    private static string Quote(string argument) =>
        argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0 ? argument : "\"" + argument.Replace("\"", "\\\"") + "\"";
}
