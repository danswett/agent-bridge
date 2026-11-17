using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

[Trait("Category", "Process")]
public sealed class FleetLaunchRetirementTests : LaunchFlowFixture
{
    private protected override bool UsesRelay => false;
    string Profile => Path.Combine(f.Root, "fleet-retirement");
    readonly ReservationLauncher launcher = new();
    readonly Channel channel = new();
    static LaunchRequest Request => new() { Request = Guid.NewGuid().ToString("D"), Agent = "claude",
        Workspace = "App", Permissions = LaunchProtocol.AllowAll };
    SessionService Service() => new(source, new("machine", "fixture", "windows", "1"), launcher: launcher) { Time = clock };
    Func<IProtocolChannel, string, JsonElement, Task<object?>> Handler(CallerKind caller = CallerKind.HostCli,
        AgentCredentials? credentials = null) => LocalApi.Handler(Service(), null!, new FixedCallerGuard(new(caller, "fixture")),
            credentials: credentials, audit: new AgentAuditLog(Profile, clock));
    async Task<JsonElement> Call(string op, object data) =>
        JsonSerializer.SerializeToElement(await Handler()(channel, op, JsonSerializer.SerializeToElement(data)));
    async Task<LaunchRequest> Reserve()
    {
        Directory.CreateDirectory(Profile);
        File.WriteAllText(Path.Combine(Profile, HostSettings.FileName),
            """{"agents":{"enabled":true,"limits":{"liveFleetWorkers":1}}}""");
        var request = Request;
        Assert.Equal(LaunchOutcome.Unconfirmed, (await Service().LaunchFleetAsync(request, "script:machine/dispatcher",
            new AgentAuditLog(Profile, clock), () => null, _ => null)).Outcome);
        return request;
    }

    [Fact]
    public async Task Owner_retirement_survives_restart_releases_only_the_live_slot_and_preserves_replay()
    {
        var request = await Reserve();
        var before = await Call("fleet.status", new { });
        Assert.Equal(8, before.GetProperty("FleetPerHour").GetInt32());
        Assert.Equal(1, before.GetProperty("LiveFleetWorkers").GetInt32());
        Assert.Equal(1, before.GetProperty("Hourly").GetInt32());
        Assert.Equal(1, before.GetProperty("Live").GetInt32());
        Assert.Equal(request.Request, Assert.Single(before.GetProperty("Reserved").EnumerateArray()).GetString());
        Assert.Equal(LaunchOutcome.Refused, (await Service().LaunchFleetAsync(Request, "script:machine/dispatcher",
            new AgentAuditLog(Profile, clock), () => null, _ => null)).Outcome);
        await Call("fleet.retire", new { request = request.Request });
        await Call("fleet.retire", new { request = request.Request });
        var after = await Call("fleet.status", new { });
        Assert.Equal(1, after.GetProperty("Hourly").GetInt32());
        Assert.Equal(0, after.GetProperty("Live").GetInt32());
        Assert.Empty(after.GetProperty("Reserved").EnumerateArray());
        Assert.Equal(LaunchOutcome.Unconfirmed, (await Service().LaunchFleetAsync(request, "script:machine/dispatcher",
            new AgentAuditLog(Profile, clock), () => null, _ => null)).Outcome);
        Assert.Equal(1, launcher.Dispatches);
        var (rows, complete) = GateAuditProjection.Load(Profile);
        Assert.True(complete);
        var retirement = Assert.Single(rows, row => row.Action == "fleet.retire");
        Assert.Equal("person:local", retirement.Principal);
        Assert.Equal(request.Request, retirement.Request);
        Assert.Contains("launches-", Assert.Single(Directory.GetFiles(Path.Combine(Profile, "audit"), "launches-*.jsonl")));
        Assert.Equal(LaunchOutcome.Unconfirmed, (await Service().LaunchFleetAsync(Request, "script:machine/dispatcher",
            new AgentAuditLog(Profile, clock), () => null, _ => null)).Outcome);
        Assert.Equal(2, (await Call("fleet.status", new { })).GetProperty("Hourly").GetInt32());
    }

    [Theory]
    [InlineData(CallerKind.Agent, false)]
    [InlineData(CallerKind.App, false)]
    [InlineData(CallerKind.Other, false)]
    [InlineData(CallerKind.HostCli, true)]
    public async Task Only_an_uncredentialed_fresh_host_cli_caller_can_read_or_retire(CallerKind caller, bool script)
    {
        var request = await Reserve();
        var credentials = new AgentCredentials(Profile, "machine");
        if (script) { var (credential, _) = credentials.MintScript("dispatcher"); credentials.Accept(channel, credential); }
        foreach (var op in new[] { "fleet.status", "fleet.retire" })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Handler(caller, credentials)(channel, op, JsonSerializer.SerializeToElement(new { request = request.Request })));
            Assert.Contains(script ? "script" : "only", error.Message);
        }
        Assert.DoesNotContain(GateAuditProjection.Load(Profile).Rows, row => row.Action == "fleet.retire");
    }

    [Fact]
    public async Task Unknown_settled_and_malformed_requests_cannot_be_retired_and_damaged_history_fails_closed()
    {
        var request = await Reserve();
        foreach (var id in new[] { "not-a-uuid", Guid.NewGuid().ToString("D") })
        { await Assert.ThrowsAnyAsync<ArgumentException>(() => Call("fleet.retire", new { request = id })); }
        launcher.Registered!(Guid.NewGuid().ToString("D"));
        var settled = await Assert.ThrowsAsync<InvalidOperationException>(() => Call("fleet.retire", new { request = request.Request }));
        Assert.Contains("unresolved", settled.Message);
        File.AppendAllText(Assert.Single(Directory.GetFiles(Path.Combine(Profile, "audit"), "launches-*.jsonl")), "broken\n");
        foreach (var op in new[] { "fleet.status", "fleet.retire" })
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Call(op, new { request = request.Request }));
            Assert.Contains("history", error.Message);
        }
    }

    sealed class ReservationLauncher : ISessionLauncher
    {
        public int Dispatches;
        public Action<string>? Registered;
        public bool Available => true;
        public LaunchConfig FleetConfig => LaunchConfig.Default;
        public Task<LaunchOptions> OptionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LaunchResult> LaunchAsync(LaunchRequest request, string peer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LaunchResult> LaunchFleetAsync(LaunchRequest request, string principal, Func<Guid, Action<string>?> prepare,
            Action<string>? terminal = null, CancellationToken cancellationToken = default)
        {
            Registered = prepare(Guid.NewGuid());
            if (Registered is null) { return Task.FromResult(new LaunchResult(LaunchOutcome.Refused)); }
            Dispatches++;
            return Task.FromResult(new LaunchResult(LaunchOutcome.Unconfirmed));
        }
    }
    sealed class Channel : IProtocolChannel
    {
        public string PeerId => "fixture";
        public string PeerName => "fixture";
        public string PeerKind => "fixture";
        public bool IsOpen => true;
        public Task Closed => Task.CompletedTask;
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
