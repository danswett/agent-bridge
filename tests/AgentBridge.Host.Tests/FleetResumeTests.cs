using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Restore;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

// #4318: a coordinator resumes a fleet worker in place; every other caller and every other session is refused before anything starts.
public sealed class FleetResumeTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("ab-fleet-resume-").FullName;
    readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    readonly string target = Guid.NewGuid().ToString();
    readonly AgentCredentials credentials;
    AgentCredential caller;
    readonly List<ResumeAsk> resumed = [];
    string? callerRole = "quality-lead";
    bool fleetLaunched = true, running;
    RestoreLaunch? recorded = new("copilot", "App", null, null, null, null, null, LaunchProtocol.AllowAll);
    LaunchResult outcome = new(LaunchOutcome.Started, null) { Session = null };

    public FleetResumeTests()
    {
        Settings();
        credentials = new(folder, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        caller = Caller(AgentProtocol.ScopeReadReplyLaunch);
    }
    public void Dispose() => Directory.Delete(folder, true);
    AgentCredential Caller(string scope, int depth = 0) => credentials.Check(credentials.Mint(Guid.NewGuid().ToString(), "person-launch", 100, started, scope, depth),
        new([new PeerProcess(100, started, "agent.exe")], true)).Accepted!;
    void Settings(string[]? roles = null, bool enabled = true, string[]? sessions = null, string[]? excludedWorkspaces = null, bool approved = true) =>
        File.WriteAllText(Path.Combine(folder, HostSettings.FileName), JsonSerializer.Serialize(new
        {
            agents = new { enabled, resume = new { roles = roles ?? ["quality-lead"] }, inbound = new { exclude = new { sessions = sessions ?? [], workspaces = excludedWorkspaces ?? [] } } },
            launch = new { enabled = true, workspaces = approved ? new[] { new { label = "App", path = folder } } : [] },
        }));
    string? name;
    FleetResumeCommands Open() => new(folder, credentials, session => session == target ? null : callerRole, _ => fleetLaunched, _ => running, _ => recorded,
        (ask, _) => { resumed.Add(ask); return Task.FromResult(outcome with { Session = ask.Id }); }, s => s == target ? name : null);
    Task<FleetResumeReceipt> Resume(string? session = null, Guid? request = null, AgentCredential? who = null) => Open().HandleAsync(who ?? caller,
        JsonSerializer.SerializeToElement(new { version = 1, request = request ?? Guid.NewGuid(), session = session ?? target }));
    [Fact]
    public async Task A_coordinator_resumes_a_fleet_worker_in_place_with_its_recorded_settings()
    {
        var receipt = await Resume();
        Assert.Equal(("resumed", null, target), (receipt.Outcome, receipt.Reason, receipt.SessionId));
        var ask = Assert.Single(resumed);
        Assert.Equal((target, LaunchProtocol.AllowAll), (ask.Id, ask.Launch!.Permissions));   // the original settings, not the caller's or the machine's
        Assert.Equal(credentials.PrincipalOf(caller), ask.By);
    }
    [Theory]
    [InlineData("empty")]       // the person has not listed any coordinator role: resume is off
    [InlineData("norole")]      // a builder or any session with no recorded role
    [InlineData("otherrole")]   // a role the person did not list
    public async Task Only_a_session_recorded_with_a_listed_coordinator_role_may_resume(string why)
    {
        if (why == "empty") { Settings([]); }
        else if (why == "norole") { callerRole = null; }
        else { callerRole = "builder"; }
        var receipt = await Resume();
        Assert.Equal(("refused", "policy"), (receipt.Outcome, receipt.Reason));
        Assert.Empty(resumed);
    }
    // The person's "no agent may reach" lists bind resume as they bind fleet.end: the caller, the target's fleet name, the recorded workspace.
    [Theory]
    [InlineData("caller")]
    [InlineData("name")]
    [InlineData("workspace")]
    [InlineData("unapproved")]
    public async Task Excluded_callers_names_and_workspaces_and_unapproved_workspaces_are_refused_like_fleet_end(string why)
    {
        name = "worker-7";
        if (why == "caller") { Settings(sessions: [caller.Session]); }
        else if (why == "name") { Settings(sessions: [name]); }
        else if (why == "workspace") { Settings(excludedWorkspaces: ["App"]); }
        else { Settings(approved: false); }
        var receipt = await Resume();
        Assert.Equal(("refused", "scope"), (receipt.Outcome, receipt.Reason));
        Assert.Empty(resumed);
    }
    [Fact]
    public async Task A_caller_without_launch_scope_or_not_at_depth_zero_is_refused()
    {
        Assert.Equal("policy", (await Resume(who: Caller(AgentProtocol.ScopeReadReply))).Reason);
        var deep = Caller(AgentProtocol.ScopeReadReplyLaunch, depth: 1);
        Assert.Equal(1, deep.Depth);   // the credential really is below depth 0
        Assert.Equal("policy", (await Resume(who: deep)).Reason);
        var adopted = credentials.Check(credentials.Mint(Guid.NewGuid().ToString(), AgentCredentials.AdoptedLaunch, 100, started, AgentProtocol.ScopeReadReplyLaunch),
            new([new PeerProcess(100, started, "agent.exe")], true)).Accepted!;   // an adopted session is never a coordinator
        Assert.Equal("policy", (await Resume(who: adopted)).Reason);
        Assert.Empty(resumed);
    }
    [Theory]
    [InlineData("person")]      // a session no fleet launch record shows
    [InlineData("running")]     // already live: no force, stop it first
    [InlineData("own")]
    [InlineData("derived")]
    [InlineData("norecord")]    // the original settings cannot be read
    public async Task Only_an_ended_fleet_worker_with_recorded_settings_is_resumed(string why)
    {
        var session = target;
        if (why == "person") { fleetLaunched = false; }
        else if (why == "running") { running = true; }
        else if (why == "own") { session = caller.Session; }
        else if (why == "derived") { session = target.Replace("-", "")[..16]; }
        else { recorded = null; }
        var receipt = await Resume(session);
        Assert.Equal("refused", receipt.Outcome);
        Assert.Equal(why switch { "running" => "running", "derived" => "bad-argument", "norecord" => "policy", _ => "scope" }, receipt.Reason);
        Assert.Empty(resumed);
    }
    [Fact]
    public async Task A_host_with_agents_off_or_a_bad_request_is_refused_and_a_repeat_returns_the_first_answer_once()
    {
        var request = Guid.NewGuid();
        var handler = Open();
        var data = JsonSerializer.SerializeToElement(new { version = 1, request, session = target });
        Assert.Equal("resumed", (await handler.HandleAsync(caller, data)).Outcome);
        Assert.Equal("resumed", (await handler.HandleAsync(caller, data)).Outcome);
        Assert.Single(resumed);                                                                      // never twice
        Assert.Equal("bad-argument", (await Open().HandleAsync(caller, JsonSerializer.SerializeToElement(new { version = 2, request, session = target }))).Reason);
        Settings(enabled: false);
        Assert.Equal("kill-switch", (await Resume()).Reason);
    }
    [Fact]
    public async Task Every_request_is_audited_under_the_agents_principal()
    {
        await Resume();
        await Resume(session: caller.Session);
        var rows = Directory.GetFiles(Path.Combine(folder, "audit"), "agents-*.jsonl").SelectMany(File.ReadAllLines).ToArray();
        Assert.Contains(rows, r => r.Contains("fleet.resume") && r.Contains("resumed") && r.Contains(credentials.PrincipalOf(caller)));
        Assert.Contains(rows, r => r.Contains("fleet.resume") && r.Contains("refused") && r.Contains("scope"));
    }
    sealed class Chan : IProtocolChannel
    {
        public string PeerId => "fixture";
        public string PeerName => "fixture";
        public string PeerKind => "fixture";
        public bool IsOpen => true;
        public Task Closed => Task.Delay(Timeout.Infinite);
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }
    [Fact]
    public async Task The_pipe_hands_fleet_resume_to_a_live_agent_session_credential_and_to_nobody_else()
    {
        var body = JsonSerializer.SerializeToElement(new { version = 1, request = Guid.NewGuid(), session = target });
        var agent = new Chan();
        credentials.Accept(agent, caller);
        var asAgent = LocalApi.Handler(null!, null!, new FixedCallerGuard(new Caller(CallerKind.Agent, "fixture")), credentials: credentials, fleetResume: Open());
        Assert.Equal("resumed", ((FleetResumeReceipt)(await asAgent(agent, FleetResumeCommands.Op, body))!).Outcome);

        var script = new Chan();
        credentials.Accept(script, credentials.MintScript("dispatcher").Credential);
        await Assert.ThrowsAsync<InvalidOperationException>(() => asAgent(script, FleetResumeCommands.Op, body));
        var person = LocalApi.Handler(null!, null!, new FixedCallerGuard(new Caller(CallerKind.App, "fixture")), credentials: credentials, fleetResume: Open());
        await Assert.ThrowsAsync<InvalidOperationException>(() => person(agent, FleetResumeCommands.Op, body));
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalApi.Handler(null!, null!, new FixedCallerGuard(new Caller(CallerKind.Agent, "fixture")),
            credentials: credentials)(agent, FleetResumeCommands.Op, body));
        Assert.Single(resumed);
    }
}
