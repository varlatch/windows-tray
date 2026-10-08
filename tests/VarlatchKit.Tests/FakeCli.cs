using System.Globalization;
using System.Text.Json;

namespace VarlatchKit.Tests;

/// <summary>
/// <c>tests/fake-varlatch.cjs</c> in a throwaway directory: the status it
/// reports, how its sign-ins end, and what it was asked.
/// </summary>
public sealed class FakeCli
{
    public static string ScriptPath { get; } = Path.Combine(AppContext.BaseDirectory, "fake-varlatch.cjs");

    public string Dir { get; }

    public FakeCli()
    {
        Dir = TestDirectory.Path("fake");
        Directory.CreateDirectory(Dir);
    }

    /// <summary>
    /// This machine's PATH (to find node.exe), with the fake's directory as
    /// home and nothing installed under its own LocalAppData.
    /// </summary>
    public SystemContext System(Func<string, bool>? fileExists = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
            environment[(string)e.Key] = (string?)e.Value ?? "";
        environment["FAKE_VARLATCH_DIR"] = Dir;
        environment["USERPROFILE"] = Dir;
        environment.Remove("VARLATCH_NO_BROWSER");
        var path = PathDirectories.Merge(Environment.GetEnvironmentVariable("PATH"));
        return new SystemContext(environment, () => path, Path.Combine(Dir, "Local"), Path.Combine(Dir, "ProgramFiles"),
                                 fileExists ?? File.Exists);
    }

    public CliInvocation Invocation()
    {
        var locator = new CliLocator(System(), ScriptPath);
        var found = Assert.IsType<CliResolution.Found>(locator.Resolve());
        return new CliInvocation(found.Command, locator.Environment(found.Command));
    }

    public void SetStatus(string json) => File.WriteAllText(Path.Combine(Dir, "status.json"), json);

    public void SetProbe(string json) => File.WriteAllText(Path.Combine(Dir, "probe.json"), json);

    /// <summary>Variables the fake reads on every call, such as FAKE_LOGIN_RC.</summary>
    public void Set(Dictionary<string, string> variables) =>
        File.WriteAllText(Path.Combine(Dir, "fake.json"), JsonSerializer.Serialize(variables));

    public IReadOnlyList<string> Calls => Lines("calls.log");

    public IReadOnlyList<string> EnvironmentSeen => Lines("env.log");

    private IReadOnlyList<string> Lines(string file)
    {
        var path = Path.Combine(Dir, file);
        return File.Exists(path) ? File.ReadAllLines(path).Where(l => l.Length > 0).ToList() : [];
    }

    public static string Status(params string[] servers) =>
        "{\"version\":1,\"servers\":[" + string.Join(",", servers) + "],\"repo\":null}";

    public static string Server(string url, double expiresIn = 10, double lifetime = 12, bool? expiring = false,
                                bool expired = false, string credentialId = "crd_x")
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.AddHours(expiresIn);
        var issued = expires.AddHours(-lifetime);
        var expiringField = expiring is { } e ? $",\"expiring\":{(e ? "true" : "false")}" : "";
        return $"{{\"server\":\"{url}\",\"name\":null,\"issuedAt\":\"{Iso(issued)}\",\"expiresAt\":\"{Iso(expires)}\"," +
               $"\"credentialId\":\"{credentialId}\",\"expired\":{(expired ? "true" : "false")}{expiringField}}}";
    }

    private static string Iso(DateTimeOffset date) =>
        date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

public static class Dates
{
    public static DateTimeOffset Date(string iso) => ServerStatus.ParseDate(iso) ?? throw new ArgumentException(iso);
}
