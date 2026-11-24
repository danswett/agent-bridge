using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core.Restore;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

// #2777: a person's launch that ticks "May start agents" gives the launched session the agent launch scope, only while the machine's agent launch
// switch is on; it is recorded in the launch record and on the launch audit rows; nothing changes for a launch without the tick.
public sealed partial class LaunchFlowTests
{
    string SavedScope(string profile)
    {
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile, AgentCredentials.FileName)));
        return Assert.Single(saved.RootElement.GetProperty("records").EnumerateArray()).GetProperty("credential").GetProperty("scope").GetString()!;
    }

    [Theory]
    [InlineData(true, true, "read+reply+launch")]    // ticked, switch on: the agent launch scope
    [InlineData(false, true, "read")]                // no tick: read-only as before
    public async Task A_person_launch_gives_the_session_the_agent_launch_scope_only_when_ticked_and_the_switch_is_on(bool tick, bool switchOn, string scope)
    {
        var profile = Directory.CreateDirectory(Path.Combine(f.Root, "scope-profile")).FullName;
        var launcher = Launcher(Config(), credentials: new AgentCredentials(profile, "machine"), agentLaunchOn: switchOn);
        host.Hub.Handler = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0"), launcher: launcher).HandleAsync;
        var o = await LaunchAsync(await OpenAsync(), tick ? new { agent = "copilot", workspace = "API", prompt = "go", agentScope = "read+reply+launch" }
            : new { agent = "copilot", workspace = "API", prompt = "go" });
        Assert.Equal("started", o.Outcome_);
        Assert.Equal(scope, SavedScope(profile));
        Assert.Equal(tick ? "read+reply+launch" : null, launcher.LaunchAgentScope(o.Session!));
    }

    [Fact]
    public async Task A_launch_that_asks_for_the_scope_is_refused_when_the_machine_switch_is_off_even_if_sent()
    {
        var launcher = Launcher(Config(), credentials: new AgentCredentials(Directory.CreateDirectory(Path.Combine(f.Root, "scope-off")).FullName, "machine"), agentLaunchOn: false);
        var result = await launcher.LaunchAsync(new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Agent = "copilot", Workspace = "API", Prompt = "go",
            AgentScope = AgentProtocol.ScopeReadReplyLaunch }, "app");
        Assert.Equal(LaunchOutcome.Refused, result.Outcome);
        Assert.Contains("agents.launch.enabled", result.Reason);
        Assert.Empty(starter.Specs);
    }

    [Theory]
    [InlineData("agent:machine/55555555-5555-4555-8555-555555555555", false)]
    [InlineData("script:machine/dispatcher", true)]
    public async Task A_launch_by_an_agent_or_a_script_never_asks_for_the_scope(string principal, bool fleet)
    {
        var launcher = Launcher(Config(), agentLaunchOn: true);
        var request = new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Agent = "copilot", Workspace = "API", Prompt = "go", AgentScope = AgentProtocol.ScopeReadReplyLaunch };
        var result = fleet ? await launcher.LaunchFleetAsync(request, principal, _ => null) : await launcher.LaunchAsync(request, principal);
        Assert.Equal(LaunchOutcome.Refused, result.Outcome);
        Assert.Contains("agent scope", result.Reason);
        Assert.Empty(starter.Specs);
    }

    // The point of the tick: a session a person launched with it, wearing the credential the launch really minted, can fleet.resume.
    [Theory]
    [InlineData(true, "resumed", null)]
    [InlineData(false, "refused", "policy")]   // without the tick the session is read-only: refused as a caller without the launch scope (policy)
    public async Task A_session_launched_with_the_tick_can_fleet_resume_and_one_without_it_is_refused(bool tick, string outcome, string? reason)
    {
        var folder = Directory.CreateDirectory(Path.Combine(f.Root, "tick-resume")).FullName;
        File.WriteAllText(Path.Combine(folder, HostSettings.FileName), JsonSerializer.Serialize(new
        {
            agents = new { enabled = true, resume = new { roles = new[] { "quality-lead" } } }, launch = new { enabled = true, workspaces = Array.Empty<string>() },
        }));
        var credentials = new AgentCredentials(folder, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", rootAlive: (_, _) => true);
        var launcher = Launcher(Config(), credentials: credentials, agentLaunchOn: true);
        var result = await launcher.LaunchAsync(new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Agent = "copilot", Workspace = "API", Prompt = "go",
            AgentScope = tick ? AgentProtocol.ScopeReadReplyLaunch : null, CoordinatorRole = "quality-lead" }, "app");
        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        var spec = Assert.Single(starter.Specs);
        var token = spec.Environment![Launchers.TokenVariable]!;
        var caller = credentials.Check(token, new([new PeerProcess(starter.Pids[0], DateTimeOffset.UtcNow, "agent.exe")], true)).Accepted;
        Assert.NotNull(caller);
        Assert.Equal(tick ? AgentProtocol.ScopeReadReplyLaunch : AgentProtocol.ScopeRead, caller!.Scope);
        Assert.Equal(("quality-lead", "session", 0, true), (launcher.LaunchRole(caller.Session), caller.Kind, caller.Depth, credentials.IsLive(caller)));
        var resumed = new List<ResumeAsk>();
        var handler = new FleetResumeCommands(folder, credentials, launcher.LaunchRole, _ => true, _ => false,
            _ => new RestoreLaunch("copilot", "API", null, null, null, null, null, LaunchProtocol.Ask),
            (ask, _) => { resumed.Add(ask); return Task.FromResult(new LaunchResult(LaunchOutcome.Started, null) { Session = ask.Id }); });
        var receipt = await handler.HandleAsync(caller, JsonSerializer.SerializeToElement(new { version = 1, request = Guid.NewGuid(), session = Guid.NewGuid().ToString() }));
        Assert.Equal((outcome, reason), (receipt.Outcome, receipt.Reason));
        Assert.Equal(tick ? 1 : 0, resumed.Count);
    }

    sealed class NoCredentials : AgentBridge.Host.Delivery.ILaunchCredentials
    {
        public string? Prepare(string session, string launchId) => null;
        public void Bind(string launchId, int processId, DateTimeOffset startedAt) { }
        public void Abandon(string launchId) { }
    }

    // The record never says a scope was given when no credential was issued: the launch is refused, nothing starts.
    [Fact]
    public async Task A_scope_launch_with_no_credential_issued_is_refused_and_records_nothing()
    {
        var launcher = Launcher(Config(), credentials: new NoCredentials(), agentLaunchOn: true);
        var result = await launcher.LaunchAsync(new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Agent = "copilot", Workspace = "API", Prompt = "go",
            AgentScope = AgentProtocol.ScopeReadReplyLaunch }, "app");
        Assert.Equal(LaunchOutcome.Refused, result.Outcome);
        Assert.Contains("no credential was issued", result.Reason);
        Assert.Empty(starter.Specs);
    }

    [Fact]
    public async Task launch_options_offer_the_tick_only_when_the_machine_switch_is_on()
    {
        Assert.True((await Launcher(Config(), agentLaunchOn: true).OptionsAsync()).AgentLaunch);
        Assert.Null((await Launcher(Config(), agentLaunchOn: false).OptionsAsync()).AgentLaunch);
        Assert.Null((await Launcher(Config()).OptionsAsync()).AgentLaunch);
    }

    [Fact]
    public async Task The_scope_is_on_both_audit_rows_of_a_persons_launch_and_not_on_one_without_the_tick()
    {
        var profile = Path.Combine(f.Root, "audit-scope", "default");
        var launcher = Launcher(Config(), agentLaunchOn: true);
        var ingress = new GateLaunchIngress("local", "person-client", "person:local", null);
        await GateLaunchAudit.LaunchAsync(launcher, new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Agent = "copilot", Workspace = "API", Prompt = "go",
            AgentScope = AgentProtocol.ScopeReadReplyLaunch }, "local", new AgentAuditLog(profile), ingress);
        await GateLaunchAudit.LaunchAsync(launcher, new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Agent = "copilot", Workspace = "API", Prompt = "again" },
            "local", new AgentAuditLog(profile), ingress);
        var (rows, complete) = GateAuditProjection.Load(profile);
        Assert.True(complete);
        Assert.Equal(new string?[] { "read+reply+launch", "read+reply+launch", null, null },
            rows.Where(r => r.Action == "session.launch" && r.Gate is not null).Select(r => r.Gate!.AgentScope).ToArray());
        var id = Guid.NewGuid();
        var person = new GateAuditMetadata(1, id, Guid.Empty, 1, "intent", "local", "person-client", id, "none", AgentScope: "read+reply+launch");
        Assert.True(person.Valid("session.launch", id.ToString(), "reserved"));
        Assert.False((person with { ActorKind = "agent" }).Valid("session.launch", id.ToString(), "reserved"));
        Assert.False((person with { AgentScope = "read" }).Valid("session.launch", id.ToString(), "reserved"));
        Assert.False(person.Valid("session.stop", id.ToString(), "reserved"));
    }
}
