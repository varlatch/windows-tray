using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using static VarlatchKit.Tests.Dates;

namespace VarlatchKit.Tests;

public class SignInTests
{
    private readonly FakeCli fake = new();

    private sealed class Outcomes
    {
        public List<SignInOutcome> List { get; } = [];
        public int Finished { get; set; }
        public List<string> Links { get; } = [];
    }

    private (SignInController, Outcomes) Controller()
    {
        var outcomes = new Outcomes();
        var invocation = fake.Invocation();
        var controller = new SignInController(() => invocation);
        controller.Outcome += o => { lock (outcomes) outcomes.List.Add(o); };
        controller.Finished += () => { lock (outcomes) outcomes.Finished++; };
        controller.LinkFound += (_, link) => { lock (outcomes) outcomes.Links.Add(link); };
        return (controller, outcomes);
    }

    private static async Task<string?> WaitForLink(SignInController controller)
    {
        for (var i = 0; i < 200; i++)
        {
            if (controller.State is SignInState.Browser { Link: { } link }) return link;
            await Task.Delay(50);
        }
        return null;
    }

    [Fact]
    public async Task SignsIn()
    {
        fake.Set(new() { ["FAKE_LOGIN_SLEEP"] = "1" });
        var (controller, outcomes) = Controller();
        controller.SignIn("https://vl.example.com", ["--ttl", "28800"]);
        Assert.False(controller.IsIdle);
        var link = await WaitForLink(controller);
        Assert.Equal("https://vl.example.com/enroll?callback=http%3A%2F%2F127.0.0.1%3A49152%2F", link);
        await controller.WaitUntilIdleAsync();
        Assert.True(controller.IsIdle);
        Assert.Equal([new SignInOutcome.SignedIn("https://vl.example.com", null)], outcomes.List);
        Assert.Equal("Logged in to vl.example.com.", outcomes.List[0].Text);
        Assert.Equal(1, outcomes.Finished);
        // The app opens the link, once; the CLI is told not to.
        Assert.Equal([link!], outcomes.Links);
        Assert.Equal(["VARLATCH_NO_BROWSER=1"], fake.EnvironmentSeen);
        Assert.Equal(["cli login --server https://vl.example.com --ttl 28800"], fake.Calls);
    }

    [Fact]
    public async Task TheLinkComesBeforeTheOutcomeAlsoWhenTheCliEndsAtOnce()
    {
        // No wait between the address and the end: whichever is handled
        // first, the app hears of the link once, and before the outcome.
        for (var i = 0; i < 5; i++)
        {
            var events = new List<string>();
            var invocation = fake.Invocation();
            var controller = new SignInController(() => invocation);
            controller.LinkFound += (_, _) => { lock (events) events.Add("link"); };
            controller.Outcome += o => { lock (events) events.Add(o.GetType().Name); };
            controller.SignIn("https://vl.example.com");
            await controller.WaitUntilIdleAsync();
            lock (events) Assert.Equal(["link", "SignedIn"], events);
        }
    }

    [Fact]
    public async Task OneAtATimeAndASecondClickGivesTheLink()
    {
        fake.Set(new() { ["FAKE_LOGIN_SLEEP"] = "2" });
        var (controller, _) = Controller();
        Assert.Null(controller.SignIn("https://vl.example.com"));
        await WaitForLink(controller);
        Assert.EndsWith("/enroll?callback=http%3A%2F%2F127.0.0.1%3A49152%2F", controller.SignIn("https://other.example.com"));
        await controller.WaitUntilIdleAsync();
        Assert.Single(fake.Calls);
    }

    [Fact]
    public async Task ReportsAFailedRevoke()
    {
        fake.Set(new() { ["FAKE_REVOKE_FAIL"] = "1" });
        var (controller, outcomes) = Controller();
        controller.SignIn("https://vl.example.com");
        await controller.WaitUntilIdleAsync();
        Assert.Equal([new SignInOutcome.SignedIn("https://vl.example.com",
                         "varlatch: previous credential not revoked (network error); it may remain live.")], outcomes.List);
    }

    [Fact]
    public async Task Fails()
    {
        fake.Set(new() { ["FAKE_LOGIN_RC"] = "1" });
        var (controller, outcomes) = Controller();
        controller.SignIn("https://vl.example.com");
        await controller.WaitUntilIdleAsync();
        Assert.Equal([new SignInOutcome.Failed("https://vl.example.com", "varlatch login: browser sign-in failed: no passkey", Browser: true)],
                     outcomes.List);
        Assert.Equal("Sign-in to vl.example.com failed: varlatch login: browser sign-in failed: no passkey", outcomes.List[0].Text);
        Assert.Equal(1, outcomes.Finished);
    }

