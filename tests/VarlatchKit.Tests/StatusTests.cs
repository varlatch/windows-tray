using System.Text.Json.Nodes;
using static VarlatchKit.Tests.Dates;

namespace VarlatchKit.Tests;

public class StatusDocumentTests
{
    [Fact]
    public void ReadsTheCliDocument()
    {
        var doc = StatusDocument.Parse("""
            {"version": 1,
             "servers": [{"server": "https://vl.example.com", "name": null,
                          "issuedAt": "2026-10-07T06:35:12.544Z", "expiresAt": "2026-10-07T18:35:12.470Z",
                          "credentialId": "crd_1", "expired": false, "expiring": false}],
             "repo": null}
            """);
        Assert.NotNull(doc);
        Assert.Equal(1, doc.Version);
        var s = Assert.Single(doc.Servers);
        Assert.Equal("https://vl.example.com", s.Server);
        Assert.Null(s.Name);
        Assert.Equal(Date("2026-10-07T06:35:12.544Z"), s.IssuedAt);
        Assert.Equal(43199.926, (s.ExpiresAt!.Value - s.IssuedAt!.Value).TotalSeconds, 3);
        Assert.Equal("crd_1", s.CredentialId);
        Assert.False(s.Expired);
        Assert.False(s.Expiring);
        Assert.True(s.ReportsExpiring);
        Assert.Null(s.Probe);
    }

    [Fact]
    public void EverythingButTheServerMayBeNull()
    {
        var doc = StatusDocument.Parse("""
            {"version":1,"servers":[{"server":"https://vl.example.com","name":null,"issuedAt":null,
             "expiresAt":null,"credentialId":null,"expired":null,"expiring":null}],"repo":null}
            """);
        var s = Assert.Single(doc!.Servers);
        Assert.True(s.IssuedAt is null && s.ExpiresAt is null && s.Expired is null && s.Expiring is null);
        Assert.True(s.ReportsExpiring);
        Assert.Equal(SessionHealth.Ok, s.Health(DateTimeOffset.Now));
        Assert.Equal("Logged in, no expiry recorded", Sessions.Detail(s, DateTimeOffset.Now));
    }

    [Fact]
    public void OlderClisLeaveOutExpiring()
    {
        var doc = StatusDocument.Parse("""
            {"servers":[{"server":"https://a.example.com","issuedAt":"2026-10-07T00:00:00Z",
             "expiresAt":"2026-10-07T10:00:00Z","expired":false}]}
            """);
        var s = Assert.Single(doc!.Servers);
        Assert.False(s.ReportsExpiring);
        // 10-hour lifetime: expiring with under 2 hours left.
        Assert.Equal(SessionHealth.Ok, s.Health(Date("2026-10-07T07:59:00Z")));
        Assert.Equal(SessionHealth.Expiring, s.Health(Date("2026-10-07T08:01:00Z")));
        Assert.Equal(SessionHealth.Expired, s.Health(Date("2026-10-07T10:00:00Z")));
    }

    [Fact]
    public void ProbeResults()
    {
        var doc = StatusDocument.Parse("""
            {"version":1,"servers":[
              {"server":"https://a.example.com","probe":{"state":"valid","detail":null}},
              {"server":"https://b.example.com","probe":{"state":"invalid","detail":"401 UNAUTHENTICATED"}},
              {"server":"https://c.example.com","probe":{"state":"unreachable","detail":"ECONNREFUSED"}},
              {"server":"https://d.example.com","probe":{"state":"brand-new","detail":null}}]}
            """);
        Assert.Equal(
            [ProbeState.Valid, ProbeState.Invalid, ProbeState.Unreachable, ProbeState.Unreachable],
            doc!.Servers.Select(s => s.Probe!.State));
        Assert.Equal("401 UNAUTHENTICATED", doc.Servers[1].Probe!.Detail);
    }

    [Fact]
    public void NoCredentials() =>
        Assert.Empty(StatusDocument.Parse("""{"version":1,"servers":[],"repo":null}""")!.Servers);

