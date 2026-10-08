namespace VarlatchKit.Tests;

public class SessionStoreTests
{
    private readonly FakeCli fake = new();

    private SessionStore Store(string? cliPath = null, bool showLocalhost = false) =>
        new(new Preferences(cliPath: cliPath ?? FakeCli.ScriptPath, showLocalhost: showLocalhost), fake.System());

    [Fact]
    public async Task ReadsSessions()
    {
        fake.SetStatus(FakeCli.Status(FakeCli.Server("https://vl.example.com")));
        var store = Store();
        await store.RefreshVersionAsync();
        await store.RefreshAsync();
        Assert.IsType<CliState.Ready>(store.CliState);
        Assert.Equal(new ReleaseVersion(0, 15, 1), store.CliVersion);
        Assert.StartsWith("v", store.NodeVersion);
        Assert.Equal(["https://vl.example.com"], store.Servers.Select(s => s.Server));
        Assert.Equal(OverallState.Ok, store.Overall());
        Assert.Equal(["cli --version", "cli status --json"], fake.Calls);
        Assert.Equal(FakeCli.ScriptPath, store.CliPath);
    }

    [Fact]
    public async Task SignedOut()
    {
        var store = Store();
        await store.RefreshAsync();
        Assert.IsType<CliState.Ready>(store.CliState);
        Assert.Equal(OverallState.SignedOut, store.Overall());
    }

    [Fact]
    public async Task LocalhostOnlyWhenAskedFor()
    {
        fake.SetStatus(FakeCli.Status(FakeCli.Server("http://localhost:3000"), FakeCli.Server("https://vl.example.com")));
        var store = Store();
        await store.RefreshAsync();
        Assert.Equal(["https://vl.example.com"], store.Servers.Select(s => s.Server));
        store.Update(new Preferences(cliPath: FakeCli.ScriptPath, showLocalhost: true));
        Assert.Equal(2, store.Servers.Count);
        Assert.Equal(2, store.AllServers.Count);
    }

    [Fact]
    public async Task LocalhostAloneCountsAsSignedOut()
    {
        fake.SetStatus(FakeCli.Status(FakeCli.Server("http://127.0.0.1:8080", expired: true)));
        var store = Store();
        await store.RefreshAsync();
        Assert.Equal(OverallState.SignedOut, store.Overall());
    }

    [Fact]
    public async Task ExpiryEventsOncePerTransition()
    {
        fake.SetStatus(FakeCli.Status(FakeCli.Server("https://vl.example.com", expiresIn: 1, expiring: true)));
        var store = Store();
        var events = new List<ExpiryEvent>();
        store.ExpiryEvents += e => events.AddRange(e);
        await store.RefreshAsync();
        await store.RefreshAsync();
        Assert.Equal([SessionHealth.Expiring], events.Select(e => e.Health));
        Assert.Equal(OverallState.Expiring, store.Overall());
        fake.SetStatus(FakeCli.Status(FakeCli.Server("https://vl.example.com", expiresIn: -1, expired: true)));
        await store.RefreshAsync();
        Assert.Equal([SessionHealth.Expiring, SessionHealth.Expired], events.Select(e => e.Health));
        Assert.Equal(OverallState.Expired, store.Overall());
    }

    [Fact]
    public async Task CliMissing()
    {
        var store = new SessionStore(new Preferences(), fake.System(fileExists: _ => false));
        await store.RefreshAsync();
        var missing = Assert.IsType<CliState.Missing>(store.CliState);
        Assert.Equal([Path.Combine(fake.Dir, "Local", "Programs", "Varlatch", "varlatch.cjs"), CliLocator.OnPathDescription],
                     missing.Searched);
        Assert.Equal(OverallState.CliMissing, store.Overall());
    }

