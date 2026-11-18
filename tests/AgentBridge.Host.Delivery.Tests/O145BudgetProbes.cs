using AgentBridge.Host.Delivery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class O145BudgetProbes
{
    [Fact]
    public async Task Exit_rescans_share_one_250ms_collection_budget()
    {
        var rig = new TreeRig();
        rig.Nodes.Remove(102);
        rig.Nodes.Remove(103);
        var clock = new FakeTimeProvider();
        rig.AfterScan = () => clock.Advance(TimeSpan.FromMilliseconds(200));
        var control = new ProcessTreeControl(rig.Open, rig.Evidence(), clock);
        using var held = control.Open(101, TreeRig.T0).Process!;
        held.Terminate();
        var began = clock.GetTimestamp();
        var exited = await held.WaitForExitAsync(TimeSpan.FromMilliseconds(250));
        var elapsed = clock.GetElapsedTime(began);
        Assert.False(exited, $"Exit proof returned true after {elapsed.TotalMilliseconds} ms of rescans");
        Assert.NotNull(held.ExitProblem);
    }
}
