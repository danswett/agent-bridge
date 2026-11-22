using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Fleet.Grants;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class FleetStopTests : IDisposable
{
    readonly Fixture f = new();
    readonly P256KeyPair key = P256KeyPair.Generate();
    readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    readonly string id = Guid.NewGuid().ToString();
    readonly Source source = new();
    readonly AgentCredentials credentials;
    readonly AgentCredential caller;
    readonly Control control;
    readonly Injector injector;
    readonly StopService stops;
    readonly GrantStore store;
    GrantDocument grant;
    readonly GrantCounters counters;
    readonly SessionService sessions;
    readonly Guid launch = Guid.NewGuid();
    const string Device = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    string Folder => Path.Combine(f.Root, "default");
    StopTarget Target => new(id, new(700, started));
    FleetStopCommands Open() => new(Folder, key, () => "123", Device, credentials, sessions, source, stops,
        pid => new(control.Exited ? GrantProcessStatus.Exited : GrantProcessStatus.Live, new(pid, started)));

    public FleetStopTests()
    {
        Directory.CreateDirectory(Folder);
        Settings();
        var events = f.CopilotSession(id, 700);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.Assistant("Done.", 3), Copilot.TurnEnd(4));
        source.Sessions = [FleetPublisherTests.NewSession(1) with { Id = id, Cwd = f.Root }];
        source.Binding = _ => new(700, started);
        f.Processes.Run(AgentKind.Copilot, 700, startedAt: started);
        credentials = new(Folder, Device);
        var token = credentials.Mint(Guid.NewGuid().ToString(), "person-launch", 100, started, AgentProtocol.ScopeReadReplyLaunch);
        caller = credentials.Check(token, new([new PeerProcess(100, started, "agent.exe")], true)).Accepted!;
        control = new(); injector = new(control);
        stops = new(new DeliveryGate(), source, f.Processes, new Probe(), injector, control,
            new() { CopilotHome = f.CopilotHome, PlatformSupported = true, Grace = TimeSpan.FromMilliseconds(10) });
        sessions = new(source, new(Device, "fixture", "windows", "1"), stops: stops);
        var scope = new GrantScope([Device], ["repo"], ["copilot"], ["small"], ["default"], ["medium"], ["default"], ["ask"]);
        store = new(Folder, key, new(Folder), scope);
        grant = new(1, Guid.NewGuid(), "123", new("123", Device, "AQ", started, new('a', 64)), new(Device, "default"),
            [new("123", Device, caller.Session, caller.Id, new(100, started))], ["launch", "stop"], scope,
            new(3, 3, 2), started, 1, started.AddHours(8), StopTargets: []);
        Assert.True(store.Issue(grant).Usable);
        counters = new(store);
    }
    public void Dispose() { key.Dispose(); f.Dispose(); }
    void Settings(string label = "repo", bool excluded = false) => File.WriteAllText(Path.Combine(Folder, HostSettings.FileName),
        JsonSerializer.Serialize(new { agents = new { enabled = true, inbound = new { exclude = new { workspaces = excluded ? new[] { label } : [] } } },
            launch = new { workspaces = new[] { new { label, path = f.Root, isolate = true } } } }));
    void Own()
    {
        Assert.True(counters.Reserve(grant.Id, 1, launch, grant.Subjects[0], new('b', 64)).Accepted);
        Assert.True(counters.Commit(launch, Target, "ask").Accepted);
    }
    Task<FleetStopReceipt> End(FleetStopCommands? handler = null, Guid? request = null, string? session = null) =>
        (handler ?? Open()).HandleAsync(caller, JsonSerializer.SerializeToElement(
            new FleetStopInput(1, request ?? Guid.NewGuid(), grant.Id, session ?? id), ProtocolJson.Options));

    [Fact]
    public async Task A_not_own_session_is_refused_but_an_exact_persisted_exception_ends()
    {
        var other = Guid.NewGuid().ToString();
        source.Sessions.Add(source.Sessions[0] with { Id = other });
        Assert.Equal("scope", (await End(session: other)).Reason);
        Assert.Equal("scope", (await End()).Reason);
        Assert.Equal(0, injector.Calls);
        var otherSubject = grant.Subjects[0] with { Session = Guid.NewGuid().ToString() };
        grant = grant with { Id = Guid.NewGuid(), Subjects = [grant.Subjects[0], otherSubject] };
        Assert.True(store.Issue(grant).Usable);
        var otherLaunch = Guid.NewGuid();
        Assert.True(counters.Reserve(grant.Id, 1, otherLaunch, otherSubject, new('c', 64)).Accepted);
        Assert.True(counters.Commit(otherLaunch, Target, "ask").Accepted);
        Assert.Equal("scope", (await End()).Reason);
        grant = grant with { Id = Guid.NewGuid(), StopTargets = [Target] };
        Assert.True(store.Issue(grant).Usable);
        Assert.Equal("ended", (await End(session: id.Replace("-", "")[..16])).Outcome);
        Assert.Equal(0, control.Terminations);
    }
    [Fact]
    public async Task Recycled_processes_and_revoked_grants_are_refused_without_typing()
    {
        Own();
        source.Binding = _ => new(700, started.AddSeconds(1));
        Assert.Equal("scope", (await End()).Reason);
        source.Binding = _ => new(700, started);
        Assert.True(store.Revoke(grant.Id, 1).Usable);
        Assert.Equal("grant-revoked", (await End()).Reason);
        Assert.Equal(0, injector.Calls);
    }
    [Fact]
    public async Task Held_stops_do_not_arm_and_graceful_timeout_never_terminates()
    {
        Own();
        stops.LaunchHeld = _ => "startup question";
        Assert.Equal("person-required", (await End()).Reason);
        Assert.Equal(0, injector.Calls);
        stops.LaunchHeld = null;
        control.Opens = 0;
        control.Before = () => { if (control.Opens == 2) { source.Sessions[0] = source.Sessions[0] with { Status = SessionStatus.Working }; } };
        Assert.Equal("person-required", (await End()).Reason);
        Assert.Equal(0, injector.Calls);
        control.Before = null;
        source.Sessions[0] = source.Sessions[0] with { Status = SessionStatus.Idle };
        var confirmed = await stops.StopAuditedAsync(new() { Id = id, Request = Guid.NewGuid().ToString(), Confirm = "fake" }, null, personConfirmation: false);
        Assert.Equal(StopOutcome.Refused, confirmed.Outcome);
        control.ExitOnTyping = false;
        var request = Guid.NewGuid();
        var first = await End(request: request);
        Assert.Equal("still-running", first.Reason);
        Assert.Equal(first.Outcome, (await End(request: request)).Outcome);
        Assert.Equal(1, injector.Calls);
        Assert.Equal(new GrantUsage(1, 1, 1), counters.Read(grant.Id, 1).Usage);
        Assert.Equal(0, control.Terminations);
    }
    [Fact]
    public async Task A_process_that_exits_before_dispatch_is_reported_ended_without_typing()
    {
        Own();
        control.GoneOnSecond = true;
        Assert.Equal("ended", (await End()).Outcome);
        Assert.Equal(0, injector.Calls);
        Assert.Equal(new GrantUsage(1, 1, 0), counters.Read(grant.Id, 1).Usage);
    }
    [Fact]
    public async Task Revocation_at_dispatch_is_refused_without_typing()
    {
        Own();
        control.Before = () => { if (control.Opens == 2) { Assert.True(store.Revoke(grant.Id, 1).Usable); } };
        Assert.Equal("grant-revoked", (await End()).Reason);
        Assert.Equal(0, injector.Calls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_exception_in_an_excluded_workspace_is_refused_at_prepare_and_dispatch(bool dispatch)
    {
        grant = grant with { Id = Guid.NewGuid(), StopTargets = [Target] };
        Assert.True(store.Issue(grant).Usable);
        if (dispatch) { control.Before = () => { if (control.Opens == 2) { Settings("protected", true); } }; }
        else { Settings("protected", true); }
        Assert.Equal("scope", (await End()).Reason);
        Assert.Equal(0, injector.Calls);
    }
    [Theory]
    [InlineData("revoke")]
    [InlineData("prompt")]
    [InlineData("question")]
    [InlineData("working")]
    [InlineData("held")]
    [InlineData("pid")]
    [InlineData("policy")]
    [InlineData("workspace")]
    public async Task Changes_while_showing_ending_refuse_agent_stop_without_keys(string change)
    {
        Own();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.BeforeEnding = async () => { entered.SetResult(); await release.Task; };
        var run = End();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            switch (change)
            {
                case "revoke": Assert.True(store.Revoke(grant.Id, 1).Usable); break;
                case "prompt": stops.LaunchHeld = _ => "startup question"; break;
                case "question": Fixture.Append(Path.Combine(f.CopilotHome, "session-state", id, "events.jsonl"), Copilot.ToolStart("q9", "ask_user", new { message = "Which?" }, 5)); break;
                case "working": source.Sessions[0] = source.Sessions[0] with { Status = SessionStatus.Working }; break;
                case "held": source.Held = new("unknown", [AgentKind.Copilot]); break;
                case "pid": source.Binding = _ => new(700, started.AddSeconds(1)); break;
                case "policy": File.WriteAllText(Path.Combine(Folder, HostSettings.FileName), """{"agents":{"enabled":false}}"""); break;
                case "workspace": Settings("other"); break;
            }
        }
        finally { release.SetResult(); }
        Assert.Equal("refused", (await run).Outcome);
        Assert.Equal(0, injector.Calls);
        Assert.Equal(0, control.Terminations);
    }
    [Fact]
    public async Task Person_stop_keeps_its_existing_path_while_showing_ending()
    {
        source.BeforeEnding = () => { stops.LaunchHeld = _ => "startup question"; return Task.CompletedTask; };
        Assert.Equal(StopOutcome.Ended, (await stops.StopAsync(new() { Id = id, Request = Guid.NewGuid().ToString() })).Outcome);
        Assert.Equal(1, injector.Calls);
    }
    [Fact]
    public async Task Own_launches_release_only_proven_live_capacity_not_hourly_or_daily_use()
    {
        Own();
        Assert.Equal("ended", (await End()).Outcome);
        Assert.Equal(new GrantUsage(1, 1, 0), counters.Read(grant.Id, 1).Usage);
        source.Sessions[0] = source.Sessions[0] with { Status = SessionStatus.Working };
        var result = await stops.StopAuditedAsync(new() { Id = id, Request = Guid.NewGuid().ToString() }, null, personConfirmation: false);
        Assert.Equal(StopOutcome.Confirm, result.Outcome);
        Assert.Null(result.Confirm);
        Assert.Equal(0, control.Terminations);
    }
    sealed class Probe : IAgentHaBridgeProbe { public string? Active() => null; }
    sealed class Source : ISessionSource
    {
        public List<Session> Sessions = [];
        public Func<string, SessionProcess?> Binding = _ => null;
        public Func<Task>? BeforeEnding;
        bool ending;
        public DiscoveryHeld? Held { get; set; }
        public SessionProcess? ProcessOf(string id) => Binding(id);
        public long Revision => 0;
        public event EventHandler<SessionChange>? Changed { add { } remove { } }
        public IReadOnlyList<Session> Snapshot() => Sessions;
        public SessionList List(string? cursor = null) => new(Snapshot(), 0);
        public Session? GetForStop(string id) => Sessions.Find(s => s.Id == id);
        public Session? Get(string id) => GetForStop(id) is { } s && ending ? s with { Status = SessionStatus.Ending } : GetForStop(id);
        public SessionSubscription Subscribe() => throw new NotSupportedException();
        public async Task SetEndingAsync(string id, bool value) { if (value && BeforeEnding is not null) { await BeforeEnding(); } ending = value; }
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    sealed class Control : IProcessControl
    {
        public bool Exited, GoneOnSecond, ExitOnTyping = true;
        public int Terminations, Opens;
        public Action? Before;
        public HeldOpen Open(int pid, DateTimeOffset at)
        {
            Opens++; Before?.Invoke();
            if (GoneOnSecond && Opens == 2) { Exited = true; return HeldOpen.Exited(); }
            return HeldOpen.Of(new Held(this, pid));
        }
        sealed class Held(Control owner, int pid) : IHeldProcess
        {
            public int Id => pid;
            public Task<bool> WaitForExitAsync(TimeSpan timeout) => Task.FromResult(owner.Exited);
            public string? Terminate() { owner.Terminations++; return null; }
            public void Dispose() { }
        }
    }
    sealed class Injector(Control control) : IConsoleInjector
    {
        public int Calls;
        public Task<InjectionResult> InjectAsync(int pid, KeyPlan plan, CancellationToken cancellationToken = default)
        { Calls++; control.Exited = control.ExitOnTyping; return Task.FromResult(new InjectionResult(InjectionStatus.Typed, "fixture")); }
    }
}
