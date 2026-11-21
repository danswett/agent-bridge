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
    readonly FleetPublisherTests.Source source = new();
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
        File.WriteAllText(Path.Combine(Folder, HostSettings.FileName), """{"agents":{"enabled":true}}""");
        var events = f.CopilotSession(id, 700);
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.Assistant("Done.", 3), Copilot.TurnEnd(4));
        source.Sessions = [FleetPublisherTests.NewSession(1) with { Id = id }];
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
        control.ExitOnTyping = false;
        Assert.Equal("still-running", (await End()).Reason);
        Assert.Equal(0, control.Terminations);
    }
    [Fact]
    public async Task Revocation_at_dispatch_is_refused_without_typing()
    {
        Own();
        control.Before = () => { if (control.Opens == 2) { Assert.True(store.Revoke(grant.Id, 1).Usable); } };
        Assert.Equal("grant-revoked", (await End()).Reason);
        Assert.Equal(0, injector.Calls);
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
    sealed class Control : IProcessControl
    {
        public bool Exited, ExitOnTyping = true;
        public int Terminations, Opens;
        public Action? Before;
        public HeldOpen Open(int pid, DateTimeOffset at) { Opens++; Before?.Invoke(); return HeldOpen.Of(new Held(this, pid)); }
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
