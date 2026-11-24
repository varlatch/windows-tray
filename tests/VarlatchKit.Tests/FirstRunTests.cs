using static VarlatchKit.Tests.Dates;

namespace VarlatchKit.Tests;

public class ServerAddressTests
{
    [Fact]
    public void AddsHttpsAndTrims()
    {
        Assert.Equal("https://varlatch.example.com", ServerAddress.Normalize("varlatch.example.com"));
        Assert.Equal("https://vl.example.com", ServerAddress.Normalize("  vl.example.com/ "));
        Assert.Equal("https://vl.example.com", ServerAddress.Normalize("https://vl.example.com//"));
        Assert.Equal("http://localhost:3000", ServerAddress.Normalize("http://localhost:3000"));
        Assert.Equal("https://vl.example.com:8443/varlatch", ServerAddress.Normalize("vl.example.com:8443/varlatch"));
    }

    [Fact]
    public void RefusesWhatCannotBeAServer()
    {
        Assert.Null(ServerAddress.Normalize(""));
        Assert.Null(ServerAddress.Normalize("   "));
        Assert.Null(ServerAddress.Normalize("ftp://vl.example.com"));
        Assert.Null(ServerAddress.Normalize("https://vl.example.com/?x=1"));
        Assert.Null(ServerAddress.Normalize("https:///path"));
    }
}

public class CliInstallTests
{
    private readonly string dir = TestDirectory.Path("install");

    private string File(string path, string contents)
    {
        var full = Path.Combine(dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, contents);
        return full;
    }

    private static CliCommand Node(string script) => new(script, @"C:\node\node.exe", [script], script);

    [Fact]
    public void ReleaseBuild()
    {
        var path = File(@"Programs\Varlatch\varlatch.cjs", "#!/usr/bin/env node\n/*! Varlatch CLI. Copyright 2026 Robotsson. */\n");
        Assert.Equal(CliInstallKind.Release, CliInstall.Kind(Node(path)));
    }

    [Fact]
    public void ReleaseBuildThroughItsShim()
    {
        var script = File(@"Programs\Varlatch\varlatch.cjs", "#!/usr/bin/env node\n/*! Varlatch CLI. Copyright 2026 Robotsson. */\n");
        var shim = File(@"Programs\Varlatch\varlatch.cmd", "@node \"%~dp0varlatch.cjs\" %*\r\n");
        Assert.Equal(script, Shim.Script(shim, System.IO.File.Exists));
        Assert.Equal(CliInstallKind.Release, CliInstall.Kind(new CliCommand(shim, @"C:\node\node.exe", [script], script)));
    }

    private static CliCommand Program(string path) => new(path, path, []);

    [Fact]
    public void NativeCli() =>
        Assert.Equal(CliInstallKind.Release, CliInstall.Kind(Program(File(@"Programs\Varlatch\varlatch.exe", "MZ\u0090"))));

    [Fact]
    public void WingetsCopy()
    {
        Assert.Equal(CliInstallKind.Winget, CliInstall.Kind(Program(File(@"Microsoft\WinGet\Links\varlatch.exe", "MZ"))));
        Assert.Equal(CliInstallKind.Winget, CliInstall.Kind(Program(File(@"Microsoft\WinGet\Packages\Varlatch.CLI_x\varlatch.exe", "MZ"))));
    }

    [Fact]
    public void SourceCheckout()
    {
        var path = File(@"src\varlatch\apps\cli\dist\varlatch.cjs", "/*! Varlatch CLI. */\n");
        Directory.CreateDirectory(Path.Combine(dir, @"src\varlatch\.git"));
        Assert.Equal(CliInstallKind.Checkout, CliInstall.Kind(Node(path)));
    }

    [Fact]
    public void AnythingElse() =>
        Assert.Equal(CliInstallKind.Custom, CliInstall.Kind(Node(File(@"other\varlatch.js", "console.log(1)\n"))));

    [Fact]
    public void Writable() => Assert.True(CliInstall.DirectoryWritable(File("a.txt", "")));
}

