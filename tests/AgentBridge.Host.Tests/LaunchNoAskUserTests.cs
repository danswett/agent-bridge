using System.Text.Json;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;

public sealed class LaunchNoAskUserTests : LaunchFlowFixture
{
    private protected override bool UsesRelay => false;

    [Theory]
    [InlineData("copilot", "agent:test/parent", null, true)]
    [InlineData("agency", "agent:test/parent", null, true)]
    [InlineData("copilot", "person-device", null, false)]
    [InlineData("agency", "person-device", null, false)]
    [InlineData("copilot", "agent:test/parent", true, false)]
    [InlineData("agency", "agent:test/parent", true, false)]
    [InlineData("copilot", "home-assistant", false, true)]
    [InlineData("claude", "agent:test/parent", null, false)]
    public async Task Only_non_person_Copilot_launches_disable_questions_unless_explicitly_overridden(
        string agent, string peer, bool? askUser, bool noAskUser)
    {
        locator.Installed[agent] = @"C:\fake\" + agent + ".exe";
        var isolated = peer.StartsWith("agent:", StringComparison.Ordinal);
        if (isolated)
        {
            Git(f.Root, "init", "-q", "-b", "main", app);
            File.WriteAllText(Path.Combine(app, "README.md"), "fixture\n");
            Git(app, "add", "README.md");
            Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "fixture");
        }
        var request = JsonSerializer.Deserialize<LaunchRequest>(JsonSerializer.Serialize(new
        {
            request = Guid.NewGuid().ToString("D"), agent, workspace = "App", askUser, prompt = "fixture prompt",
        }), ProtocolJson.Options)!;
        var launcher = Launcher(Config(c => c with { Workspaces = [new("App", app, isolated)] }),
            account: new SyntheticAccount(), agentWorktreeCheck: (_, _, _) => null);

        var result = await launcher.LaunchAsync(request, peer);

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        var args = Assert.Single(starter.Specs).Arguments;
        Assert.Equal(noAskUser, args.Contains("--no-ask-user", StringComparer.Ordinal));
        Assert.Equal("fixture prompt", args[^1]);
        Assert.Empty(screens.Typed);
    }

    sealed class SyntheticAccount : ILaunchAccount
    {
        public Task<LaunchAccount> CheckAsync(string? expectedLogin, CancellationToken cancellationToken) =>
            Task.FromResult(LaunchAccount.Allowed("synthetic-noask-account"));
    }
}