    [Fact]
    public async Task SetPathThatCannotRun()
    {
        var store = Store(cliPath: @"C:\nonexistent\varlatch.cjs");
        await store.RefreshAsync();
        Assert.Equal(new CliState.PathUnusable(@"C:\nonexistent\varlatch.cjs"), store.CliState);
        Assert.Equal(OverallState.CliMissing, store.Overall());
    }

    [Fact]
    public async Task TooOldForStatus()
    {
        fake.Set(new() { ["FAKE_NO_STATUS"] = "1" });
        var store = Store();
        await store.RefreshAsync();
        Assert.IsType<CliState.Unsupported>(store.CliState);
        Assert.Equal(OverallState.Unsupported, store.Overall());
    }

    [Fact]
    public async Task NodeMissing()
    {
        // The release CLI is a script that Node.js runs; without node.exe
        // anywhere the app looks, there is nothing to run it with.
        var system = fake.System(fileExists: p => p == FakeCli.ScriptPath);
        var store = new SessionStore(new Preferences(cliPath: FakeCli.ScriptPath), system);
        await store.RefreshAsync();
        Assert.Equal(new CliState.NodeMissing(FakeCli.ScriptPath), store.CliState);
        Assert.Equal(OverallState.Unavailable, store.Overall());
    }

    [Fact]
    public void NodeTooOld()
    {
        var command = new CliCommand(@"C:\x\varlatch.cjs", @"C:\node\node.exe", [@"C:\x\varlatch.cjs"], @"C:\x\varlatch.cjs");
        var failed = new CliResult(1, Stderr: "SyntaxError: Unexpected token\n");
        Assert.Equal(new CliState.NodeMissing(@"C:\x\varlatch.cjs", "v18.20.4"),
                     SessionStore.FailureState(failed, command, "v18.20.4"));
        Assert.Equal(new CliState.Unavailable("SyntaxError: Unexpected token"),
                     SessionStore.FailureState(failed, command, "v22.17.0"));
        Assert.Equal(22, SessionStore.NodeMajor("v22.17.0"));
        Assert.Null(SessionStore.NodeMajor("node"));
    }

    [Fact]
    public async Task UnreadableOutput()
    {
        fake.SetStatus("not json");
        var store = Store();
        await store.RefreshAsync();
        Assert.Equal(new CliState.Unavailable("unreadable status output"), store.CliState);
    }

    [Fact]
    public async Task NeverTwoReadsAtOnce()
    {
        fake.SetStatus(FakeCli.Status());
        var store = Store();
        await Task.WhenAll(store.RefreshAsync(), store.RefreshAsync(), store.RefreshAsync());
        // The first read may have been answered by the time the others asked;
        // either way, one more read follows them, and never one per request.
        await store.RefreshAsync();
        var reads = fake.Calls.Count(c => c == "cli status --json");
        Assert.InRange(reads, 2, 3);
    }

    [Fact]
    public async Task ReadsRequestedWhileOneRunsWaitForIt()
    {
        fake.SetStatus(FakeCli.Status());
        var store = Store();
        var first = store.RefreshAsync();
        var second = store.RefreshAsync();
        var third = store.RefreshAsync();
        Assert.True(second.IsCompleted && third.IsCompleted);
        await first;
        // One read, plus one more for the requests made while it ran.
        Assert.Equal(2, fake.Calls.Count(c => c == "cli status --json"));
    }
}

public class CliProcessTests
{
    private readonly FakeCli fake = new();

    private string Node => new CliLocator(fake.System()).FindNode() ?? throw new InvalidOperationException("node.exe not found");

    private IReadOnlyDictionary<string, string> Environment => fake.System().Environment;