public class CliUpdateTests
{
    private static readonly CliCommand Script = new(@"C:\v\varlatch.cjs", @"C:\node\node.exe", [@"C:\v\varlatch.cjs"], @"C:\v\varlatch.cjs");
    private static readonly CliCommand Native = new(@"C:\v\varlatch.exe", @"C:\v\varlatch.exe", []);

    private static CliUpdateWay Way(CliInstallKind kind, CliCommand command, string version) =>
        CliUpdate.Way(kind, command, ReleaseVersion.Parse(version));

    [Fact]
    public void TheNativeCliUpdatesItself()
    {
        Assert.Equal(CliUpdateWay.SelfUpdate, Way(CliInstallKind.Release, Native, "0.50.0"));
        Assert.Equal(CliUpdateWay.SelfUpdate, Way(CliInstallKind.Release, Native, "0.50.0-rc.3"));
    }

    [Fact]
    public void TheSingleFileGoesThroughTheInstallScript() =>
        Assert.Equal(CliUpdateWay.Installer, Way(CliInstallKind.Release, Script, "0.23.0"));

    [Fact]
    public void NoTwoUpdatersForOneCopy()
    {
        Assert.Equal(CliUpdateWay.Winget, Way(CliInstallKind.Winget, Native, "0.50.0"));
        Assert.Equal(CliUpdateWay.Manual, Way(CliInstallKind.Checkout, Script, "0.23.0"));
        Assert.Equal(CliUpdateWay.Manual, Way(CliInstallKind.Custom, Native, "0.50.0"));
    }

    [Fact]
    public void CopiesThatCannotUpdate()
    {
        Assert.Equal(CliUpdateWay.Manual, Way(CliInstallKind.Release, Script, "0.10.1"));
        Assert.Equal(CliUpdateWay.Manual, Way(CliInstallKind.Release, Native, "0.50.0-dev"));
        Assert.Equal(CliUpdateWay.Manual, CliUpdate.Way(CliInstallKind.Release, Native, null));
        Assert.Equal(CliUpdateWay.Manual, CliUpdate.Way(null, null, null));
    }

