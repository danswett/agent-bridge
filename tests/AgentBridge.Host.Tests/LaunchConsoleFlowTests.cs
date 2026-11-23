using AgentBridge.Protocol;
using Xunit;

public sealed class LaunchConsoleFlowTests : LaunchFlowFixture
{
    private protected override bool UsesRelay => false;

    [Theory]
    [InlineData("copilot", 2)]
    [InlineData("agency", 2)]
    [InlineData("claude", 0)]
    public async Task Only_Copilot_launches_size_their_own_console_at_start_and_first_read(string agent, int attempts)
    {
        locator.Installed[agent] = @"C:\fake\agent.exe";
        var launcher = Launcher(Config());
        var result = await launcher.LaunchAsync(
            new LaunchRequest { Request = Guid.NewGuid().ToString(), Agent = agent, Workspace = "App" }, "fixture");

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        var process = Assert.Single(launcher.LaunchedProcesses());
        Assert.Equal(attempts, screens.Sizes.Count);
        Assert.All(screens.Sizes, s => Assert.Equal((process.ProcessId, process.StartedAt, 100), s));
        Assert.Empty(screens.Typed);
    }
}