    [Fact]
    public void NotADocument()
    {
        Assert.Null(StatusDocument.Parse("No stored credentials."));
        Assert.Null(StatusDocument.Parse("""{"servers":[{"name":"no server"}]}"""));
        Assert.Null(StatusDocument.Parse("""{"version":"one","servers":[]}"""));
        Assert.Null(StatusDocument.Parse("[]"));
    }

    [Fact]
    public void BadTimestampsReadAsMissing()
    {
        var doc = StatusDocument.Parse("""{"servers":[{"server":"https://a","expiresAt":"tomorrow"}]}""");
        Assert.Null(doc!.Servers[0].ExpiresAt);
        Assert.Null(ServerStatus.ParseDate("2026-10-07"));
        Assert.Equal(Date("2026-10-07T12:00:00Z"), ServerStatus.ParseDate("2026-10-07T14:00:00+02:00"));
    }
}

public class SessionRuleTests
{
    private readonly DateTimeOffset issued = Date("2026-10-07T06:00:00Z");
    private readonly DateTimeOffset expires = Date("2026-10-07T18:00:00Z");

    [Fact]
    public void TheCliDecidesExpiring()
    {
        var s = new ServerStatus("https://a") { IssuedAt = issued, ExpiresAt = expires, Expired = false, Expiring = true };
        Assert.Equal(SessionHealth.Expiring, s.Health(Date("2026-10-07T07:00:00Z")));
    }

    [Fact]
    public void ExpiredWinsAndTimePassingCounts()
    {
        var s = new ServerStatus("https://a") { IssuedAt = issued, ExpiresAt = expires, Expired = false, Expiring = false };
        Assert.Equal(SessionHealth.Ok, s.Health(Date("2026-10-07T17:59:59Z")));
        Assert.Equal(SessionHealth.Expired, s.Health(Date("2026-10-07T18:00:00Z")));
        Assert.Equal(SessionHealth.Expired, new ServerStatus("https://a") { Expired = true }.Health());
    }

    [Fact]
    public void CountdownText()
    {
        var now = Date("2026-10-07T14:47:30Z");
        Assert.Equal("3h 12m left", Sessions.Remaining(expires, now));
        Assert.Equal("12m left", Sessions.Remaining(Date("2026-10-07T15:00:00Z"), now));
        Assert.Equal("0m left", Sessions.Remaining(Date("2026-10-07T14:47:40Z"), now));
        Assert.Equal("expired", Sessions.Remaining(now, now));
        var s = new ServerStatus("https://a") { IssuedAt = issued, ExpiresAt = expires, Expired = false, Expiring = false };
        Assert.Equal("Logged in, 3h 12m left", Sessions.Detail(s, now));
        Assert.Equal("Session expired", Sessions.Detail(s, expires));
    }

    [Fact]
    public void Hosts()
    {
        Assert.Equal("vl.example.com", Sessions.Host("https://vl.example.com"));
        Assert.Equal("vl.example.com:8443", Sessions.Host("https://vl.example.com:8443/varlatch/"));
        Assert.Equal("vl.example.com", Sessions.Host("vl.example.com"));
    }

    [Fact]
    public void LocalhostFilter()
    {
        Assert.True(Sessions.IsLocalhost("http://localhost:3000"));
        Assert.True(Sessions.IsLocalhost("http://127.0.0.1:8080"));
        Assert.True(Sessions.IsLocalhost("HTTP://LOCALHOST"));
        Assert.False(Sessions.IsLocalhost("https://vl.example.com"));
        Assert.False(Sessions.IsLocalhost("https://localhost.example.com"));
        Assert.False(Sessions.IsLocalhost("https://my-localhost.example.com"));
        ServerStatus[] all = [new("http://localhost:3000"), new("https://vl.example.com")];
        Assert.Equal(["https://vl.example.com"], Sessions.Visible(all, showLocalhost: false).Select(s => s.Server));
        Assert.Equal(2, Sessions.Visible(all, showLocalhost: true).Count);
    }