    [Fact]
    public async Task Cancels()
    {
        fake.Set(new() { ["FAKE_LOGIN_SLEEP"] = "30" });
        var (controller, outcomes) = Controller();
        controller.SignIn("https://vl.example.com");
        await WaitForLink(controller);
        controller.Cancel();
        await controller.WaitUntilIdleAsync();
        Assert.Equal([new SignInOutcome.Cancelled("https://vl.example.com")], outcomes.List);
        Assert.Equal("Sign-in to vl.example.com cancelled.", outcomes.List[0].Text);
    }

    [Fact]
    public async Task CancelsQuietly()
    {
        fake.Set(new() { ["FAKE_LOGIN_SLEEP"] = "30" });
        var (controller, outcomes) = Controller();
        controller.SignIn("https://vl.example.com");
        await WaitForLink(controller);
        controller.Cancel(quietly: true);
        await controller.WaitUntilIdleAsync();
        Assert.Empty(outcomes.List);
        Assert.Equal(1, outcomes.Finished);
    }

    [Fact]
    public void NoCli()
    {
        var outcomes = new List<SignInOutcome>();
        var controller = new SignInController(() => null);
        controller.Outcome += outcomes.Add;
        controller.SignIn("https://vl.example.com");
        Assert.True(controller.IsIdle);
        Assert.Equal([new SignInOutcome.Failed("https://vl.example.com", "the varlatch CLI was not found.", Browser: false)], outcomes);
    }

    [Fact]
    public void EnrollLink()
    {
        Assert.Equal("https://a.example.com/enroll?callback=x",
                     SignInController.EnrollLink("Complete passkey sign-in in your browser:\n  https://a.example.com/enroll?callback=x\n"));
        Assert.Equal("https://a.example.com/enroll?callback=x",
                     SignInController.EnrollLink("Complete passkey sign-in in your browser:\r\n  https://a.example.com/enroll?callback=x\r\n"));
        Assert.Null(SignInController.EnrollLink("Complete passkey sign-in in your browser:\n"));
    }
}

public class StoreActionTests
{
    private readonly FakeCli fake = new();

    private SessionStore Store() =>
        new(new Preferences(cliPath: FakeCli.ScriptPath), fake.System(), Path.Combine(fake.Dir, "state"));

    [Fact]
    public async Task VerifyPutsResultsOnTheRows()
    {
        fake.SetStatus(FakeCli.Status(FakeCli.Server("https://a.example.com"), FakeCli.Server("https://b.example.com")));
        fake.SetProbe("""{"version":1,"servers":[{"server":"https://a.example.com","credentialId":"crd_x","probe":{"state":"valid","detail":null}},{"server":"https://b.example.com","credentialId":"crd_x","probe":{"state":"invalid","detail":"401 UNAUTHENTICATED"}}]}""");
        var store = Store();
        await store.RefreshAsync();
        await store.VerifyAsync();
        Assert.Null(store.VerifyError);
        Assert.Equal([ProbeState.Valid, ProbeState.Invalid], store.Servers.Select(s => store.ProbeFor(s)!.State));
        Assert.Equal("invalid (401 UNAUTHENTICATED)", Sessions.ProbeText(store.ProbeFor(store.Servers[1])!));
        // They stay through the next read of the same credentials.
        await store.RefreshAsync();
        Assert.Equal(ProbeState.Valid, store.ProbeFor(store.Servers[0])!.State);
        Assert.Contains("cli status --probe --json", fake.Calls);
    }

    [Fact]
    public async Task ANewCredentialDropsTheProbe()
    {
        fake.SetStatus(FakeCli.Status(FakeCli.Server("https://a.example.com")));
        fake.SetProbe("""{"version":1,"servers":[{"server":"https://a.example.com","credentialId":"crd_x","probe":{"state":"valid","detail":null}}]}""");
        var store = Store();
        await store.VerifyAsync();
        Assert.NotNull(store.ProbeFor(store.Servers[0]));
        fake.SetStatus(FakeCli.Status(FakeCli.Server("https://a.example.com", credentialId: "crd_new")));
        await store.RefreshAsync();
        Assert.Null(store.ProbeFor(store.Servers[0]));
    }

    [Fact]
    public async Task VerifyFailure()
    {
        fake.SetProbe("not json");
        var store = Store();
        await store.VerifyAsync();
        Assert.Equal("Verify failed: unreadable output from the CLI.", store.VerifyError);
        Assert.False(store.Verifying);
    }

