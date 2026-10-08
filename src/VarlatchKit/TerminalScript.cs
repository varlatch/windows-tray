using System.Text;

namespace VarlatchKit;

/// <summary>
/// A PowerShell script that runs one command for the person watching: it
/// says what it does, shows the command, runs it, says how it went, and
/// waits for Enter so the window does not vanish. The app writes it to a
/// temporary file and opens it in Windows Terminal; the file removes itself.
/// </summary>
public static class TerminalScript
{
    /// <param name="title">What the window says it does.</param>
    /// <param name="command">The program and its arguments, run as they are.</param>
    public static string Build(string title, IReadOnlyList<string> command)
    {
        var line = "& " + string.Join(" ", command.Select(Quote));
        var shown = string.Join(" ", command.Select(Display));
        var script = new StringBuilder();
        script.AppendLine("Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue");
        script.AppendLine($"$Host.UI.RawUI.WindowTitle = {Quote(title)}");
        script.AppendLine($"Write-Host {Quote(title)}");
        script.AppendLine("Write-Host ''");
        script.AppendLine($"Write-Host {Quote("> " + shown)}");
        script.AppendLine("$global:LASTEXITCODE = 0");
        script.AppendLine("try {");
        script.AppendLine($"  {line}");
        script.AppendLine("  $status = if ($?) { $LASTEXITCODE } else { if ($LASTEXITCODE) { $LASTEXITCODE } else { 1 } }");
        script.AppendLine("} catch {");
        script.AppendLine("  Write-Host $_ -ForegroundColor Red");
        script.AppendLine("  $status = 1");
        script.AppendLine("}");
        script.AppendLine("Write-Host ''");
        script.AppendLine("if ($status -eq 0) { Write-Host 'Done. You can close this window.' } else { Write-Host \"That did not work (exit $status).\" }");
        script.AppendLine("[void](Read-Host 'Press Enter to close')");
        return script.ToString();
    }

    /// <summary>'text' for PowerShell, with single quotes inside doubled.</summary>
    public static string Quote(string text) => "'" + text.Replace("'", "''") + "'";

    /// <summary>An argument as a person would type it: quoted only when it needs to be.</summary>
    public static string Display(string text) =>
        text.Length > 0 && text.All(c => char.IsLetterOrDigit(c) || "-_.:/\\=+@,".Contains(c)) ? text : "\"" + text + "\"";
}