    [Fact]
    public void OverallIsTheWorst()
    {
        var now = Date("2026-10-07T12:00:00Z");
        var ok = new ServerStatus("https://a") { ExpiresAt = Date("2026-10-07T18:00:00Z"), Expired = false, Expiring = false };
        var soon = new ServerStatus("https://b") { ExpiresAt = Date("2026-10-07T13:00:00Z"), Expired = false, Expiring = true };
        var gone = new ServerStatus("https://c") { Expired = true };
        Assert.Equal(OverallState.SignedOut, Sessions.Overall([], now));
        Assert.Equal(OverallState.Ok, Sessions.Overall([ok], now));
        Assert.Equal(OverallState.Expiring, Sessions.Overall([ok, soon], now));
        Assert.Equal(OverallState.Expired, Sessions.Overall([soon, gone, ok], now));
        Assert.Equal(Badge.None, OverallState.Ok.Badge());
        Assert.Equal(Badge.Warning, OverallState.Expiring.Badge());
        Assert.All([OverallState.SignedOut, OverallState.Expired, OverallState.CliMissing, OverallState.Unsupported, OverallState.Unavailable],
                   s => Assert.Equal(Badge.Error, s.Badge()));
        Assert.True(OverallState.Unavailable.CliUnusable());
        Assert.False(OverallState.Expired.CliUnusable());
    }

    [Fact]
    public void ProbeTexts()
    {
        Assert.Equal("verified", Sessions.ProbeText(new Probe(ProbeState.Valid)));
        Assert.Equal("invalid (401 UNAUTHENTICATED)", Sessions.ProbeText(new Probe(ProbeState.Invalid, "401 UNAUTHENTICATED")));
        Assert.Equal("invalid", Sessions.ProbeText(new Probe(ProbeState.Invalid)));
        Assert.Equal("server unreachable", Sessions.ProbeText(new Probe(ProbeState.Unreachable, "ECONNREFUSED")));
    }
}

public class ExpiryTrackerTests
{
    private readonly DateTimeOffset now = Date("2026-10-07T12:00:00Z");

    private static ServerStatus Server(string name, bool expiring = false, bool expired = false) =>
        new($"https://{name}.example.com")
        {
            ExpiresAt = Date("2026-10-07T13:00:00Z"),
            Expired = expired,
            Expiring = expiring,
        };

    [Fact]
    public void OncePerTransition()
    {
        var tracker = new ExpiryTracker();
        Assert.Empty(tracker.Update([Server("a")], now));
        var first = tracker.Update([Server("a", expiring: true)], now);
        Assert.Equal([SessionHealth.Expiring], first.Select(e => e.Health));
        Assert.Equal("Credential for a.example.com expires soon (1h 0m left).", first[0].Body(now));
        Assert.Empty(tracker.Update([Server("a", expiring: true)], now));
        var expired = tracker.Update([Server("a", expired: true)], now);
        Assert.Equal([SessionHealth.Expired], expired.Select(e => e.Health));
        Assert.Equal("Session expired", expired[0].Title);
        Assert.Equal("Credential for a.example.com has expired. Log in again.", expired[0].Body());
        Assert.Empty(tracker.Update([Server("a", expired: true)], now));
    }

    [Fact]
    public void RenewedThenExpiringAgainNotifiesAgain()
    {
        var tracker = new ExpiryTracker();
        tracker.Update([Server("a", expiring: true)], now);
        Assert.Empty(tracker.Update([Server("a")], now));
        Assert.Single(tracker.Update([Server("a", expiring: true)], now));
    }

    [Fact]
    public void EachServerOnItsOwn()
    {
        var tracker = new ExpiryTracker();
        var events = tracker.Update([Server("a", expiring: true), Server("b", expired: true), Server("c")], now);
        Assert.Equal(["https://a.example.com", "https://b.example.com"], events.Select(e => e.Server));
        Assert.Empty(tracker.Update([Server("a", expiring: true), Server("b", expired: true)], now));
    }
}

public class PreferencesTests
{
    [Fact]
    public void Defaults()
    {
        var p = Preferences.From(new JsonObject());
        Assert.Equal(new Preferences(), p);
        Assert.Equal(30, p.RefreshInterval);
        Assert.True(p.NotifyExpiry && p.ShowWhenLoggedOut && p.LaunchAtLogin);
        Assert.False(p.ShowLocalhost || p.CheckUpdates);
        Assert.Equal(0, p.SessionHours);
        Assert.Empty(p.TtlArguments);
    }

