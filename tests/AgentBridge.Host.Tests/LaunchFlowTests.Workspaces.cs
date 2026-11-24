using AgentBridge.Protocol;
using Xunit;

public sealed partial class LaunchFlowTests
{
    [Fact]
    public async Task A_workspace_approved_after_the_host_started_is_launched_in_at_once_and_not_before()
    {
        var launcher = Launcher(Config());
        Task<LaunchResult> Ask() => launcher.LaunchAsync(new LaunchRequest { Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "New" }, "phone");

        var before = await Ask();
        launcher.SetWorkspaces([.. launcher.FleetConfig.Workspaces, new("New", elsewhere, false)]);
        var after = await Ask();

        Assert.Equal((LaunchOutcome.Refused, LaunchOutcome.Started), (before.Outcome, after.Outcome));
        Assert.Equal(["App", "API", "New"], launcher.FleetConfig.Workspaces.Select(w => w.Label));
        Assert.Single(starter.Specs);
    }
}
