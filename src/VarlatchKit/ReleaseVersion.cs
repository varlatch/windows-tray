using System.Globalization;
using System.Text.RegularExpressions;

namespace VarlatchKit;

/// <summary>
/// A release version such as 0.15.1, compared by its numbers. A suffix
/// (0.16.0-rc.1) is kept for display and sorts before the release, with
/// numeric dot-separated segments compared as numbers (rc.9 &lt; rc.10).
/// </summary>
public readonly record struct ReleaseVersion : IComparable<ReleaseVersion>
{
    public const int MaxLength = 64;

    private static readonly Regex StrictPattern = new(
        @"^v?([0-9]+)\.([0-9]+)\.([0-9]+)(?:-([0-9A-Za-z.]+))?$",
        RegexOptions.CultureInvariant);

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string Suffix => suffix ?? "";

    private readonly string? suffix;

    public ReleaseVersion(int major, int minor, int patch, string suffix = "")
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        this.suffix = suffix;
    }

    /// <summary>Device sign-in (<c>login --start</c> / <c>--wait</c>) arrived in CLI 0.14.0.</summary>
    public static readonly ReleaseVersion DeviceSignIn = new(0, 14, 0);

    /// <summary><c>self-update</c> arrived in CLI 0.11.0.</summary>
    public static readonly ReleaseVersion SelfUpdate = new(0, 11, 0);

    /// <summary>The first release whose CLI is a native program needing no Node.js.</summary>
    public static readonly ReleaseVersion NativeCli = new(0, 50, 0);

    /// <summary>"0.15.1", "v0.15.1", or "0.16.0-rc.1"; null for anything else.</summary>
    public static ReleaseVersion? Parse(string? text)
    {
        if (text is null) return null;
        var s = text.Trim();
        if (s.Length == 0 || s.Length > MaxLength) return null;
        var match = StrictPattern.Match(s);
        if (!match.Success) return null;
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
            || !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return null;
        }
        var suffix = match.Groups[4].Success ? match.Groups[4].Value : "";
        if (suffix.Length > 0 && (suffix.StartsWith('.') || suffix.EndsWith('.') || suffix.Contains("..")))
            return null;
        return new ReleaseVersion(major, minor, patch, suffix);
    }

    /// <summary>The version in <c>varlatch --version</c> output: "varlatch 0.15.1 (migration 26)", "varlatch 0.50.0".</summary>
    public static ReleaseVersion? FromCliOutput(string output)
    {
        var line = output.Split('\n', '\r').FirstOrDefault(l => l.Length > 0);
        if (line is null) return null;
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || words[0] != "varlatch") return null;
        return Parse(words[1]);
    }

    public override string ToString() =>
        $"{Major}.{Minor}.{Patch}" + (Suffix.Length == 0 ? "" : "-" + Suffix);

    public int CompareTo(ReleaseVersion other)
    {
        var numbers = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (numbers != 0) return numbers;
        // A pre-release comes before its release.
        if ((Suffix.Length == 0) != (other.Suffix.Length == 0)) return Suffix.Length == 0 ? 1 : -1;
        if (Suffix.Length == 0) return 0;
        var a = Suffix.Split('.');
        var b = other.Suffix.Split('.');
        var shared = Math.Min(a.Length, b.Length);
        for (var i = 0; i < shared; i++)
        {
            var aNum = ulong.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var av);
            var bNum = ulong.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bv);
            int cmp;
            if (aNum && bNum) cmp = av.CompareTo(bv);
            else if (aNum != bNum) cmp = aNum ? -1 : 1;
            else cmp = Math.Sign(string.CompareOrdinal(a[i], b[i]));
            if (cmp != 0) return cmp;
        }
        return a.Length.CompareTo(b.Length);
    }

    public bool Equals(ReleaseVersion other) =>
        Major == other.Major && Minor == other.Minor && Patch == other.Patch && Suffix == other.Suffix;

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Suffix);

    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;
}