    [Fact]
    public void TheInstallScriptsCommand()
    {
        Assert.Equal(["ps", "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "s.ps1", "-Update", @"C:\v\varlatch.cjs",
                      "-Version", "0.50.0", "-Node", @"C:\node\node.exe", "-Settings", @"C:\s\settings.json"],
                     CliUpdate.Installer("ps", "s.ps1", @"C:\v\varlatch.cjs", ReleaseVersion.Parse("0.50.0"), @"C:\node\node.exe", @"C:\s\settings.json"));
        Assert.Equal(["ps", "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "s.ps1", "-Update", @"C:\v\varlatch.cjs"],
                     CliUpdate.Installer("ps", "s.ps1", @"C:\v\varlatch.cjs", null, null));
    }

    [Fact]
    public void NativeTargetWithoutNodeEnforcesEligibilityAndNativeTarget()
    {
        var dir = TestDirectory.Path("nonode-elig");
        Directory.CreateDirectory(dir);
        var cjs = Path.Combine(dir, "varlatch.cjs");
        File.WriteAllText(cjs, "#!/usr/bin/env node\n/*! Varlatch CLI. Copyright 2026 Robotsson. */\n");
        var shim = Path.Combine(dir, "varlatch.cmd");
        File.WriteAllText(shim, "@node \"%~dp0varlatch.cjs\" %*\r\n");
        var other = Path.Combine(dir, "other.cjs");
        File.WriteAllText(other, "#!/usr/bin/env node\n/*! Varlatch CLI. Copyright 2026 Robotsson. */\n");
        var custom = Path.Combine(dir, "custom", "varlatch.cjs");
        Directory.CreateDirectory(Path.GetDirectoryName(custom)!);
        File.WriteAllText(custom, "console.log(1);\n");
        var checkoutDir = Path.Combine(dir, "repo");
        Directory.CreateDirectory(Path.Combine(checkoutDir, ".git"));
        var checkoutCli = Path.Combine(checkoutDir, "varlatch.cjs");
        File.WriteAllText(checkoutCli, "/*! Varlatch CLI. */\n");

        var v23 = ReleaseVersion.Parse("0.23.0");
        var v50 = ReleaseVersion.Parse("0.50.0");

        // No native target resolved (< 0.50.0 or null): ineligible.
        Assert.Null(CliUpdate.NativeTargetWithoutNode(cjs, null));
        Assert.Null(CliUpdate.NativeTargetWithoutNode(cjs, v23));
        // Eligible with a native target:
        Assert.Equal(v50, CliUpdate.NativeTargetWithoutNode(cjs, v50));
        Assert.Equal(v50, CliUpdate.NativeTargetWithoutNode(shim, v50));
        // Wrong filename, custom script, or checkout: ineligible.
        Assert.Null(CliUpdate.NativeTargetWithoutNode(other, v50));
        Assert.Null(CliUpdate.NativeTargetWithoutNode(custom, v50));
        Assert.Null(CliUpdate.NativeTargetWithoutNode(checkoutCli, v50));
    }

    [Fact]
    public void TheVersionsBothGenerationsPrint()
    {
        Assert.Equal(new ReleaseVersion(0, 23, 0), ReleaseVersion.FromCliOutput("varlatch 0.23.0 (migration 52)\n"));
        Assert.Equal(new ReleaseVersion(0, 50, 0, "rc.3"), ReleaseVersion.FromCliOutput("varlatch 0.50.0-rc.3\n"));
    }
}

public class ReleaseRulesTests
{
    private readonly DateTimeOffset now = Date("2026-10-07T12:00:00Z");
    private DateTimeOffset Ago(double hours) => now.AddHours(-hours);
    private static readonly ReleaseVersion V0151 = new(0, 15, 1);
    private static readonly ReleaseVersion V0160 = new(0, 16, 0);

    [Fact]
    public void FirstCheck() =>
        Assert.True(ReleaseRules.ShouldFetch(new ReleaseCache(), V0151, ReleaseRules.BackgroundMaxAge, now));

    [Fact]
    public void TwelveHoursInTheBackgroundAnHourFromThePanel()
    {
        var cache = new ReleaseCache { Latest = "0.15.1", CheckedAt = Ago(2), AttemptedAt = Ago(2) };
        Assert.False(ReleaseRules.ShouldFetch(cache, V0151, ReleaseRules.BackgroundMaxAge, now));
        Assert.True(ReleaseRules.ShouldFetch(cache, V0151, ReleaseRules.PanelMaxAge, now));
        var old = new ReleaseCache { Latest = "0.15.1", CheckedAt = Ago(13), AttemptedAt = Ago(13) };
        Assert.True(ReleaseRules.ShouldFetch(old, V0151, ReleaseRules.BackgroundMaxAge, now));
    }

    [Fact]
    public void ACacheOlderThanTheInstalledCli()
    {
        var cache = new ReleaseCache { Latest = "0.15.1", CheckedAt = Ago(1.5), AttemptedAt = Ago(1.5) };
        Assert.True(ReleaseRules.ShouldFetch(cache, V0160, ReleaseRules.BackgroundMaxAge, now));
    }

    [Fact]
    public void NeverMoreThanOnceAnHourFailuresIncluded()
    {
        // The last answer is old, but a check failed 30 minutes ago.
        var failed = new ReleaseCache { Latest = "0.15.1", CheckedAt = Ago(20), AttemptedAt = Ago(0.5) };
        Assert.False(ReleaseRules.ShouldFetch(failed, V0151, ReleaseRules.PanelMaxAge, now));
        var newer = new ReleaseCache { Latest = "0.15.1", CheckedAt = Ago(0.5), AttemptedAt = Ago(0.5) };
        Assert.False(ReleaseRules.ShouldFetch(newer, V0160, ReleaseRules.PanelMaxAge, now));
        Assert.True(ReleaseRules.ShouldFetch(failed, V0151, ReleaseRules.PanelMaxAge, now, force: true));
    }

