using System.Text.Json;
using AgentBridge.HomeAssistant;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed partial class StopFlowTests
{
    sealed class FleetLauncher(LaunchProvenance provenance) : ISessionLauncher
    {
        public bool Available => true;
        public Task<LaunchOptions> OptionsAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LaunchResult> LaunchAsync(LaunchRequest request, string peer, CancellationToken ct = default) => throw new NotSupportedException();
        public bool FleetLaunchedSession(string session, SessionProcess process) => provenance.FleetContains(session, process);
    }

    sealed class Clearance : IWorkStateClearance
    {
        public int Reads;
        public Func<int, WorkStateClearance, WorkStateClearance> Change = (_, value) => value;
        public WorkStateClearance Read(string session, SessionProcess process)
        {
            var clear = new WorkClearance(WorkState.Cleared, "fixture clear");
            return Change(++Reads, new(session, process, Fixture.T0.AddHours(2), clear, clear, clear, clear));
        }
    }

    (Func<IProtocolChannel, string, JsonElement, Task<object?>> Handle, AgentCredentials Credentials, FleetController Controller)
        Fleet(Clearance? clearance = null, bool grant = true, bool fleetOrigin = true, bool personOrigin = false, bool recorded = true)
    {
        var credentials = new AgentCredentials(Path.Combine(f.Root, "fleet-controller", Guid.NewGuid().ToString("N")), "fixture", clock);
        var credential = credentials.MintScript("sre").Credential;
        credentials.Accept(fleetChannel, credential);
        var provenance = new LaunchProvenance(null, logged.Enqueue);
        var request = Guid.NewGuid();
        if (recorded)
        {
            provenance.Preparing(request, Id, new LaunchAttribution("local", personOrigin ? "person-client" : "agent", credential.Principal!, fleetOrigin));
            var process = source.ProcessOf(Id)!;
            provenance.Bound(request, Id, process, new Started(process.Id, process.StartedAt!.Value));
        }
        var controller = new FleetController(credentials, source, new FleetLauncher(provenance), Stopper(graceMs: 1),
            new AgentAuditLog(Path.Combine(f.Root, "fleet-audit"), clock), clearance, clock);
        if (grant) { controller.RegisterController(credential); }
        return (LocalApi.Handler(null!, null!, null!, credentials: credentials, fleetController: controller), credentials, controller);
    }

    readonly FleetChannel fleetChannel = new();
    Task<object?> FleetStop(Func<IProtocolChannel, string, JsonElement, Task<object?>> handle, string? node = null) =>
        handle(fleetChannel, FleetController.StopOp, JsonSerializer.SerializeToElement(new
        {
            node = node ?? HaContract.NodeId(Id), request = Guid.NewGuid().ToString(),
            openOwnedPr = false, unpushedWork = false, held = false, @private = false,
        }));

    [Theory]
    [InlineData(0), InlineData(1), InlineData(2), InlineData(3), InlineData(4)]
    public async Task Fleet_stop_requires_a_separate_live_host_grant_and_verified_fleet_origin(int missing)
    {
        var setup = Fleet(new Clearance(), grant: missing != 0, fleetOrigin: missing is not (1 or 2),
            personOrigin: missing == 2, recorded: missing != 3);
        if (missing == 4) { setup.Credentials.RevokeScript("sre"); }
        if (missing is 0 or 4) { await Assert.ThrowsAsync<InvalidOperationException>(() => FleetStop(setup.Handle)); }
        else { Assert.Equal(StopOutcome.Refused, Assert.IsType<StopResult>(await FleetStop(setup.Handle)).Outcome); }
        Assert.Empty(injector.Plans);
        Assert.Empty(control.Terminated);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData(Id)]
    [InlineData("AGENT_BRIDGE_7777777777774777")]
    [InlineData(null)]
    public async Task Fleet_stop_requires_the_exact_registered_node_and_host_clearance(string? node)
    {
        var setup = Fleet(node is null ? null : new Clearance());
        Assert.Equal(StopOutcome.Refused, Assert.IsType<StopResult>(await FleetStop(setup.Handle, node)).Outcome);
        Assert.Empty(injector.Plans);
        Assert.Empty(control.Terminated);
    }

    [Theory]
    [InlineData(0), InlineData(1), InlineData(2), InlineData(3), InlineData(4), InlineData(5), InlineData(6), InlineData(7)]
    [InlineData(9)]
    [InlineData(0, true), InlineData(1, true), InlineData(2, true), InlineData(3, true), InlineData(8, true)]
    public async Task Fleet_stop_rechecks_every_safeguard_and_identity_before_dispatch(int change, bool delayed = false)
    {
        var clearance = new Clearance();
        var setup = Fleet(clearance);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool changed = false, typed = false;
        injector.Gate = delayed ? release.Task : Task.CompletedTask;
        injector.OnInject = (_, _) => typed = true;
        clearance.Change = (read, value) =>
        {
            if (read == 1 || delayed && !changed) { return value; }
            if (change == 9) { setup.Credentials.RevokeScript("sre"); return value; }
            var refused = new WorkClearance(WorkState.Refused, "protected work");
            return change switch
            {
                0 => value with { OpenOwnedPr = refused }, 1 => value with { UnpushedWork = refused },
                2 => value with { Held = refused }, 3 => value with { Private = new(WorkState.Unknown, "unknown") },
                4 => value with { CheckedAt = value.CheckedAt.AddMinutes(-1) },
                5 => value with { Process = new(Pid, Started.AddSeconds(1)) },
                7 => value with { CheckedAt = value.CheckedAt.AddMinutes(1) },
                _ => value with { Session = Other },
            };
        };
        var stop = FleetStop(setup.Handle);
        if (delayed)
        {
            await injector.Entered.Task.WaitAsync(Patience);
            changed = true;
            if (change == 8) { setup.Credentials.RevokeScript("sre"); }
            release.SetResult();
        }
        Assert.Equal(StopOutcome.Refused, Assert.IsType<StopResult>(await stop).Outcome);
        Assert.True(clearance.Reads >= 2);
        Assert.Equal(delayed ? 1 : 0, injector.Plans.Count);
        Assert.False(typed);
        Assert.Empty(control.Terminated);
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public async Task A_completed_fleet_stop_replays_after_the_node_disappears_without_another_effect(bool forced)
    {
        var setup = Fleet(new Clearance());
        if (forced) { injector.OnInject = null; }
        var body = JsonSerializer.SerializeToElement(new { node = HaContract.NodeId(Id), request = Guid.NewGuid().ToString() });
        var first = Assert.IsType<StopResult>(await setup.Handle(fleetChannel, FleetController.StopOp, body));
        Assert.Equal(StopOutcome.Ended, first.Outcome);
        Assert.Equal(forced ? true : (bool?)null, first.Forced);
        await source.RefreshAsync();
        Assert.Null(source.Get(Id));
        probe.Reason = "bridge now active";
        var again = Assert.IsType<StopResult>(await setup.Handle(fleetChannel, FleetController.StopOp, body));
        Assert.Equal(first, again);
        Assert.Single(injector.Plans);
        Assert.Equal(forced ? 1 : 0, control.Terminated.Count);
        var wrong = JsonSerializer.SerializeToElement(new { node = HaContract.NodeId(Other), request = body.GetProperty("request").GetString() });
        Assert.Equal(StopOutcome.Refused, Assert.IsType<StopResult>(await setup.Handle(fleetChannel, FleetController.StopOp, wrong)).Outcome);
        var other = setup.Credentials.MintScript("other").Credential;
        var channel = new FleetChannel();
        setup.Credentials.Accept(channel, other);
        setup.Controller.RegisterController(other);
        Assert.Equal(StopOutcome.Refused, Assert.IsType<StopResult>(await setup.Handle(channel, FleetController.StopOp, body)).Outcome);
        setup.Credentials.RevokeScript("sre");
        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.Handle(fleetChannel, FleetController.StopOp, body));
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public async Task Fleet_stop_checks_again_before_forced_termination(bool protect)
    {
        var clearance = new Clearance();
        if (protect) { injector.OnInject = (_, _) => clearance.Change = (_, value) => value with { Held = new(WorkState.Refused, "held") }; }
        else { injector.OnInject = null; }
        var result = Assert.IsType<StopResult>(await FleetStop(Fleet(clearance).Handle));
        Assert.Equal(protect ? StopOutcome.Refused : StopOutcome.Ended, result.Outcome);
        Assert.Single(injector.Plans);
        Assert.Equal(protect ? 0 : 1, control.Terminated.Count);
        var rows = GateAuditProjection.Load(Path.Combine(f.Root, "fleet-audit")).Rows;
        Assert.Single(rows, r => r.Action == Ops.SessionStop && r.Gate?.Phase == "intent");
        Assert.All(rows.Where(r => r.Action == Ops.SessionStop), r => Assert.Equal("agent", r.Gate!.ActorKind));
    }

}

sealed class FleetChannel : IProtocolChannel
{
    public string PeerId => "not-authority";
    public string PeerName => "SRE";
    public string PeerKind => "agent";
    public bool IsOpen => true;
    public Task Closed => Task.CompletedTask;
    public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
}