    [Fact]
    public void Limits()
    {
        Assert.Equal(15, new Preferences(refreshInterval: 5).RefreshInterval);
        Assert.Equal(["--ttl", "28800"], new Preferences(sessionHours: 8).TtlArguments);
        Assert.Equal(["--ttl", "86400"], new Preferences(sessionHours: 24).TtlArguments);
        Assert.Equal(0, new Preferences(sessionHours: 25).SessionHours);
        Assert.Empty(new Preferences(sessionHours: -1).TtlArguments);
    }

    [Fact]
    public void ReadsStoredValues()
    {
        var p = Preferences.From(JsonNode.Parse("""
            {"refreshInterval": 10, "showLocalhost": true, "launchAtLogin": false,
             "cliPath": "%LOCALAPPDATA%\\varlatch\\varlatch.cjs", "notifyExpiry": "yes"}
            """)!.AsObject());
        Assert.Equal(15, p.RefreshInterval);
        Assert.True(p.ShowLocalhost);
        Assert.False(p.LaunchAtLogin);
        Assert.Equal(@"%LOCALAPPDATA%\varlatch\varlatch.cjs", p.CliPath);
        // A mistyped value takes its default.
        Assert.True(p.NotifyExpiry);
    }

    [Fact]
    public void SettingsFileKeepsOtherKeys()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vl-settings-{Guid.NewGuid():N}", "settings.json");
        var file = new SettingsFile(path);
        Assert.Equal(new Preferences(), file.Preferences);
        file.SetFlag("debugHooks", true);
        file.Save(new Preferences(sessionHours: 8, checkUpdates: true));
        Assert.True(file.Flag("debugHooks"));
        Assert.False(file.Flag("hasLaunched"));
        Assert.Equal(new Preferences(sessionHours: 8, checkUpdates: true), file.Preferences);
        File.WriteAllText(path, "not json");
        Assert.Equal(new Preferences(), file.Preferences);
    }
}

public class VersionTests
{
    [Fact]
    public void Parses()
    {
        Assert.Equal(new ReleaseVersion(0, 15, 1), ReleaseVersion.FromCliOutput("varlatch 0.15.1 (migration 26)\n"));
        Assert.Equal(new ReleaseVersion(0, 15, 1), ReleaseVersion.FromCliOutput("varlatch 0.15.1 (migration 26)\r\n"));
        Assert.Null(ReleaseVersion.FromCliOutput("Usage: varlatch <command>"));
        Assert.Equal(new ReleaseVersion(0, 16, 0, "rc.1"), ReleaseVersion.Parse("v0.16.0-rc.1"));
        Assert.Null(ReleaseVersion.Parse("0.15"));
        Assert.Null(ReleaseVersion.Parse("0.15.x"));
        Assert.Equal("0.16.0-rc.1", new ReleaseVersion(0, 16, 0, "rc.1").ToString());
        Assert.Equal("0.15.1", new ReleaseVersion(0, 15, 1).ToString());
    }

    [Fact]
    public void Compares()
    {
        Assert.True(new ReleaseVersion(0, 9, 9) < new ReleaseVersion(0, 10, 0));
        Assert.True(new ReleaseVersion(0, 14, 0) >= ReleaseVersion.DeviceSignIn);
        Assert.True(new ReleaseVersion(0, 13, 9) < ReleaseVersion.DeviceSignIn);
        Assert.True(new ReleaseVersion(0, 16, 0, "rc.1") < new ReleaseVersion(0, 16, 0));
        Assert.True(new ReleaseVersion(1, 0, 0) > new ReleaseVersion(0, 99, 99));
        Assert.Equal(new ReleaseVersion(0, 15, 1), ReleaseVersion.Parse("0.15.1"));
        Assert.NotEqual(new ReleaseVersion(0, 16, 0), new ReleaseVersion(0, 16, 0, "rc.1"));
    }
}
