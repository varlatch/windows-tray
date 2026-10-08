using System.Globalization;

namespace VarlatchKit;

/// <summary>
/// A release version such as 0.15.1, compared by its numbers. A suffix
/// (0.16.0-rc.1) is kept for display and sorts before the release.
/// </summary>
public readonly record struct ReleaseVersion : IComparable<ReleaseVersion>
{
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

    /// <summary>"0.15.1", "v0.15.1", or "0.16.0-rc.1"; null for anything else.</summary>
    public static ReleaseVersion? Parse(string? text)
    {
        if (text is null) return null;
        var s = text.Trim();
        if (s.StartsWith('v')) s = s[1..];
        var suffix = "";
        var dash = s.IndexOf('-');
        if (dash >= 0)
        {
            suffix = s[(dash + 1)..];
            s = s[..dash];
        }
        var parts = s.Split('.');
        if (parts.Length != 3) return null;
        if (!Number(parts[0], out var major) || !Number(parts[1], out var minor) || !Number(parts[2], out var patch))
            return null;
        return new ReleaseVersion(major, minor, patch, suffix);

        static bool Number(string part, out int value) =>
            int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The version in <c>varlatch --version</c> output: "varlatch 0.15.1 (migration 26)".</summary>
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
        return Math.Sign(string.CompareOrdinal(Suffix, other.Suffix));
    }

    public bool Equals(ReleaseVersion other) =>
        Major == other.Major && Minor == other.Minor && Patch == other.Patch && Suffix == other.Suffix;

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Suffix);

    public static bool operator <(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(ReleaseVersion a, ReleaseVersion b) => a.CompareTo(b) >= 0;
}
