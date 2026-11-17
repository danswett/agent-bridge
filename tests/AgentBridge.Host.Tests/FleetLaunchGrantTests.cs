using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Cli;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class FleetLaunchGrantTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("ab-fleet-grant-").FullName;
    static readonly DateTimeOffset Now = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
    static readonly ProcessAncestry Outside = new([new PeerProcess(123, Now, "pwsh.exe")], true);
    static AgentCredential Script => AgentCredential.Script("fixture", "machine", "dispatcher", Now) with { FleetLaunch = true };
    static LaunchRequest Request => new() { Request = Guid.NewGuid().ToString("D"), Agent = "claude",
        Workspace = "workers", Permissions = LaunchProtocol.AllowAll };
    void Config(string workspace = "workers", bool isolate = true) => File.WriteAllText(Path.Combine(folder, HostSettings.FileName),
        JsonSerializer.Serialize(new { agents = new { enabled = true, fleet = new { workspaces = new[] { workspace } } },
            launch = new { workspaces = new[] { new { label = "workers", path = folder, isolate } } } }));
    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void allowAll_is_allowed_only_in_a_ticked_isolated_workspace()
    {
        var grant = new FleetLaunchGrant(folder);
        Config();
        Assert.Null(grant.CheckLaunch(Script, Outside, Request));
        Config("elsewhere");
        Assert.Contains("ticked", grant.CheckLaunch(Script, Outside, Request));
        Config(isolate: false);
        Assert.Contains("isolated", grant.CheckLaunch(Script, Outside, Request));
        Config();
        Assert.Contains("listed", grant.CheckLaunch(Script, Outside, Request with { Workspace = "unknown" }));
    }

    [Fact]
    public void A_launch_is_refused_when_the_fleet_grant_is_off_with_no_fallback()
    {
        Config();
        var grant = new FleetLaunchGrant(folder);
        Assert.Contains("off", grant.CheckGrant(Script with { FleetLaunch = false }));
        Assert.Contains("off", grant.CheckLaunch(Script with { FleetLaunch = false }, Outside, Request));
        File.WriteAllText(Path.Combine(folder, HostSettings.FileName), """{"agents":{"enabled":true,"fleet":{"workspaces":true}}}""");
        Assert.NotNull(grant.CheckLaunch(Script, Outside, Request));
    }

    [Fact]
    public void Partial_agent_ancestry_wider_scope_and_unsafe_request_shapes_are_refused()
    {
        Config();
        var grant = new FleetLaunchGrant(folder);
        Assert.NotNull(grant.CheckLaunch(Script, Outside with { Complete = false }, Request));
        Assert.NotNull(grant.CheckLaunch(Script, new([new PeerProcess(123, Now, "copilot.exe")], true), Request));
        Assert.NotNull(grant.CheckLaunch(Script with { Scope = AgentProtocol.ScopeReadReplyLaunch }, Outside, Request));
        foreach (var request in new[] { Request with { Resume = "existing" }, Request with { Profile = "override" },
            Request with { Attachments = [new AttachmentRef { Id = Guid.NewGuid().ToString("D") }] } })
        { Assert.NotNull(grant.CheckLaunch(Script, Outside, request)); }
    }

    [Fact]
    public void Only_the_owner_grants_fleet_and_workers_receive_reply_but_never_launch_scope()
    {
        Config();
        var store = new AgentCredentials(folder, "machine");
        var (script, _) = store.MintScript("dispatcher");
        var channel = new Channel();
        var data = JsonSerializer.SerializeToElement(new { name = "dispatcher" });
        var owner = new FixedCallerGuard(new(CallerKind.HostCli, "fixture owner"));
        Assert.Throws<InvalidOperationException>(() => ScriptCredentialCommands.Handle(channel, "scripts.grant-fleet", data,
            new FixedCallerGuard(new(CallerKind.Agent, "fixture agent")), store, new AgentAuditLog(folder)));
        ScriptCredentialCommands.Handle(channel, "scripts.grant-fleet", data, owner, store, new AgentAuditLog(folder));
        store.Accept(channel, script);
        Assert.True(store.Of(channel)!.FleetLaunch);
        var restarted = new AgentCredentials(folder, "machine");
        restarted.Accept(channel, script);
        Assert.True(restarted.Of(channel)!.FleetLaunch);
        var token = store.PrepareFleet("worker", "launch");
        var worker = store.Check(token, new([new PeerProcess(124, Now, "claude.exe")], true));
        Assert.Null(worker.Accepted); // Unbound tokens must not authenticate.
        store.Bind("launch", 124, Now);
        var accepted = Assert.IsType<AgentCredential>(store.Check(token, new([new PeerProcess(124, Now, "claude.exe")], true)).Accepted);
        Assert.Equal(AgentProtocol.ScopeReadReply, accepted.Scope);
        Assert.Equal(1, accepted.Depth);
        Assert.False(accepted.FleetLaunch);
        ScriptCredentialCommands.Handle(new Channel(), "scripts.revoke-fleet", data, owner, store, new AgentAuditLog(folder));
        Assert.False(store.Of(channel)!.FleetLaunch);
    }

    [Fact]
    public void The_active_dispatch_workspace_must_match_current_approval()
    {
        Config();
        var grant = new FleetLaunchGrant(folder);
        var actual = HostSettings.Load(folder).Settings.Launch;
        Assert.Null(grant.CheckDispatch(actual, Request));
        Assert.NotNull(grant.CheckDispatch(actual with { Workspaces = [new("workers", Path.GetTempPath(), true)] }, Request));
    }

    [Theory]
    [InlineData(true, "bind")]
    [InlineData(false, "bind")]
    [InlineData(true, "abandon")]
    [InlineData(false, "abandon")]
    [InlineData(true, "revoke")]
    [InlineData(false, "revoke")]
    public void A_failed_grant_save_cannot_be_persisted_by_later_mutations_after_storage_recovers(bool enabled, string later)
    {
        Config();
        var store = new AgentCredentials(folder, "machine");
        var (script, _) = store.MintScript("dispatcher");
        if (!enabled) { store.SetFleetLaunch("dispatcher", true); }
        store.Prepare("pending", "launch");
        var channel = new Channel();
        store.Accept(channel, script);
        var blocked = Path.Combine(folder, AgentCredentials.FileName) + ".tmp";
        Directory.CreateDirectory(blocked);
        Assert.NotNull(Record.Exception(() => store.SetFleetLaunch("dispatcher", enabled)));
        Directory.Delete(blocked);
        Assert.NotNull(store.Problem);
        Assert.Null(store.Of(channel));
        Assert.Throws<InvalidOperationException>(() => store.SetFleetLaunch("dispatcher", enabled));
        if (later == "bind") { store.Bind("launch", 123, Now); }
        if (later == "abandon") { store.Abandon("launch"); }
        if (later == "revoke") { store.Revoke("pending", "fixture"); }
        var reopened = new AgentCredentials(folder, "machine");
        reopened.Accept(channel, script);
        Assert.Equal(!enabled, reopened.Of(channel)!.FleetLaunch);
    }

    [Fact]
    public void Fleet_limits_default_to_8_and_6_and_cannot_be_raised_or_disabled()
    {
        var defaults = HostSettings.Parse("""{"agents":{"enabled":true,"limits":{}}}""", "fixture").Settings;
        Assert.Equal((8, 6), (defaults.Agents.Limits.Launch.FleetPerHour, defaults.Agents.Limits.Launch.LiveFleetWorkers));

        var lowered = HostSettings.Parse("""{"agents":{"enabled":true,"limits":{"fleetPerHour":2,"liveFleetWorkers":3}}}""", "fixture").Settings;
        Assert.Equal((2, 3), (lowered.Agents.Limits.Launch.FleetPerHour, lowered.Agents.Limits.Launch.LiveFleetWorkers));

        var raised = HostSettings.Parse("""{"agents":{"enabled":true,"limits":{"fleetPerHour":9,"liveFleetWorkers":7}}}""", "fixture").Settings;
        Assert.Equal((8, 6), (raised.Agents.Limits.Launch.FleetPerHour, raised.Agents.Limits.Launch.LiveFleetWorkers));
        Assert.Contains(raised.AgentsProblems, problem => problem.Contains("fleetPerHour"));
        Assert.Contains(raised.AgentsProblems, problem => problem.Contains("liveFleetWorkers"));

        var disabled = HostSettings.Parse("""{"agents":{"enabled":true,"limits":{"fleetPerHour":0,"liveFleetWorkers":0}}}""", "fixture").Settings;
        Assert.Equal((8, 6), (disabled.Agents.Limits.Launch.FleetPerHour, disabled.Agents.Limits.Launch.LiveFleetWorkers));
        Assert.Contains(disabled.AgentsProblems, problem => problem.Contains("fleetPerHour"));
        Assert.Contains(disabled.AgentsProblems, problem => problem.Contains("liveFleetWorkers"));
    }

    [Fact]
    public async Task A_granted_script_still_refuses_scope_overrides_and_an_untraceable_pipe()
    {
        Config();
        var store = new AgentCredentials(folder, "machine");
        var (script, _) = store.MintScript("dispatcher");
        var owner = new FixedCallerGuard(new(CallerKind.HostCli, "fixture owner"));
        ScriptCredentialCommands.Handle(new Channel(), "scripts.grant-fleet",
            JsonSerializer.SerializeToElement(new { name = "dispatcher" }), owner, store, new AgentAuditLog(folder));
        var channel = new Channel();
        store.Accept(channel, script);
        var handler = LocalApi.Handler(null!, null!, owner, credentials: store);
        var shape = await Assert.ThrowsAsync<InvalidOperationException>(() => handler(channel, LaunchProtocol.Launch,
            JsonSerializer.SerializeToElement(new { request = Request.Request, agentScope = "read+reply+launch" })));
        Assert.Contains("unsupported fields", shape.Message);
        var ancestry = await Assert.ThrowsAsync<InvalidOperationException>(() => handler(channel, LaunchProtocol.Launch,
            JsonSerializer.SerializeToElement(Request, ProtocolJson.Options)));
        Assert.Contains("ancestry", ancestry.Message);
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

[Trait("Category", "Process")]
public sealed class FleetLaunchCapacityTests : LaunchFlowFixture
{
    private protected override bool UsesRelay => false;

    [Fact]
    public async Task Concurrent_admission_reserves_hourly_slots_before_registration_and_replay_does_not_consume_another()
    {
        var (service, audit) = Setup(wait: TimeSpan.FromSeconds(1), follow: TimeSpan.FromSeconds(2));
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(LaunchOutcome.Started, (await FleetLaunch(service, audit)).Outcome);
        }
        foreach (var pid in starter.Pids.ToArray()) { starter.Exit(pid); }
        await source.RefreshAsync();
        Assert.Equal(LaunchOutcome.Started, (await FleetLaunch(service, audit)).Outcome);
        foreach (var pid in starter.Pids.ToArray()) { starter.Exit(pid); }
        await source.RefreshAsync();

        starter.Registers = false;
        var burst = Enumerable.Range(0, 6).Select(_ => FleetRequest()).ToArray();
        var results = await Task.WhenAll(burst.Select(async request =>
            (Request: request, Result: await FleetLaunch(service, audit, request, refresh: false))));
        var admitted = Assert.Single(results, item => item.Result.Outcome == LaunchOutcome.Unconfirmed);
        Assert.Equal(5, results.Count(item => item.Result.Outcome == LaunchOutcome.Refused));
        Assert.All(results.Where(item => item.Result.Outcome == LaunchOutcome.Refused),
            item => Assert.Contains("hourly fleet", item.Result.Reason));
        Assert.Equal(8, starter.Specs.Count);

        Assert.Equal(LaunchOutcome.Unconfirmed, (await FleetLaunch(service, audit, admitted.Request, refresh: false)).Outcome);
        Assert.Equal(8, starter.Specs.Count);
        var (rows, complete) = GateAuditProjection.Load(audit.Profile);
        Assert.True(complete);
        Assert.Equal(8, rows.Count(row => row.Grant == "fleet" && row.Action == "session.launch"
            && row.Gate is { Phase: "intent" } && row.Outcome == "reserved"));
    }

    [Fact]
    public async Task Failed_fleet_starts_close_live_reservations_but_still_consume_hourly_admissions()
    {
        var (service, audit) = Setup();
        starter.Throws = new IOException("start failed");
        var failed = Enumerable.Range(0, 6).Select(_ => FleetRequest()).ToArray();
        foreach (var request in failed)
        {
            Assert.Equal(LaunchOutcome.Failed, (await FleetLaunch(service, audit, request)).Outcome);
        }

        var (rows, complete) = GateAuditProjection.Load(audit.Profile);
        Assert.True(complete);
        Assert.All(failed, request => Assert.Contains(rows, row => row.Request == request.Request
            && row.Gate is { Phase: "terminal", ReasonCode: "failed" } && row.Outcome == "failed"));

        starter.Throws = null;
        Assert.Equal(LaunchOutcome.Started, (await FleetLaunch(service, audit)).Outcome);
    }

    [Fact]
    public async Task A_fleet_launch_without_a_credential_closes_its_live_reservation()
    {
        var credentials = new NoFleetCredentials();
        var (service, audit) = Setup(credentials: credentials);
        var request = FleetRequest();
        Assert.Equal(LaunchOutcome.Refused, (await FleetLaunch(service, audit, request)).Outcome);

        var (rows, complete) = GateAuditProjection.Load(audit.Profile);
        Assert.True(complete);
        Assert.Contains(rows, row => row.Request == request.Request
            && row.Gate is { Phase: "terminal", ReasonCode: "refused" } && row.Outcome == "refused");
        Assert.Empty(starter.Specs);
        credentials.IssueFleet = true;
        Assert.Equal(LaunchOutcome.Started, (await FleetLaunch(service, audit)).Outcome);
    }

    [Fact]
    public async Task An_unregistered_launcher_exit_without_child_proof_stays_fail_closed()
    {
        var (service, audit) = Setup(wait: TimeSpan.FromSeconds(2), follow: TimeSpan.FromSeconds(3));
        starter.Registers = false;
        starter.ExitsAtOnce = true;
        var request = FleetRequest();
        Assert.Equal(LaunchOutcome.Failed, (await FleetLaunch(service, audit, request)).Outcome);

        var (rows, complete) = GateAuditProjection.Load(audit.Profile);
        Assert.True(complete);
        Assert.DoesNotContain(rows, row => row.Request == request.Request && row.Gate?.Phase == "terminal");
        starter.Registers = true;
        starter.ExitsAtOnce = false;
        var refused = await FleetLaunch(service, audit);
        Assert.Equal(LaunchOutcome.Refused, refused.Outcome);
        Assert.Contains("cannot be checked safely", refused.Reason);
        Assert.Single(starter.Specs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Restart_reconciles_only_a_fleet_reservation_with_durable_exit_proof(bool processWasBound)
    {
        var (_, audit) = Setup();
        var request = FleetRequest();
        var requestId = Guid.Parse(request.Request);
        var operation = Guid.NewGuid();
        var gate = new GateAuditMetadata(1, operation, Guid.Empty, 0, "intent", "local", "agent", operation,
            "none", LaunchBinding: new string('a', 64));
        Assert.True(audit.Append(new AgentAuditEntry("local", request.Request, "script:machine/dispatcher", [],
            "session.launch", request.Request, "reserved", Gate: gate) { Grant = "fleet" }));
        var provenance = new LaunchProvenance(Path.Combine(audit.Profile, "launch-provenance.json"), logged.Enqueue);
        provenance.Preparing(requestId, request.Request, "fleet");
        if (processWasBound) { provenance.Bound(requestId, request.Request, new(999999, DateTimeOffset.UtcNow)); }

        var restarted = CreateService(audit);
        var next = FleetRequest();
        var result = await FleetLaunch(restarted, audit, next);
        if (processWasBound)
        {
            Assert.Equal(LaunchOutcome.Started, result.Outcome);
            var (rows, complete) = GateAuditProjection.Load(audit.Profile);
            Assert.True(complete);
            Assert.Contains(rows, row => row.Request == request.Request
                && row.Gate is { Phase: "terminal", ReasonCode: "failed" } && row.Outcome == "failed");
        }
        else
        {
            Assert.Equal(LaunchOutcome.Refused, result.Outcome);
            Assert.Contains("cannot be checked safely", result.Reason);
            var (rows, complete) = GateAuditProjection.Load(audit.Profile);
            Assert.True(complete);
            Assert.DoesNotContain(rows, row => row.Request == request.Request && row.Gate?.Phase == "terminal");
            Assert.Empty(starter.Specs);
        }
    }

    [Fact]
    public async Task A_script_credential_cannot_exceed_the_fleet_cap_of_8_an_hour_nor_6_live_workers()
    {
        var (service, audit) = Setup();
        for (var i = 0; i < 6; i++)
        {
            var launch = await FleetLaunch(service, audit);
            Assert.True(launch.Outcome == LaunchOutcome.Started, $"{launch.Outcome}: {launch.Reason}");
        }
        var seventhWorker = await FleetLaunch(service, audit);
        Assert.Equal(LaunchOutcome.Refused, seventhWorker.Outcome);
        Assert.Contains("live fleet worker", seventhWorker.Reason);

        foreach (var pid in starter.Pids.ToArray()) { starter.Exit(pid); }
        await source.RefreshAsync();
        for (var i = 0; i < 2; i++)
        {
            var launch = await FleetLaunch(service, audit);
            Assert.True(launch.Outcome == LaunchOutcome.Started, $"{launch.Outcome}: {launch.Reason}");
        }
        var ninthLaunch = await FleetLaunch(service, audit);
        Assert.Equal(LaunchOutcome.Refused, ninthLaunch.Outcome);
        Assert.Contains("hourly fleet", ninthLaunch.Reason);
    }

    [Fact]
    public async Task A_coordinator_can_launch_while_the_fleet_cap_is_full()
    {
        var (service, audit) = Setup();
        for (var i = 0; i < 6; i++)
        {
            var launch = await FleetLaunch(service, audit);
            Assert.True(launch.Outcome == LaunchOutcome.Started, $"{launch.Outcome}: {launch.Reason}");
        }
        foreach (var pid in starter.Pids.ToArray()) { starter.Exit(pid); }
        await source.RefreshAsync();
        for (var i = 0; i < 2; i++)
        {
            var launch = await FleetLaunch(service, audit);
            Assert.True(launch.Outcome == LaunchOutcome.Started, $"{launch.Outcome}: {launch.Reason}");
        }

        var launcher = Launcher(Config(c => c with { Workspaces = [new("App", app, true)] }),
            credentials: new AgentCredentials(Path.Combine(f.Root, "profile"), "machine"),
            agentWorktreeCheck: AgentLaunchWorktreeCheck.Check,
            agentWorktreeRefusalAudit: (_, _, _) => true);
        var coordinator = await GateLaunchAudit.LaunchAsync(launcher,
            new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Agent = "claude",
                Workspace = "App", Permissions = LaunchProtocol.Ask },
            "person:local", audit, new("local", "person-client", "person:local", null));

        Assert.Equal(LaunchOutcome.Started, coordinator.Outcome);
    }

    (SessionService Service, AgentAuditLog Audit) Setup(TimeSpan? wait = null, TimeSpan? follow = null,
        AgentBridge.Host.Delivery.ILaunchCredentials? credentials = null)
    {
        locator.Installed["claude"] = @"C:\fake\claude.exe";
        Git(f.Root, "init", "-q", "-b", "main", app);
        File.WriteAllText(Path.Combine(app, "README.md"), "fixture\n");
        Git(app, "add", ".");
        Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-qm", "fixture");
        var profile = Path.Combine(f.Root, "profile");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, HostSettings.FileName),
            JsonSerializer.Serialize(new { agents = new { enabled = true, fleet = new { workspaces = new[] { "App" } },
                limits = new { fleetPerHour = 8, liveFleetWorkers = 6 } } }));
        var audit = new AgentAuditLog(profile, clock);
        return (CreateService(audit, wait, follow, credentials), audit);
    }

    SessionService CreateService(AgentAuditLog audit, TimeSpan? wait = null, TimeSpan? follow = null,
        AgentBridge.Host.Delivery.ILaunchCredentials? credentials = null)
    {
        var launcher = Launcher(Config(c => c with { Workspaces = [new("App", app, true)] }), wait: wait, follow: follow,
            credentials: credentials ?? new AgentCredentials(audit.Profile, "machine"),
            agentWorktreeCheck: AgentLaunchWorktreeCheck.Check,
            agentWorktreeRefusalAudit: (_, _, _) => true);
        return new SessionService(source, new MachineInfo("host-id", machineName, "windows", "0.1.0"), launcher: launcher);
    }

    LaunchRequest FleetRequest() => new() { Request = Guid.NewGuid().ToString("D"), Agent = "claude",
        Workspace = "App", Permissions = LaunchProtocol.AllowAll };

    sealed class NoFleetCredentials : AgentBridge.Host.Delivery.ILaunchCredentials
    {
        public bool IssueFleet;
        public string? Prepare(string session, string launchId) => null;
        public string? PrepareFleet(string session, string launchId) => IssueFleet ? "fixture" : null;
        public void Bind(string launchId, int processId, DateTimeOffset startedAt) { }
        public void Abandon(string launchId) { }
    }

    async Task<LaunchResult> FleetLaunch(SessionService service, AgentAuditLog audit, LaunchRequest? request = null, bool refresh = true)
    {
        var result = await service.LaunchFleetAsync(request ?? FleetRequest(),
            "script:machine/dispatcher", audit, () => null, _ => null);
        if (refresh) { await source.RefreshAsync(); }
        return result;
    }
}
