using System.Text;
using System.Text.Json;

namespace VarlatchKit;

/// <summary>
/// A PowerShell script that runs one command for the person watching: it
/// says what it does, shows the command, runs it, says how it went, and
/// waits for Enter so the window does not vanish. For unelevated runs the app
/// writes it to a temporary file and opens it in Windows Terminal; for
/// elevated runs it passes the fixed bootstrap by <c>-EncodedCommand</c> with
/// the values as Base64 JSON data so no temporary <c>.ps1</c> is written.
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

    /// <summary>
    /// A fixed bootstrap script with <paramref name="title"/> and
    /// <paramref name="command"/> carried as Base64 UTF-8 JSON data, never
    /// interpolated into PowerShell syntax. Used for <c>-EncodedCommand</c>.
    /// </summary>
    public static string BuildBootstrap(string title, IReadOnlyList<string> command)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new
            {
                title,
                command,
                shown = string.Join(" ", command.Select(Display)),
            })));
        var script = new StringBuilder();
        script.AppendLine($"$d = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{payload}')) | ConvertFrom-Json");
        script.AppendLine("$Host.UI.RawUI.WindowTitle = $d.title");
        script.AppendLine("Write-Host $d.title");
        script.AppendLine("Write-Host ''");
        script.AppendLine("Write-Host ('> ' + $d.shown)");
        script.AppendLine("$global:LASTEXITCODE = 0");
        script.AppendLine("try {");
        script.AppendLine("  $cmd = @($d.command)");
        script.AppendLine("  $exe = $cmd[0]");
        script.AppendLine("  $rest = if ($cmd.Count -gt 1) { @($cmd[1..($cmd.Count - 1)]) } else { @() }");
        script.AppendLine("  & $exe @rest");
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

    /// <summary>UTF-16LE Base64 for PowerShell's <c>-EncodedCommand</c> parameter.</summary>
    public static string EncodedCommand(string title, IReadOnlyList<string> command) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildBootstrap(title, command)));

    /// <summary>
    /// Quotes <paramref name="text"/> for PowerShell: ASCII single quotes are
    /// doubled inside <c>'...'</c>; Unicode single-quote delimiters that
    /// PowerShell also treats as string terminators (U+2018 to U+201B) and
    /// line breaks are emitted as <c>[char]</c> concatenations so no input
    /// can break out of a string literal.
    /// </summary>
    public static string Quote(string text)
    {
        if (!NeedsCharEscape(text)) return "'" + text.Replace("'", "''") + "'";
        var parts = new List<string>();
        var chunk = new StringBuilder();
        foreach (var c in text)
        {
            if (IsSpecialQuoteOrBreak(c))
            {
                if (chunk.Length > 0)
                {
                    parts.Add("'" + chunk.ToString().Replace("'", "''") + "'");
                    chunk.Clear();
                }
                parts.Add($"[char]0x{(int)c:X4}");
            }
            else
            {
                chunk.Append(c);
            }
        }
        if (chunk.Length > 0) parts.Add("'" + chunk.ToString().Replace("'", "''") + "'");
        return "(" + string.Join(" + ", parts) + ")";
    }

    private static bool NeedsCharEscape(string text)
    {
        foreach (var c in text)
            if (IsSpecialQuoteOrBreak(c)) return true;
        return false;
    }

    private static bool IsSpecialQuoteOrBreak(char c) =>
        c is '\u2018' or '\u2019' or '\u201A' or '\u201B' or '\r' or '\n';

    /// <summary>An argument as a person would type it: quoted only when it needs to be.</summary>
    public static string Display(string text) =>
        text.Length > 0 && text.All(c => char.IsLetterOrDigit(c) || "-_.:/\\=+@,".Contains(c)) ? text : "\"" + text + "\"";
}
