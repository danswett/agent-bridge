using System.Text.Json;
using AgentBridge.HomeAssistant;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Cli;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed partial class StopFlowTests
{
    sealed class StopLauncher(bool fleet) : ISessionLauncher
    {
        public bool Available => true;
        public Task<LaunchOptions> OptionsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LaunchResult> LaunchAsync(LaunchRequest request, string peer, CancellationToken ct = default) => throw new NotSupportedException();
        public bool IsFleetWorker(string session, SessionProcess? process) => fleet;
    }
    sealed class StopClearance : IWorkStateClearance
    {
        public int Reads;
        public Func<WorkStateClearance, WorkStateClearance> Change = value => value;
        public WorkStateClearance Read(string session, SessionProcess process)
        {
            Reads++;
            var clear = new WorkClearance(WorkState.Cleared, "fixture clear");
            return Change(new(session, process, Fixture.T0.AddHours(2), clear, clear, clear, clear));
        }
    }
    readonly StopScriptChannel stopScript = new();
    (Func<IProtocolChannel, string, JsonElement, Task<object?>> Handler, AgentCredentials Credentials, string Audit)
        StopFleet(StopClearance? clearance, bool fleet = true, bool grant = true)
    {
        var folder = Path.Combine(f.Root, "fleet-stop", Guid.NewGuid().ToString("N"));
        var credentials = new AgentCredentials(folder, "fixture", clock);
        var script = credentials.MintScript("sre").Credential;
        File.WriteAllText(Path.Combine(folder, HostSettings.FileName), """{"agents":{"enabled":true}}""");
        var audit = new AgentAuditLog(folder, clock);
        if (grant) { ScriptCredentialCommands.Handle(new StopScriptChannel(), "scripts.grant-stop",
            JsonSerializer.SerializeToElement(new { name = "sre" }),
            new FixedCallerGuard(new(CallerKind.HostCli, "fixture owner")), credentials, audit); }
        credentials.Accept(stopScript, script);
        var sessions = new SessionService(source, new("fixture", "Fixture", "windows", "1"), stops: Stopper(graceMs: 1),
            launcher: new StopLauncher(fleet)) { FleetStopClearance = clearance, Time = clock };
        return (LocalApi.Handler(sessions, null!, new FixedCallerGuard(new(CallerKind.Agent, "fixture")),
            credentials: credentials, audit: audit), credentials, folder);
    }
    Task<object?> SendFleet(Func<IProtocolChannel, string, JsonElement, Task<object?>> handler, string? node = null) =>
        handler(stopScript, Ops.SessionStop, JsonSerializer.SerializeToElement(new
        { node = node ?? HaContract.NodeId(Id), request = Guid.NewGuid().ToString() }));

    [Fact]
    public async Task Fleet_stop_ends_only_with_an_explicit_SRE_grant_and_records_agent_audit()
    {
        var setup = StopFleet(new StopClearance());
        Assert.Equal(StopOutcome.Ended, Assert.IsType<StopResult>(await SendFleet(setup.Handler)).Outcome);
        Assert.Single(injector.Plans);
        var rows = GateAuditProjection.Load(setup.Audit).Rows.Where(r => r.Action == Ops.SessionStop).ToArray();
        Assert.Equal(new[] { "intent", "terminal" }, rows.Select(r => r.Gate!.Phase));
        Assert.All(rows, r => { Assert.Equal("agent", r.Gate!.ActorKind); Assert.StartsWith("script:", r.Principal); });
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData(Id)]
    [InlineData("AGENT_BRIDGE_7777777777774777")]
    public async Task Fleet_stop_refuses_derived_wrong_and_unknown_nodes(string node)
    {
        var setup = StopFleet(new StopClearance());
        Assert.Equal(StopOutcome.Refused, Assert.IsType<StopResult>(await SendFleet(setup.Handler, node)).Outcome);
        Assert.Contains(GateAuditProjection.Load(setup.Audit).Rows, row => row.Action == Ops.SessionStop && row.Outcome == "refused");
        Assert.Empty(injector.Plans);
        Assert.Empty(control.Terminated);
    }

    [Theory]
    [InlineData(0), InlineData(1), InlineData(2), InlineData(3), InlineData(4), InlineData(5), InlineData(6), InlineData(7)]
    public async Task Fleet_stop_refuses_person_missing_unknown_and_protected_work(int blocked)
    {
        var clearance = new StopClearance();
        clearance.Change = value => blocked switch
        {
            2 => value with { OpenOwnedPr = new(WorkState.Refused, "owned PR") },
            3 => value with { UnpushedWork = new(WorkState.Refused, "unpushed") },
            4 => value with { Held = new(WorkState.Refused, "held") },
            5 => value with { Private = new(WorkState.Unknown, "private unknown") },
            6 => value with { CheckedAt = value.CheckedAt.AddMinutes(-1) }, _ => value,
        };
        var setup = StopFleet(blocked == 1 ? null : clearance, fleet: blocked != 0);
        if (blocked == 7) { File.WriteAllText(Path.Combine(setup.Audit, HostSettings.FileName), """{"agents":{"enabled":false}}"""); }
        Assert.Equal(StopOutcome.Refused, Assert.IsType<StopResult>(await SendFleet(setup.Handler)).Outcome);
        Assert.Contains(GateAuditProjection.Load(setup.Audit).Rows, row => row.Action == Ops.SessionStop && row.Outcome == "refused");
        Assert.Empty(injector.Plans);
        Assert.Empty(control.Terminated);
    }

    [Fact]
    public async Task Fleet_stop_read_credentials_cannot_self_grant_or_fall_back_to_person_authority()
    {
        var setup = StopFleet(new StopClearance(), grant: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SendFleet(setup.Handler));
        Assert.Equal("script:fixture/sre", Assert.Single(GateAuditProjection.Load(setup.Audit).Rows, row => row.Action == Ops.SessionStop).Principal);
        Assert.Throws<InvalidOperationException>(() => ScriptCredentialCommands.Handle(stopScript, "scripts.grant-stop",
            JsonSerializer.SerializeToElement(new { name = "sre" }),
            new FixedCallerGuard(new(CallerKind.HostCli, "spoofed owner")), setup.Credentials, new AgentAuditLog(setup.Audit)));
        Assert.Empty(injector.Plans);
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public async Task Fleet_stop_rechecks_clearance_and_revocation_after_the_injector_wait(bool revoke)
    {
        var clearance = new StopClearance();
        var setup = StopFleet(clearance);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        injector.Gate = release.Task;
        var stop = SendFleet(setup.Handler);
        await injector.Entered.Task.WaitAsync(Patience);
        if (revoke) { setup.Credentials.RevokeScript("sre"); }
        else { clearance.Change = value => value with { UnpushedWork = new(WorkState.Refused, "new work") }; }
        release.SetResult();
        Assert.Equal(StopOutcome.Refused, Assert.IsType<StopResult>(await stop).Outcome);
        Assert.Contains(GateAuditProjection.Load(setup.Audit).Rows, row => row.Action == Ops.SessionStop && row.Outcome == "refused");
        Assert.Empty(control.Terminated);
        Assert.NotNull(source.Get(Id));
    }

    [Fact]
    public async Task Fleet_stop_grants_survive_reload_but_revocation_never_falls_back()
    {
        var setup = StopFleet(new StopClearance());
        var granted = setup.Credentials.Of(stopScript)!;
        var reopened = new AgentCredentials(setup.Audit, "fixture", clock);
        var connection = new StopScriptChannel();
        reopened.Accept(connection, granted);
        Assert.True(reopened.Of(connection)!.FleetStop);
        Assert.False(reopened.Of(connection)!.FleetLaunch);
        ScriptCredentialCommands.Handle(new StopScriptChannel(), "scripts.revoke-stop",
            JsonSerializer.SerializeToElement(new { name = "sre" }),
            new FixedCallerGuard(new(CallerKind.HostCli, "fixture owner")), setup.Credentials, new AgentAuditLog(setup.Audit, clock));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SendFleet(setup.Handler));
        Assert.Equal("script:fixture/sre", Assert.Single(GateAuditProjection.Load(setup.Audit).Rows, row => row.Action == Ops.SessionStop).Principal);
        var after = new AgentCredentials(setup.Audit, "fixture", clock);
        after.Accept(connection, granted);
        Assert.False(after.Of(connection)!.FleetStop);
        setup.Credentials.RevokeScript("sre");
        await Assert.ThrowsAsync<InvalidOperationException>(() => SendFleet(setup.Handler));
        Assert.Equal(new[] { "script:fixture/sre", "script:fixture/sre" }, GateAuditProjection.Load(setup.Audit).Rows.Where(row => row.Action == Ops.SessionStop).Select(row => row.Principal));
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task Fleet_stop_rechecks_protection_before_forced_termination()
    {
        var clearance = new StopClearance();
        injector.OnInject = (_, _) => clearance.Change = value => value with { Held = new(WorkState.Refused, "held now") };
        var setup = StopFleet(clearance);
        Assert.Equal(StopOutcome.Refused, Assert.IsType<StopResult>(await SendFleet(setup.Handler)).Outcome);
        Assert.Contains(GateAuditProjection.Load(setup.Audit).Rows, row => row.Action == Ops.SessionStop && row.Outcome == "refused");
        Assert.Empty(control.Terminated);
    }
}
sealed class StopScriptChannel : IProtocolChannel
{
    public string PeerId => "not-authority";
    public string PeerName => "not-authority";
    public string PeerKind => "agent";
    public bool IsOpen => true;
    public Task Closed => Task.CompletedTask;
    public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
}
