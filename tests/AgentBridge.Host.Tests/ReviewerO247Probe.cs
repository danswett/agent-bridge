using AgentBridge.Host;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using Xunit;

public sealed partial class StopFlowTests
{
    [Theory]
    [InlineData(false), InlineData(true)]
    public async Task Reviewer_O247_refused_fleet_stop_has_a_durable_audit(bool afterInitialCheck)
    {
        var clearance = new StopClearance();
        clearance.Change = value => !afterInitialCheck || clearance.Reads >= 2
            ? value with { Held = new(WorkState.Refused, "reviewer protected work") } : value;
        var setup = StopFleet(clearance);
        var result = Assert.IsType<StopResult>(await SendFleet(setup.Handler));
        Assert.Equal(StopOutcome.Refused, result.Outcome);
        Assert.Empty(injector.Plans);
        Assert.Empty(control.Terminated);
        var rows = GateAuditProjection.Load(setup.Audit).Rows.Where(row => row.Action == Ops.SessionStop).ToArray();
        Assert.NotEmpty(rows);
        Assert.Contains(rows, row => row.Outcome == "refused");
        Assert.DoesNotContain(rows, row => row.Outcome == "ended");
    }
}
