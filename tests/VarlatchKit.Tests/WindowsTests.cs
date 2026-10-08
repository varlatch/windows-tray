namespace VarlatchKit.Tests;

public class CliLocatorTests
{
    private const string Local = @"C:\Users\me\AppData\Local";
    private const string Installed = @"C:\Users\me\AppData\Local\Programs\Varlatch\varlatch.cjs";
    private const string Node = @"C:\Program Files\nodejs\node.exe";

    private static SystemContext System(Func<string, bool> exists, string[]? path = null,
                                        Dictionary<string, string>? environment = null) =>
        new(environment ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["USERPROFILE"] = @"C:\Users\me" },
            () => path ?? [@"C:\Windows\system32", @"C:\Program Files\nodejs"],
            Local, @"C:\Program Files", exists);

    [Fact]
    public void PrefersTheReleaseCliItInstalls()
    {
        var locator = new CliLocator(System(p => p is Installed or Node or @"C:\tools\varlatch.cmd",
                                            [@"C:\tools", @"C:\Program Files\nodejs"]));
        var found = Assert.IsType<CliResolution.Found>(locator.Resolve());
        Assert.Equal(new CliCommand(Installed, Node, [Installed], Installed), found.Command);
    }

    [Fact]
    public void FindsItOnThePath()
    {
        var locator = new CliLocator(System(p => p is @"C:\tools\varlatch.exe" or Node, [@"C:\tools", @"C:\Program Files\nodejs"]));
        var found = Assert.IsType<CliResolution.Found>(locator.Resolve());
        Assert.Equal(new CliCommand(@"C:\tools\varlatch.exe", @"C:\tools\varlatch.exe", []), found.Command);
    }

    [Fact]
    public void RunsTheScriptBehindAShim()
    {
        var dir = TestDirectory.Path("shim");
        Directory.CreateDirectory(dir);
        var shim = Path.Combine(dir, "varlatch.cmd");
        var script = Path.Combine(dir, "varlatch.cjs");
        File.WriteAllText(shim, "@node \"%~dp0varlatch.cjs\" %*\r\n");
        File.WriteAllText(script, "");
        var locator = new CliLocator(System(p => p == Node || File.Exists(p), [dir, @"C:\Program Files\nodejs"]));
        var found = Assert.IsType<CliResolution.Found>(locator.Resolve());
        Assert.Equal(new CliCommand(shim, Node, [script], script), found.Command);
    }

    [Fact]
    public void ReadsNpmShims()
    {
        var dir = TestDirectory.Path("npm");
        var script = Path.Combine(dir, @"node_modules\@varlatch\cli\dist\main.js");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        File.WriteAllText(script, "");
        var shim = Path.Combine(dir, "varlatch.cmd");
        File.WriteAllText(shim, "@ECHO off\r\nSETLOCAL\r\nSET dp0=%~dp0\r\n\"%_prog%\"  \"%dp0%\\node_modules\\@varlatch\\cli\\dist\\main.js\" %*\r\n");
        Assert.Equal(script, Shim.Script(shim, File.Exists));
    }

    [Fact]
    public void ReportsWhereItLooked()
    {
        var locator = new CliLocator(System(_ => false));
        Assert.Equal(new CliResolution.NotFound([Installed, CliLocator.OnPathDescription]), locator.Resolve());
    }

    [Fact]
    public void UsesTheSetPathOnly()
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["USERPROFILE"] = @"C:\Users\me",
            ["LOCALAPPDATA"] = Local,
        };
        var locator = new CliLocator(System(p => p == Installed || p == Node, environment: environment), @" ~\bin\varlatch.cjs ");
        Assert.Equal(new CliResolution.OverrideUnusable(@"C:\Users\me\bin\varlatch.cjs"), locator.Resolve());
        var expanded = new CliLocator(System(p => p == Installed || p == Node, environment: environment),
                                      @"""%LOCALAPPDATA%\Programs\Varlatch\varlatch.cjs""");
        Assert.IsType<CliResolution.Found>(expanded.Resolve());
    }

    [Fact]
    public void EmptySetPathMeansAutomatic()
    {
        var locator = new CliLocator(System(p => p is Installed or Node), "  ");
        Assert.IsType<CliResolution.Found>(locator.Resolve());
    }

    [Fact]
    public void NoNode()
    {
        var locator = new CliLocator(System(p => p == Installed));
        Assert.Equal(new CliResolution.NodeMissing(Installed), locator.Resolve());
    }

    [Fact]
    public void NodeWhereItsInstallersPutIt()
    {
        var volta = $@"{Local}\Volta\bin\node.exe";
        var locator = new CliLocator(System(p => p == volta, [@"C:\Windows\system32"]));
        Assert.Equal(volta, locator.FindNode());
        var nvm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["NVM_SYMLINK"] = @"C:\nvm4w\nodejs" };
        Assert.Equal(@"C:\nvm4w\nodejs\node.exe",
                     new CliLocator(System(p => p == @"C:\nvm4w\nodejs\node.exe", [], nvm)).FindNode());
    }

    [Fact]
    public void ScriptsWindowsCannotRun()
    {
        var locator = new CliLocator(System(p => p == @"C:\x\varlatch.sh"), @"C:\x\varlatch.sh");
        Assert.Equal(new CliResolution.OverrideUnusable(@"C:\x\varlatch.sh"), locator.Resolve());
    }

    [Fact]
    public void PathForTheCli()
    {
        var locator = new CliLocator(System(_ => true, [@"C:\Windows\system32", @"C:\Program Files\nodejs"]));
        var env = locator.Environment(new CliCommand(Installed, Node, [Installed], Installed));
        Assert.Equal(@"C:\Users\me\AppData\Local\Programs\Varlatch;C:\Program Files\nodejs;C:\Windows\system32", env["PATH"]);
        Assert.Equal(@"C:\Users\me", env["userprofile"]);
    }

    [Fact]
    public void MergesPathsWithoutRepeats()
    {
        Assert.Equal([@"C:\a", @"C:\b", @"C:\c", "D:\\"],
                     PathDirectories.Merge(@"C:\a;C:\b\", @"c:\A;""C:\c"";;", "D:"));
    }
}

public class LaunchAtLoginTests
{
    private sealed class FakeRegistry : IUserRegistry
    {
        public Dictionary<(string, string), object> Values { get; } = [];
        public object? Get(string key, string name) => Values.GetValueOrDefault((key, name));
        public void Set(string key, string name, string value) => Values[(key, name)] = value;
        public void Delete(string key, string name) => Values.Remove((key, name));
        public IEnumerable<string> SubKeys(string key) =>
            Values.Keys.Where(k => k.Item1.StartsWith(key + "\\")).Select(k => k.Item1[(key.Length + 1)..]).Distinct();
    }

    private const string Exe = @"C:\Users\me\AppData\Local\Programs\Varlatch for Windows\Varlatch.exe";

    [Fact]
    public void RegistersTheInstalledApp()
    {
        var registry = new FakeRegistry();
        var login = new LaunchAtLogin(registry, Exe);
        Assert.Equal(LaunchAtLogin.Status.Off, login.Current);
        login.Set(true);
        Assert.Equal($"\"{Exe}\" --login-start", registry.Get(LaunchAtLogin.RunKey, "Varlatch"));
        Assert.Equal(LaunchAtLogin.Status.On, login.Current);
        login.Set(false);
        Assert.Equal(LaunchAtLogin.Status.Off, login.Current);
    }

    [Fact]
    public void ReplacesAnotherCopysValue()
    {
        var registry = new FakeRegistry();
        registry.Set(LaunchAtLogin.RunKey, "Varlatch", @"""C:\old\Varlatch.exe"" --login-start");
        new LaunchAtLogin(registry, Exe).Set(true);
        Assert.Equal($"\"{Exe}\" --login-start", registry.Get(LaunchAtLogin.RunKey, "Varlatch"));
    }

    [Fact]
    public void TurnedOffInTaskManager()
    {
        var registry = new FakeRegistry();
        var login = new LaunchAtLogin(registry, Exe);
        login.Set(true);
        registry.Values[(LaunchAtLogin.ApprovedKey, "Varlatch")] = new byte[] { 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        Assert.Equal(LaunchAtLogin.Status.DisabledByUser, login.Current);
        registry.Values[(LaunchAtLogin.ApprovedKey, "Varlatch")] = new byte[] { 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        Assert.Equal(LaunchAtLogin.Status.On, login.Current);
    }

    [Fact]
    public void OnlyAnInstalledCopy()
    {
        Assert.True(LaunchAtLogin.IsInstalled(Exe, @"C:\Users\me\AppData\Local", @"C:\Program Files"));
        Assert.True(LaunchAtLogin.IsInstalled(@"C:\Program Files\Varlatch\Varlatch.exe", @"C:\Users\me\AppData\Local", @"C:\Program Files"));
        Assert.False(LaunchAtLogin.IsInstalled(@"C:\src\windows-tray\src\Varlatch\bin\Debug\Varlatch.exe",
                                               @"C:\Users\me\AppData\Local", @"C:\Program Files"));
    }

    [Fact]
    public void TrayIconInView()
    {
        var registry = new FakeRegistry();
        Assert.Null(TrayIconVisibility.IsPromoted(registry, Exe));
        registry.Values[(TrayIconVisibility.SettingsKey + @"\123", "ExecutablePath")] = Exe.ToLowerInvariant();
        registry.Values[(TrayIconVisibility.SettingsKey + @"\123", "IsPromoted")] = 0;
        registry.Values[(TrayIconVisibility.SettingsKey + @"\456", "ExecutablePath")] = @"C:\other.exe";
        registry.Values[(TrayIconVisibility.SettingsKey + @"\456", "IsPromoted")] = 1;
        Assert.False(TrayIconVisibility.IsPromoted(registry, Exe));
        registry.Values[(TrayIconVisibility.SettingsKey + @"\123", "IsPromoted")] = 1;
        Assert.True(TrayIconVisibility.IsPromoted(registry, Exe));
    }
}

public class FlyoutPlacementTests
{
    private static readonly PixelRect Monitor = new(0, 0, 1920, 1080);

    [Fact]
    public void AboveTheIconOnABottomTaskbar()
    {
        var workArea = new PixelRect(0, 0, 1920, 1032);
        var icon = new PixelRect(1700, 1040, 1724, 1064);
        Assert.Equal(TaskbarEdge.Bottom, FlyoutPlacement.Edge(Monitor, workArea));
        Assert.Equal(new PixelRect(1532, 680, 1892, 1020), FlyoutPlacement.Place(icon, Monitor, workArea, 360, 340, 12));
    }

    [Fact]
    public void KeptInsideTheWorkArea()
    {
        var workArea = new PixelRect(0, 0, 1920, 1032);
        var icon = new PixelRect(1890, 1040, 1914, 1064);
        Assert.Equal(new PixelRect(1548, 680, 1908, 1020), FlyoutPlacement.Place(icon, Monitor, workArea, 360, 340, 12));
    }

    [Fact]
    public void OtherEdges()
    {
        var top = new PixelRect(0, 48, 1920, 1080);
        Assert.Equal(TaskbarEdge.Top, FlyoutPlacement.Edge(Monitor, top));
        Assert.Equal(60, FlyoutPlacement.Place(new PixelRect(1700, 12, 1724, 36), Monitor, top, 360, 340, 12).Top);
        var left = new PixelRect(60, 0, 1920, 1080);
        Assert.Equal(TaskbarEdge.Left, FlyoutPlacement.Edge(Monitor, left));
        Assert.Equal(72, FlyoutPlacement.Place(new PixelRect(18, 900, 42, 924), Monitor, left, 360, 340, 12).Left);
        var right = new PixelRect(0, 0, 1860, 1080);
        Assert.Equal(TaskbarEdge.Right, FlyoutPlacement.Edge(Monitor, right));
        Assert.Equal(1848, FlyoutPlacement.Place(new PixelRect(1878, 900, 1902, 924), Monitor, right, 360, 340, 12).Right);
    }

    [Fact]
    public void ByTheClockWhenTheIconIsHidden()
    {
        var workArea = new PixelRect(0, 0, 1920, 1032);
        Assert.Equal(new PixelRect(1548, 680, 1908, 1020), FlyoutPlacement.Place(null, Monitor, workArea, 360, 340, 12));
    }

    [Fact]
    public void NeverTallerThanTheScreen()
    {
        var workArea = new PixelRect(0, 0, 1920, 1032);
        var placed = FlyoutPlacement.Place(null, Monitor, workArea, 360, 5000, 12);
        Assert.Equal(12, placed.Top);
        Assert.Equal(1020, placed.Bottom);
    }
}

public class TerminalScriptTests
{
    [Fact]
    public void QuotesForPowerShell()
    {
        Assert.Equal("'it''s'", TerminalScript.Quote("it's"));
        Assert.Equal("self-update", TerminalScript.Display("self-update"));
        Assert.Equal(@"""C:\Program Files\nodejs\node.exe""", TerminalScript.Display(@"C:\Program Files\nodejs\node.exe"));
    }

    [Fact]
    public void RunsTheCommandAsGiven()
    {
        var script = TerminalScript.Build("Updating the Varlatch CLI",
            [@"C:\Program Files\nodejs\node.exe", @"C:\Users\o'neil\varlatch.cjs", "self-update", "0.16.0"]);
        Assert.Contains(@"& 'C:\Program Files\nodejs\node.exe' 'C:\Users\o''neil\varlatch.cjs' 'self-update' '0.16.0'", script);
        Assert.Contains("Write-Host 'Updating the Varlatch CLI'", script);
        Assert.StartsWith("Remove-Item -LiteralPath $PSCommandPath", script);
        Assert.Contains("Read-Host", script);
    }

    [Fact]
    public async Task WorksInWindowsPowerShell()
    {
        // Without the last line, which waits for Enter.
        var lines = TerminalScript.Build("Test", ["cmd.exe", "/c", "exit 3"]).Split('\n').Where(l => !l.Contains("Read-Host"));
        var path = TestDirectory.Path("term") + ".ps1";
        File.WriteAllText(path, string.Join('\n', lines));
        var result = await CliProcess.RunAsync("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path],
                                               new Dictionary<string, string>(Environment.GetEnvironmentVariables()
                                                   .Cast<System.Collections.DictionaryEntry>()
                                                   .ToDictionary(e => (string)e.Key, e => (string?)e.Value ?? "")),
                                               TimeSpan.FromSeconds(60));
        Assert.Contains("> cmd.exe /c \"exit 3\"", result.Stdout);
        Assert.Contains("That did not work (exit 3).", result.Stdout);
        Assert.False(File.Exists(path));
    }
}
