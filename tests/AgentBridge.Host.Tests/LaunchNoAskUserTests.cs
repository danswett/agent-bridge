using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;

public sealed class LaunchNoAskUserTests : LaunchFlowFixture
{
    private protected override bool UsesRelay => false;

    [Theory]
    [InlineData("copilot", "agent:test/parent", null, true, null, false, false)]
    [InlineData("agency", "agent:test/parent", null, true, null, false, false)]
    [InlineData("copilot", "person-device", null, false, null, false, false)]
    [InlineData("agency", "person-device", null, false, null, false, false)]
    [InlineData("copilot", "person-device", true, false, "reviewer", false, false)]
    [InlineData("agency", "person-device", false, true, null, false, false)]
    [InlineData("copilot", "agent:test/parent", true, true, "reviewer", false, false)]
    [InlineData("agency", "agent:test/parent", true, true, "delegate", false, false)]
    [InlineData("agency", "agent:test/parent", true, true, "lead", false, true)]
    [InlineData("copilot", "script:test/dispatcher", null, true, "reviewer", true, false)]
    [InlineData("agency", "script:test/dispatcher", true, true, "lead", true, false)]
    [InlineData("copilot", "script:test/dispatcher", true, true, "delegate", true, false)]
    [InlineData("copilot", "home-assistant", false, true, null, false, false)]
    [InlineData("claude", "agent:test/parent", true, false, null, false, false)]
    public async Task Non_person_Copilot_child_argv_disables_questions_even_when_askUser_is_true(
        string agent, string peer, bool? askUser, bool noAskUser, string? role, bool fleet, bool audited)
    {
        locator.Installed[agent] = @"C:\fake\" + agent + ".exe";
        var isolated = fleet || peer.StartsWith("agent:", StringComparison.Ordinal);
        if (isolated)
        {
            Git(f.Root, "init", "-q", "-b", "main", app);
            File.WriteAllText(Path.Combine(app, "README.md"), "fixture\n");
            Git(app, "add", "README.md");
            Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "fixture");
        }
        var request = JsonSerializer.Deserialize<LaunchRequest>(JsonSerializer.Serialize(new
        {
            request = Guid.NewGuid().ToString("D"), agent, workspace = "App", askUser, role,
            prompt = "fixture \"quoted\" --no-ask-user text",
        }), ProtocolJson.Options)!;
        Directory.CreateDirectory(f.CopilotHome);
        File.WriteAllText(Path.Combine(f.CopilotHome, "mcp-config.json"), "{\"mcpServers\":{}}");
        var launcher = Launcher(Config(c => c with { Workspaces = [new("App", app, isolated)] }),
            account: new SyntheticAccount(), agentWorktreeCheck: (_, _, _) => null,
            credentials: new AgentCredentials(Directory.CreateDirectory(Path.Combine(f.Root, "noask-credentials")).FullName, "fixture"));

        var result = fleet ? await launcher.LaunchFleetAsync(request, peer, _ => session => { })
            : audited ? await launcher.LaunchAuditedAsync(request, peer, _ => session => { })
            : await launcher.LaunchAsync(request, peer);

        Assert.True(result.Outcome == LaunchOutcome.Started, $"{result.Outcome}: {result.Reason}");
        var args = Assert.Single(starter.Specs).Arguments;
        Assert.Equal(noAskUser, args.Contains("--no-ask-user", StringComparer.Ordinal));
        Assert.Equal(request.Prompt, args[^1]);
        Assert.DoesNotContain("--allow-all", args);
        Assert.Equal(LaunchProtocol.Ask, result.Launched!.Permissions);
        Assert.Empty(screens.Typed);
    }

    sealed class SyntheticAccount : ILaunchAccount
    {
        public Task<LaunchAccount> CheckAsync(string? expectedLogin, CancellationToken cancellationToken) =>
            Task.FromResult(LaunchAccount.Allowed("synthetic-noask-account"));
    }
}
