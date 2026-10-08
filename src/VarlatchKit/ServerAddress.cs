using System.Text.RegularExpressions;

namespace VarlatchKit;

public static class ServerAddress
{
    private static readonly Regex Scheme = new(@"^[A-Za-z][A-Za-z0-9+.-]*://", RegexOptions.CultureInvariant);

    private static readonly Regex Server = new(
        @"^https?://[^/?#]+(/[^?#]*)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// "https://varlatch.example.com" from what a person typed: no spaces,
    /// https:// when no scheme is given, no trailing slash. Null when it
    /// cannot be a server's address.
    /// </summary>
    public static string? Normalize(string text)
    {
        var s = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()).TrimEnd('/');
        if (s.Length == 0) return null;
        if (!Scheme.IsMatch(s)) s = "https://" + s;
        return Server.IsMatch(s) ? s : null;
    }
}
