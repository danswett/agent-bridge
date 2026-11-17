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
            provenance.Bound(request, Id, source.ProcessOf(Id)!);
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

    [Fact]
    public async Task Fleet_stop_requires_a_separate_live_host_grant_and_verified_fleet_origin()
    {
        var setup = Fleet(new Clearance(), grant: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => FleetStop(setup.Handle));
        setup = Fleet(new Clearance(), fleetOrigin: false);
        Assert.Equal(StopOutcome.Refused, Assert.IsType<StopResult>(await FleetStop(setup.Handle)).Outcome);
        setup = Fleet(new Clearance(), fleetOrigin: false, personOrigin: true);
        Assert.Equal(StopOutcome.Refused, Assert.IsType<StopResult>(await FleetStop(setup.Handle)).Outcome);
        setup = Fleet(new Clearance(), recorded: false);
        Assert.Equal(StopOutcome.Refused, Assert.IsType<StopResult>(await FleetStop(setup.Handle)).Outcome);
        setup = Fleet(new Clearance());
        setup.Credentials.RevokeScript("sre");
        await Assert.ThrowsAsync<InvalidOperationException>(() => FleetStop(setup.Handle));
        Assert.Empty(injector.Plans);
        Assert.Empty(control.Terminated);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData(Id)]
    [InlineData("AGENT_BRIDGE_7777777777774777")]
    public async Task Fleet_stop_requires_the_exact_registered_node(string node)
    {
        var setup = Fleet(new Clearance());
        Assert.Equal(StopOutcome.Refused, Assert.IsType<StopResult>(await FleetStop(setup.Handle, node)).Outcome);
        Assert.Empty(injector.Plans);
    }

    [Fact]
    public async Task Fleet_stop_refuses_a_grant_revoked_during_clearance()
    {
        var clearance = new Clearance();
        var setup = Fleet(clearance);
        clearance.Change = (read, value) =>
        {
            if (read > 1) { setup.Credentials.RevokeScript("sre"); }
            return value;
        };
        Assert.Equal(StopOutcome.Refused, Assert.IsType<StopResult>(await FleetStop(setup.Handle)).Outcome);
        Assert.Empty(injector.Plans);
        Assert.Empty(control.Terminated);
    }

    [Fact]
    public async Task Fleet_stop_defaults_to_unknown_despite_caller_supplied_clearance()
    {
        Assert.Equal(StopOutcome.Refused, Assert.IsType<StopResult>(await FleetStop(Fleet().Handle)).Outcome);
        Assert.Empty(injector.Plans);
        Assert.Empty(control.Terminated);
    }

    [Theory]
    [InlineData(0), InlineData(1), InlineData(2), InlineData(3), InlineData(4), InlineData(5), InlineData(6), InlineData(7)]
    public async Task Fleet_stop_rechecks_every_safeguard_and_identity_before_dispatch(int change)
    {
        var clearance = new Clearance();
        clearance.Change = (read, value) =>
        {
            if (read == 1) { return value; }
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
        Assert.Equal(StopOutcome.Refused, Assert.IsType<StopResult>(await FleetStop(Fleet(clearance).Handle)).Outcome);
        Assert.True(clearance.Reads >= 2);
        Assert.Empty(injector.Plans);
        Assert.Empty(control.Terminated);
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

    sealed class FleetChannel : IProtocolChannel
    {
        public string PeerId => "not-authority";
        public string PeerName => "SRE";
        public string PeerKind => "agent";
        public bool IsOpen => true;
        public Task Closed => Task.CompletedTask;
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