    [Fact]
    public async Task Logout()
    {
        fake.SetStatus(FakeCli.Status(FakeCli.Server("https://a.example.com")));
        var store = Store();
        var result = await store.LogoutAsync("https://a.example.com");
        Assert.True(result.Succeeded);
        Assert.Equal("Logged out of a.example.com.", result.Message);
        Assert.Contains("cli logout --server https://a.example.com", fake.Calls);
        fake.Set(new() { ["FAKE_LOGOUT_FAIL"] = "1" });
        var failed = await store.LogoutAsync("https://a.example.com");
        Assert.False(failed.Succeeded);
        Assert.Equal("Log out of a.example.com failed: varlatch: No stored credential for https://a.example.com", failed.Message);
    }

    [Fact]
    public async Task RemembersServersAcrossLogout()
    {
        fake.SetStatus(FakeCli.Status(FakeCli.Server("http://localhost:3000"), FakeCli.Server("https://vl.example.com")));
        var store = Store();
        await store.RefreshAsync();
        Assert.Equal("https://vl.example.com", store.KnownServer);
        fake.SetStatus(FakeCli.Status());
        await store.RefreshAsync();
        Assert.Empty(store.Servers);
        Assert.Equal("https://vl.example.com", store.KnownServer);
        // Kept on disk for the next start.
        var again = Store();
        Assert.Equal("https://vl.example.com", again.KnownServer);
        Assert.Equal(["http://localhost:3000", "https://vl.example.com"], again.Memory.Servers);
    }
}

public class ServerMemoryTests
{
    [Fact]
    public void NewestFirstWithoutRepeats()
    {
        var memory = new ServerMemory(["https://a", "https://b"]).Remember(["https://c", "https://a"]);
        Assert.Equal(["https://c", "https://a", "https://b"], memory.Servers);
        Assert.Equal(["https://c", "https://b"], memory.Forget("https://a").Servers);
    }

    [Fact]
    public void Limited() =>
        Assert.Equal(ServerMemory.Limit, new ServerMemory().Remember(Enumerable.Range(0, 15).Select(i => $"https://s{i}")).Servers.Count);

    [Fact]
    public void RealDeploymentsFirst()
    {
        var memory = new ServerMemory(["http://localhost:3000", "https://vl.example.com"]);
        Assert.Equal("https://vl.example.com", memory.Preferred(showLocalhost: false));
        Assert.Equal("https://vl.example.com", memory.Preferred(showLocalhost: true));
        Assert.Null(new ServerMemory(["http://localhost:3000"]).Preferred(showLocalhost: false));
        Assert.Equal("http://localhost:3000", new ServerMemory(["http://localhost:3000"]).Preferred(showLocalhost: true));
    }

    [Fact]
    public void SavesAndLoads()
    {
        var path = Path.Combine(TestDirectory.Path("memory"), "servers.json");
        new ServerMemory(["https://a", "https://b"]).Save(path);
        Assert.Equal(new ServerMemory(["https://a", "https://b"]), ServerMemory.Load(path));
        Assert.Equal(new ServerMemory(), ServerMemory.Load(path + ".missing"));
    }
}

public class DeviceSignInTests
{
    private readonly FakeCli fake = new();

    private (SignInController, List<SignInOutcome>) Controller()
    {
        var outcomes = new List<SignInOutcome>();
        var invocation = fake.Invocation();
        var controller = new SignInController(() => invocation);
        controller.Outcome += o => { lock (outcomes) outcomes.Add(o); };
        return (controller, outcomes);
    }

    private static async Task<DeviceCode?> WaitForCode(SignInController controller)
    {
        for (var i = 0; i < 200; i++)
        {
            if (controller.State is SignInState.Device { Code: { } code }) return code;
            await Task.Delay(50);
        }
        return null;
    }

    [Fact]
    public async Task Approved()
    {
        var (controller, outcomes) = Controller();
        await controller.SignInFromAnotherDeviceAsync("https://vl.example.com", ["--ttl", "3600"]);
        Assert.Equal([new SignInOutcome.SignedIn("https://vl.example.com", null)], outcomes);
        Assert.True(controller.IsIdle);
        // --ttl goes with --start only; --wait refuses it.
        Assert.Equal([
            "cli login --server https://vl.example.com --start --json --ttl 3600",
            "cli login --server https://vl.example.com --wait --timeout 600 --json",
        ], fake.Calls);
    }

    [Fact]
    public async Task ShowsTheCodeWhileWaiting()
    {
        fake.Set(new() { ["FAKE_WAIT"] = "pending-once" });
        var (controller, outcomes) = Controller();
        var task = controller.SignInFromAnotherDeviceAsync("https://vl.example.com");
        var code = await WaitForCode(controller);
        Assert.Equal("https://vl.example.com/device", code?.VerificationUri);
        Assert.Equal("BCDF-GHJK", code?.UserCode);
        Assert.StartsWith("Code expires in 9m", code?.ExpiryText());
        await task;
        // Pending once (75), asked again, then approved.
        Assert.Equal(2, fake.Calls.Count(c => c.Contains("--wait")));
        Assert.Equal([new SignInOutcome.SignedIn("https://vl.example.com", null)], outcomes);
    }

