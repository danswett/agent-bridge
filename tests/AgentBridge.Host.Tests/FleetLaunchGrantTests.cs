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
    [InlineData("{}", 8, 6, false)]
    [InlineData("""{"fleetPerHour":2,"liveFleetWorkers":3}""", 2, 3, false)]
    [InlineData("""{"fleetPerHour":9,"liveFleetWorkers":0}""", 8, 6, true)]
    public void Fleet_limits_are_bounded_and_invalid_values_are_reported(string limits, int hourly, int live, bool invalid)
    {
        var settings = HostSettings.Parse("{\"agents\":{\"enabled\":true,\"limits\":" + limits + "}}", "fixture").Settings;
        var parsed = JsonSerializer.SerializeToElement(settings.Agents.Limits.Launch);
        Assert.True(parsed.TryGetProperty("FleetPerHour", out var hour));
        Assert.True(parsed.TryGetProperty("LiveFleetWorkers", out var workers));
        Assert.Equal(hourly, hour.GetInt32());
        Assert.Equal(live, workers.GetInt32());
        Assert.Equal(invalid, settings.AgentsProblems.Count > 0);
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

    [Trait("Category", "Process")]
    public sealed class FleetLaunchCapCoreTests : LaunchFlowFixture
    {
        private protected override bool UsesRelay => false;

        [Fact]
        public async Task Fleet_admission_limits_live_workers_and_hourly_launches_without_limiting_person_launches()
        {
            locator.Installed["claude"] = @"C:\fake\claude.exe";
            Git(f.Root, "init", "-q", "-b", "main", app);
            File.WriteAllText(Path.Combine(app, "README.md"), "fixture\n");
            Git(app, "add", ".");
            Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-qm", "fixture");
            var profile = Path.Combine(f.Root, "profile");
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, HostSettings.FileName),
                """{"agents":{"enabled":true,"fleet":{"workspaces":["App"]},"limits":{"fleetPerHour":8,"liveFleetWorkers":6}}}""");
            var launcher = Launcher(Config(c => c with { Workspaces = [new("App", app, true)] }),
                credentials: new AgentCredentials(profile, "machine"),
                agentWorktreeCheck: AgentLaunchWorktreeCheck.Check,
                agentWorktreeRefusalAudit: (_, _, _) => true);
            var service = new SessionService(source, new("host-id", machineName, "windows", "0.1.0"), launcher: launcher) { Time = clock };
            var audit = new AgentAuditLog(profile, clock);
            async Task<LaunchResult> Fleet()
            {
                var result = await service.LaunchFleetAsync(new LaunchRequest { Request = Guid.NewGuid().ToString("D"),
                    Agent = "claude", Workspace = "App", Permissions = LaunchProtocol.AllowAll },
                    "script:machine/dispatcher", audit, () => null, _ => null);
                await source.RefreshAsync();
                return result;
            }
            for (var i = 0; i < 6; i++) { Assert.Equal(LaunchOutcome.Started, (await Fleet()).Outcome); }
            var seventh = await Fleet();
            Assert.Equal(LaunchOutcome.Refused, seventh.Outcome);
            Assert.Contains("live fleet", seventh.Reason);
            foreach (var pid in starter.Pids.ToArray()) { starter.Exit(pid); }
            await source.RefreshAsync();
            for (var i = 0; i < 2; i++) { Assert.Equal(LaunchOutcome.Started, (await Fleet()).Outcome); }
            var ninth = await Fleet();
            Assert.Equal(LaunchOutcome.Refused, ninth.Outcome);
            Assert.Contains("hourly fleet", ninth.Reason);
            var coordinator = await GateLaunchAudit.LaunchAsync(launcher,
                new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Agent = "claude",
                    Workspace = "App", Permissions = LaunchProtocol.Ask },
                "person:local", audit, new("local", "person-client", "person:local", null));
            Assert.Equal(LaunchOutcome.Started, coordinator.Outcome);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(7, 0)]
    [InlineData(8, 0)]
    [InlineData(0, 5)]
    [InlineData(0, 6)]
    [InlineData(8, 6)]
    public async Task Fleet_activation_stays_closed_at_hourly_and_live_boundaries_under_concurrency_and_replay(int hourly, int live)
    {
        File.WriteAllText(Path.Combine(folder, HostSettings.FileName),
            """{"agents":{"enabled":true,"launch":{"enabled":true},"limits":{"launchesPerOriginPerHour":3,"launchesPerMachinePerHour":10,"liveAgentLaunched":6,"agentLaunchedIdleHours":2}}}""");
        var agent = new AgentLaunchGate(folder);
        for (var i = 0; i < 9; i++) { Assert.True(agent.CheckLaunch($"agent:{i}", Guid.NewGuid().ToString(), "hash").Allowed); }
        var launcher = new AdmissionLauncher(live);
        var audit = new AgentAuditLog(folder);
        var replay = Request;
        const string principal = "script:machine/dispatcher";
        for (var i = 0; i < hourly; i++)
        {
            replay = Request;
            Assert.Equal(LaunchOutcome.Started, (await GateLaunchAudit.LaunchAsync(launcher, replay, principal,
                audit, new("local", "agent", principal, null), fleet: true)).Outcome);
        }
        var sessions = new SessionService(null!, new("machine", "fixture", "windows", "1"), launcher: launcher);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<LaunchResult> Attempt(LaunchRequest request)
        {
            await ready.Task;
            return await sessions.LaunchFleetAsync(request, principal, audit, () => null, _ => null);
        }
        var attempts = new[] { Attempt(Request), Attempt(Request) };
        ready.SetResult();
        foreach (var result in await Task.WhenAll(attempts))
        {
            Assert.Equal(LaunchOutcome.Refused, result.Outcome);
            Assert.Contains("admission", result.Reason);
        }
        Assert.Equal(LaunchOutcome.Refused, (await Attempt(replay)).Outcome);
        Assert.Equal(LaunchOutcome.Refused, (await Attempt(replay)).Outcome);
        Assert.Equal(hourly, launcher.Dispatches);
        Assert.Equal(live, launcher.LaunchedProcesses().Count);
        Assert.True(agent.CheckLaunch("agent:last", Guid.NewGuid().ToString(), "hash").Allowed);
        Assert.False(agent.CheckLaunch("agent:overflow", Guid.NewGuid().ToString(), "hash").Allowed);
    }

    sealed class AdmissionLauncher(int live) : ISessionLauncher
    {
        public int Dispatches;
        public bool Available => true;
        public LaunchConfig FleetConfig => LaunchConfig.Default;
        public Task<LaunchOptions> OptionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LaunchResult> LaunchAsync(LaunchRequest request, string peer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IReadOnlyList<Started> LaunchedProcesses() => Enumerable.Range(1, live).Select(i => new Started(i, Now)).ToArray();
        public Task<LaunchResult> LaunchFleetAsync(LaunchRequest request, string principal, Func<Guid, Action<string>?> prepare, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Dispatches);
            var session = Guid.NewGuid().ToString();
            prepare(Guid.NewGuid())?.Invoke(session);
            return Task.FromResult(new LaunchResult(LaunchOutcome.Started) { Session = session });
        }
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
