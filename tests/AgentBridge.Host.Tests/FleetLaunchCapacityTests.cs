using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class FleetLaunchCapacityTests : LaunchFlowFixture
{
    private protected override bool UsesRelay => false;
    readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 17, 0, 0, 0, TimeSpan.Zero));
    const string Principal = "script:machine/dispatcher";
    static LaunchRequest Request() => new() { Request = Guid.NewGuid().ToString("D"), Agent = "claude",
        Workspace = "App", Permissions = LaunchProtocol.AllowAll };

    (SessionService Service, AgentAuditLog Audit, ScriptedLauncher Launcher) Setup(ISessionSource? discovery = null)
    {
        var profile = Directory.CreateDirectory(Path.Combine(f.Root, "profile")).FullName;
        File.WriteAllText(Path.Combine(profile, HostSettings.FileName),
            """{"agents":{"enabled":true,"limits":{"fleetPerHour":8,"liveFleetWorkers":6}}}""");
        var launcher = new ScriptedLauncher();
        return (new SessionService(discovery ?? source, new("machine", "fixture", "windows", "1"), launcher: launcher)
            { Time = time }, new AgentAuditLog(profile, time), launcher);
    }

    static Task<LaunchResult> Launch(SessionService service, AgentAuditLog audit) =>
        service.LaunchFleetAsync(Request(), Principal, audit, () => null, _ => null);

    [Theory]
    [InlineData(LaunchOutcome.Refused)]
    [InlineData(LaunchOutcome.Busy)]
    [InlineData(LaunchOutcome.Failed)]
    public async Task Every_no_start_outcome_releases_its_live_reservation(LaunchOutcome outcome)
    {
        var (service, audit, launcher) = Setup();
        launcher.Outcome = outcome;
        for (var i = 0; i < 6; i++) { Assert.Equal(outcome, (await Launch(service, audit)).Outcome); }
        launcher.Outcome = LaunchOutcome.Started;
        Assert.Equal(LaunchOutcome.Started, (await Launch(service, audit)).Outcome);
        Assert.Equal(6, GateAuditProjection.Load(audit.Profile).Rows.Count(r => r.Gate?.Phase == "terminal" && r.Outcome != "started"));
    }

    [Fact]
    public async Task Unresolved_intents_reserve_only_a_bounded_window_even_after_restart()
    {
        var (service, audit, launcher) = Setup();
        launcher.Outcome = LaunchOutcome.Unconfirmed;
        for (var i = 0; i < 6; i++) { Assert.Equal(LaunchOutcome.Unconfirmed, (await Launch(service, audit)).Outcome); }
        Assert.Contains("live fleet", (await Launch(service, audit)).Reason);
        time.Advance(TimeSpan.FromMinutes(10));
        var restarted = new SessionService(source, new("machine", "fixture", "windows", "1"), launcher: launcher) { Time = time };
        Assert.Equal(LaunchOutcome.Unconfirmed, (await Launch(restarted, audit)).Outcome);
    }

    [Fact]
    public async Task Duplicate_intents_for_one_request_spend_only_one_hourly_admission()
    {
        var (service, audit, _) = Setup();
        Assert.True(audit.Bootstrap());
        var request = Request();
        var operation = Guid.NewGuid();
        var intent = new AgentAuditEntry("local", request.Request, Principal, [], "session.launch", request.Request, "reserved",
            Gate: new(1, operation, Guid.Empty, 0, "intent", "local", "agent", operation, "none")) { Grant = "fleet" };
        for (var i = 0; i < 8; i++) { Assert.True(audit.Append(intent)); }
        Assert.Equal(LaunchOutcome.Started, (await Launch(service, audit)).Outcome);
    }

    [Fact]
    public async Task Several_sessions_on_one_proven_fleet_process_spend_only_one_live_slot()
    {
        var id = Guid.NewGuid().ToString();
        f.CopilotSession(id, 4321);
        f.Processes.Run(AgentKind.Copilot, 4321, $"copilot --session-id {id}", DateTimeOffset.UtcNow);
        await source.RefreshAsync();
        Assert.Single(source.Snapshot());
        var (service, audit, launcher) = Setup(new RepeatedDiscovery(source));
        launcher.Worker = true;
        Assert.Equal(LaunchOutcome.Started, (await Launch(service, audit)).Outcome);
    }

    sealed class ScriptedLauncher : ISessionLauncher
    {
        public LaunchOutcome Outcome = LaunchOutcome.Started;
        public bool Worker;
        public bool Available => true;
        public LaunchConfig FleetConfig => LaunchConfig.Default;
        public Task<LaunchOptions> OptionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<LaunchResult> LaunchAsync(LaunchRequest request, string peer, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public bool IsFleetWorker(string session, SessionProcess? process) => Worker;
        public Task<LaunchResult> LaunchFleetAsync(LaunchRequest request, string principal, Func<Guid, Action<string>?> prepare, CancellationToken cancellationToken = default)
        {
            var registered = prepare(Guid.NewGuid());
            if (registered is null) { return Task.FromResult(new LaunchResult(LaunchOutcome.Refused)); }
            var session = Guid.NewGuid().ToString();
            if (Outcome == LaunchOutcome.Started) { registered(session); }
            return Task.FromResult(new LaunchResult(Outcome) { Session = Outcome == LaunchOutcome.Started ? session : null });
        }
    }

    sealed class RepeatedDiscovery(ISessionSource inner) : ISessionSource
    {
        public long Revision => inner.Revision;
        public event EventHandler<SessionChange>? Changed { add { } remove { } }
        public IReadOnlyList<Session> Snapshot() => Enumerable.Range(0, 6).Select(i => inner.Snapshot()[0] with { Id = $"alias-{i}" }).ToArray();
        public SessionProcess? ProcessOf(string id) => inner.ProcessOf(inner.Snapshot()[0].Id);
        public Session? Get(string id) => inner.Get(id);
        public SessionList List(string? cursor = null) => new(Snapshot(), Revision);
        public SessionSubscription Subscribe() => inner.Subscribe();
        public Task RefreshAsync(CancellationToken cancellationToken = default) => inner.RefreshAsync(cancellationToken);
        public Task StartAsync(CancellationToken cancellationToken = default) => inner.StartAsync(cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
