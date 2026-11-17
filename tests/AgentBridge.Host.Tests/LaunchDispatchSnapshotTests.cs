using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;

public sealed class LaunchDispatchSnapshotTests : LaunchFlowFixture
{
    private protected override bool UsesRelay => false;

    [Theory]
    [InlineData("copilot")]
    [InlineData("agency")]
    [InlineData("claude")]
    [InlineData("codex")]
    public void Dispatch_copies_the_selected_launchers_configured_arguments_without_normalizing_them(string agent)
    {
        var extras = new List<string> { "--custom", " value ", "--custom" };
        var agents = new Dictionary<string, AgentConfig>
        {
            [agent] = new() { ExtraArgs = extras },
            ["other"] = new() { ExtraArgs = ["not selected"] },
        };
        var launcher = Launcher(Config(c => c with { Agents = agents }));

        var snapshot = launcher.CaptureDispatch(agent, "agent:fixture");
        extras[0] = "--changed";
        agents[agent] = new() { ExtraArgs = ["replacement"] };

        Assert.Equal(["--custom", " value ", "--custom"], snapshot.ExtraArguments);
        Assert.Equal(["replacement"], launcher.CaptureDispatch(agent, "agent:fixture").ExtraArguments);
        Assert.Throws<NotSupportedException>(() => { ((IList<string>)snapshot.ExtraArguments)[0] = "mutated"; });
    }

    [Theory]
    [InlineData("agent:fixture", "agent:fixture")]
    [InlineData("person-client", "person-client")]
    [InlineData("script:fixture/task", "script:fixture/task")]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData(" \t", null)]
    public void Dispatch_preserves_only_the_explicit_authenticated_principal(string? principal, string? expected)
    {
        var snapshot = Launcher(Config()).CaptureDispatch("copilot", principal);

        Assert.Equal(expected, snapshot.AuthenticatedPrincipal);
        Assert.Empty(snapshot.ExtraArguments);
        Assert.Empty(logged);
        Assert.Empty(starter.Specs);
    }

    [Fact]
    public async Task Capturing_dispatch_does_not_change_launch_results_replay_or_audit_lines()
    {
        var launcher = Launcher(Config());
        var request = new LaunchRequest
        {
            Request = Guid.NewGuid().ToString("D"), Agent = "copilot", Workspace = "App", Prompt = "go",
        };
        var audit = new List<string>();
        Action<string>? Prepare(Guid key)
        {
            audit.Add($"prepare {key:D}");
            return audit.Add;
        }

        launcher.CaptureDispatch("copilot", null);
        Assert.Empty(logged);
        Assert.Empty(starter.Specs);
        Assert.Empty(audit);
        var result = await launcher.LaunchAuditedAsync(request, "fixture", Prepare);
        var lines = logged.ToArray();
        var auditLines = audit.ToArray();
        launcher.CaptureDispatch("copilot", "agent:unrelated");
        var replay = await launcher.LaunchAuditedAsync(request, "fixture", Prepare);

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.Equal(result, replay);
        Assert.Single(starter.Specs);
        Assert.NotEmpty(lines);
        Assert.NotEmpty(auditLines);
        Assert.Equal(lines, logged.ToArray());
        Assert.Equal(auditLines, audit.ToArray());
    }
}
