using AgentBridge.Host.Fleet;
using AgentBridge.Protocol;
using Xunit;

public sealed partial class LaunchFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fleet_name_reservations_follow_proven_failure_and_late_registration(bool failed)
    {
        starter.Registers = false;
        screens.Rows = CopilotReady;
        var launcher = Launcher(Config(), wait: TimeSpan.FromMilliseconds(50));
        var names = new FleetNames(f.Root);
        var policy = new FleetLaunchNames(launcher, source, names, new RelayFleetRoster(), f.Root, logged.Enqueue);
        launcher.NamedLaunch = policy.RunAsync;
        LaunchRequest Ask() => new() { Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "App", FleetName = "SG Worker 1829" };
        var first = await launcher.LaunchAsync(Ask(), "fixture");
        Assert.Equal(LaunchOutcome.Unconfirmed, first.Outcome);
        Assert.Contains("already", (await launcher.LaunchAsync(Ask(), "fixture")).Reason);
        Assert.Single(starter.Specs);
        if (failed)
        {
            starter.Exit(starter.Pids[0]);
            await WaitAsync(async () => (await policy.RunAsync(Ask(), "fixture",
                () => Task.FromResult(new LaunchResult(LaunchOutcome.Refused, "available")))).Reason == "available");
            Assert.Null(names.Get(first.Session!));
        }
        else
        {
            starter.Register(starter.Specs[0]);
            await WaitAsync(async () => { await source.RefreshAsync(); return names.Get(first.Session!) == "SG Worker 1829"; });
            Assert.Contains("already", (await launcher.LaunchAsync(Ask(), "fixture")).Reason);
        }
    }
}