    [Fact]
    public async Task Denied()
    {
        fake.Set(new() { ["FAKE_WAIT"] = "deny" });
        var (controller, outcomes) = Controller();
        await controller.SignInFromAnotherDeviceAsync("https://vl.example.com");
        Assert.Equal([new SignInOutcome.Denied("https://vl.example.com")], outcomes);
        Assert.Equal("Sign-in to vl.example.com was denied.", outcomes[0].Text);
    }

    [Fact]
    public async Task CodeExpired()
    {
        fake.Set(new() { ["FAKE_WAIT"] = "expire" });
        var (controller, outcomes) = Controller();
        await controller.SignInFromAnotherDeviceAsync("https://vl.example.com");
        Assert.Equal([new SignInOutcome.CodeExpired("https://vl.example.com")], outcomes);
        Assert.Equal("The sign-in code for vl.example.com expired before it was approved.", outcomes[0].Text);
    }

    [Fact]
    public async Task StartRefused()
    {
        fake.Set(new() { ["FAKE_START_FAIL"] = "1" });
        var (controller, outcomes) = Controller();
        await controller.SignInFromAnotherDeviceAsync("https://vl.example.com");
        Assert.Equal([new SignInOutcome.Failed("https://vl.example.com",
                         "varlatch login: the server refused to start a sign-in (NOT_FOUND)", Browser: false)], outcomes);
        Assert.DoesNotContain(fake.Calls, c => c.Contains("--wait"));
    }

    [Fact]
    public async Task CancelWhileWaiting()
    {
        fake.Set(new() { ["FAKE_WAIT"] = "hang" });
        var (controller, outcomes) = Controller();
        var task = controller.SignInFromAnotherDeviceAsync("https://vl.example.com");
        await WaitForCode(controller);
        controller.Cancel();
        await task;
        Assert.Equal([new SignInOutcome.Cancelled("https://vl.example.com")], outcomes);
        Assert.True(controller.IsIdle);
    }

    [Fact]
    public async Task TakesOverABrowserSignInQuietly()
    {
        fake.Set(new() { ["FAKE_LOGIN_SLEEP"] = "30" });
        var (controller, outcomes) = Controller();
        controller.SignIn("https://vl.example.com");
        Assert.False(controller.IsIdle);
        await controller.SignInFromAnotherDeviceAsync("https://vl.example.com");
        // No "cancelled" for the browser sign-in that made way.
        Assert.Equal([new SignInOutcome.SignedIn("https://vl.example.com", null)], outcomes);
    }

    [Fact]
    public void ExpiryText()
    {
        var now = Date("2026-10-07T12:00:00Z");
        DeviceCode Code(string expires) => new("https://a/device", "X", Date(expires));
        Assert.Equal("Code expires in 9m", Code("2026-10-07T12:09:59Z").ExpiryText(now));
        Assert.Equal("Code expires in under a minute", Code("2026-10-07T12:00:30Z").ExpiryText(now));
        Assert.Equal("Code expired", Code("2026-10-07T11:59:00Z").ExpiryText(now));
        Assert.Equal("BCDF-GHJK", DeviceCode.Parse("""{"verificationUri":"https://a/device","userCode":"BCDF-GHJK","expiresAt":"2026-10-07T12:10:00.000Z"}""")?.UserCode);
        Assert.Null(DeviceCode.Parse("""{"state":"pending"}"""));
    }

    [Fact]
    public void WaitStates()
    {
        Assert.Equal("denied", SignInController.WaitState("""{"state":"denied"}"""));
        Assert.Equal("expired", SignInController.WaitState("noise\n{\"state\":\"expired\"}\n"));
        Assert.Null(SignInController.WaitState(""));
    }
}

public class QrCodeTests
{
    [Fact]
    public void ReadsBackTheAddress()
    {
        // Scaled up, with a white quiet zone, as the panel shows it.
        var (pixels, side) = QrCode.Bgra("https://vl.example.com/device", pixelsPerModule: 8, quietModules: 4);
        var source = new RGBLuminanceSource(pixels, side, side, RGBLuminanceSource.BitmapFormat.BGRA32);
        var result = new QRCodeReader().decode(new BinaryBitmap(new HybridBinarizer(source)));
        Assert.Equal("https://vl.example.com/device", result?.Text);
    }

    [Fact]
    public void NoQuietZoneInTheModules()
    {
        var modules = QrCode.Modules("https://vl.example.com/device");
        // A finder pattern starts in the top left corner.
        Assert.True(modules[0, 0] && modules[0, 6] && modules[6, 0]);
        Assert.False(modules[1, 1]);
    }
}