    [Fact]
    public void Available()
    {
        Assert.Equal(V0160, ReleaseRules.Available(new ReleaseCache { Latest = "0.16.0" }, V0151));
        Assert.Null(ReleaseRules.Available(new ReleaseCache { Latest = "0.15.1" }, V0151));
        Assert.Null(ReleaseRules.Available(new ReleaseCache { Latest = "0.15.0" }, V0151));
        Assert.Null(ReleaseRules.Available(new ReleaseCache { Latest = "0.16.0" }, null));
    }
}

public class ReleaseCheckerTests
{
    private readonly string path = TestDirectory.Path("update") + ".json";
    private static readonly ReleaseVersion V0151 = new(0, 15, 1);

    private sealed class Fetches
    {
        public int Count;
        public string? Answer = "v0.16.0";
    }

    private sealed class Clock
    {
        public DateTimeOffset Now = Date("2026-10-07T12:00:00Z");
    }

    private (ReleaseChecker, List<ReleaseVersion>) Checker(Fetches fetches, Clock clock)
    {
        var notified = new List<ReleaseVersion>();
        var checker = new ReleaseChecker(path, () => { fetches.Count++; return Task.FromResult(fetches.Answer); }, () => clock.Now);
        checker.NewRelease += notified.Add;
        return (checker, notified);
    }

    [Fact]
    public async Task FindsANewReleaseAndNotifiesOnce()
    {
        var fetches = new Fetches();
        var clock = new Clock();
        var (checker, notified) = Checker(fetches, clock);
        await checker.CheckAsync(V0151, ReleaseRules.BackgroundMaxAge);
        Assert.Equal(new ReleaseVersion(0, 16, 0), checker.Available(V0151));
        Assert.Equal([new ReleaseVersion(0, 16, 0)], notified);
        clock.Now = clock.Now.AddHours(2);
        await checker.CheckAsync(V0151, ReleaseRules.PanelMaxAge);
        Assert.Equal(2, fetches.Count);
        Assert.Equal([new ReleaseVersion(0, 16, 0)], notified);
    }

    [Fact]
    public async Task KeepsItsCache()
    {
        var fetches = new Fetches();
        var clock = new Clock();
        var (checker, _) = Checker(fetches, clock);
        await checker.CheckAsync(V0151, ReleaseRules.BackgroundMaxAge);
        var again = new ReleaseChecker(path, () => Task.FromResult<string?>(null));
        Assert.Equal("0.16.0", again.Cache.Latest);
        Assert.Equal(clock.Now, again.Cache.CheckedAt);
        Assert.Equal("0.16.0", again.Cache.Notified);
    }

    [Fact]
    public async Task AFailedCheckWaitsAnHour()
    {
        var fetches = new Fetches { Answer = null };
        var clock = new Clock();
        var (checker, notified) = Checker(fetches, clock);
        await checker.CheckAsync(V0151, ReleaseRules.PanelMaxAge);
        Assert.Null(checker.Cache.CheckedAt);
        Assert.Equal(clock.Now, checker.Cache.AttemptedAt);
        clock.Now = clock.Now.AddMinutes(30);
        await checker.CheckAsync(V0151, ReleaseRules.PanelMaxAge);
        Assert.Equal(1, fetches.Count);
        clock.Now = clock.Now.AddMinutes(31);
        fetches.Answer = "v0.15.1";
        await checker.CheckAsync(V0151, ReleaseRules.PanelMaxAge);
        Assert.Equal(2, fetches.Count);
        Assert.Null(checker.Available(V0151));
        Assert.Empty(notified);
    }

    [Fact]
    public void ReleaseUrl() =>
        Assert.Equal("https://github.com/varlatch/varlatch/releases/tag/v0.16.0", ReleaseChecker.ReleaseUrl(new ReleaseVersion(0, 16, 0)));
}