    [Fact]
    public async Task SeparateOutputs()
    {
        var result = await CliProcess.RunAsync(Node, ["-e", "console.log('out'); console.error('err'); process.exitCode = 3"],
                                               Environment, TimeSpan.FromSeconds(10));
        Assert.Equal("out\n", result.Stdout);
        Assert.Equal("err\n", result.Stderr);
        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Timeout()
    {
        var started = DateTimeOffset.Now;
        var result = await CliProcess.RunAsync(Node, ["-e", "setTimeout(() => {}, 30000)"], Environment, TimeSpan.FromSeconds(0.5));
        Assert.True(result.TimedOut);
        Assert.True(result.Signaled);
        Assert.True(DateTimeOffset.Now - started < TimeSpan.FromSeconds(5));
        Assert.Equal("the CLI did not finish in time", result.LastErrorLines());
    }

    [Fact]
    public async Task Terminate()
    {
        var process = new CliProcess(Node, ["-e", "setTimeout(() => {}, 30000)"], Environment);
        process.Start();
        Assert.True(process.IsRunning);
        process.Terminate();
        var result = await process.WaitAsync();
        Assert.True(result.Signaled);
        Assert.False(result.TimedOut);
        Assert.False(process.IsRunning);
    }

    [Fact]
    public async Task TerminateEndsWhatItStarted()
    {
        // A cmd.exe in front of node, as a .cmd shim would be: ending it must
        // end node too, or a cancelled sign-in would go on waiting.
        var pidFile = Path.Combine(fake.Dir, "child.pid");
        var script = Path.Combine(fake.Dir, "wait.js");
        File.WriteAllText(script, "require('fs').writeFileSync(process.argv[2], String(process.pid)); setTimeout(() => {}, 30000);\n");
        var shim = Path.Combine(fake.Dir, "wait.cmd");
        File.WriteAllText(shim, $"@\"{Node}\" \"%~dp0wait.js\" \"%~dp0child.pid\"\r\n");
        var process = new CliProcess(shim, [], Environment);
        process.Start();
        for (var i = 0; i < 200 && !File.Exists(pidFile); i++) await Task.Delay(50, TestContext.Current.CancellationToken);
        var child = int.Parse(File.ReadAllText(pidFile));
        process.Terminate();
        await process.WaitAsync();
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Throws<ArgumentException>(() => System.Diagnostics.Process.GetProcessById(child));
    }

    [Fact]
    public async Task CannotStart()
    {
        var result = await CliProcess.RunAsync(@"C:\nonexistent\varlatch.exe", [], Environment, TimeSpan.FromSeconds(5));
        Assert.NotNull(result.LaunchError);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void LastErrorLines()
    {
        var r = new CliResult(1, Stderr: "varlatch login: one\n  indented detail\nvarlatch login: two\nthree\n");
        Assert.Equal("varlatch login: two three", r.LastErrorLines());
        Assert.Equal("only stdout", new CliResult(1, Stdout: "only stdout\n").LastErrorLines());
    }

    [Fact]
    public async Task StreamsStdout()
    {
        var seen = new List<string>();
        var process = new CliProcess(Node, ["-e", "console.log('first'); setTimeout(() => console.log('second'), 300)"],
                                     Environment, text => { lock (seen) seen.Add(text); });
        process.Start();
        await process.WaitAsync();
        lock (seen)
        {
            Assert.Equal("first\n", seen.First().Replace("\r\n", "\n"));
            Assert.Equal("first\nsecond\n", seen.Last().Replace("\r\n", "\n"));
        }
    }

    [Fact]
    public async Task RefusesWhatCmdWouldMisread()
    {
        var shim = Path.Combine(fake.Dir, "varlatch.cmd");
        File.WriteAllText(shim, "@echo varlatch 0.15.1\r\n");
        var cli = new CliInvocation(new CliCommand(shim, shim, []), Environment);
        var result = await cli.RunAsync(["login", "--server", "https://vl.example.com/a&b"], TimeSpan.FromSeconds(5));
        Assert.Contains("cannot pass \"https://vl.example.com/a&b\" to varlatch.cmd", result.LaunchError);
        var plain = await cli.RunAsync(["--version"], TimeSpan.FromSeconds(10));
        Assert.True(plain.Succeeded);
        Assert.Equal("varlatch 0.15.1\n", plain.Stdout);
    }
}
