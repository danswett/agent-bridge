using AgentBridge.Protocol;
using Xunit;

public sealed class LaunchRequestQueueFlowTests : LaunchFlowFixture
{
    private protected override bool UsesRelay => false;

    [Fact]
    public async Task Two_different_request_ids_both_launch_with_their_own_prompt()
    {
        var launcher = Launcher(Config());
        var first = new LaunchRequest
        {
            Request = Guid.NewGuid().ToString("D"), Agent = "copilot", Workspace = "App", Prompt = "first request",
        };
        var second = first with { Request = Guid.NewGuid().ToString("D"), Prompt = "second request" };

        var outcomes = await Task.WhenAll(launcher.LaunchAsync(first, "fixture"), launcher.LaunchAsync(second, "fixture"))
            .WaitAsync(Patience);

        Assert.All(outcomes, outcome => Assert.Equal(LaunchOutcome.Started, outcome.Outcome));
        Assert.NotEqual(outcomes[0].Session, outcomes[1].Session);
        Assert.Equal(2, starter.Specs.Count);
        Assert.Contains(starter.Specs, spec => spec.Arguments.Contains("first request"));
        Assert.Contains(starter.Specs, spec => spec.Arguments.Contains("second request"));
    }

    [Fact]
    public async Task A_repeated_request_id_launches_once_while_pending_and_after_completion()
    {
        var launcher = Launcher(Config());
        var request = new LaunchRequest
        {
            Request = Guid.NewGuid().ToString("D"), Agent = "copilot", Workspace = "App", Prompt = "one request",
        };

        var first = launcher.LaunchAsync(request, "fixture");
        var repeat = launcher.LaunchAsync(request, "fixture");
        Assert.Same(first, repeat);
        var outcome = await first.WaitAsync(Patience);
        var settledRepeat = await launcher.LaunchAsync(request, "fixture").WaitAsync(Patience);

        Assert.Equal(LaunchOutcome.Started, outcome.Outcome);
        Assert.Equal(outcome, settledRepeat);
        Assert.Single(starter.Specs);
    }
}
