using AgentBridge.Host.Core.Usage;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;

public sealed partial class LaunchFlowTests
{
    sealed class CompatibilityRunner(string help, string version = "codex-cli 0.140.0") : ICommandRunner
    {
        public Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CommandResult(true, false, 0, arguments[0] == "--help" ? help : version));
    }

    LaunchService CompatibleLauncher(LaunchConfig config, ICommandRunner runner) => new(source, probe, screens, starter, locator, new AgencyCatalog(agency), new LaunchServiceOptions
    {
        CopilotHome = f.CopilotHome, ClaudeHome = f.ClaudeHome, CodexHome = f.CodexHome,
        Config = config, PlatformSupported = true, IsSystemDirectory = _ => false,
        RegistrationWait = TimeSpan.FromSeconds(3), PollInterval = TimeSpan.FromMilliseconds(25),
        MacCompatibility = new MacLaunchCompatibility(runner),
    });

    string CompatibilityExecutable(string name)
    {
        var path = Path.Combine(f.Root, name);
        File.WriteAllText(path, "fixture executable, never run");
        locator.Installed.Clear();
        locator.Installed[name == "codex.cmd" ? "codex" : "copilot"] = path;
        return path;
    }

    [Fact]
    public async Task Managed_0140_launch_reaches_the_starter_with_the_verified_embedded_arguments()
    {
        CompatibilityExecutable("codex.cmd");
        var launcher = CompatibleLauncher(Config(), new CompatibilityRunner("Options:\n  -c, --config <value>\n"));
        var result = await launcher.LaunchAsync(new() { Request = Guid.NewGuid().ToString(), Agent = "codex", Workspace = "App", Prompt = "fixture" }, "phone");
        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        var arguments = Assert.Single(starter.Specs).Arguments;
        Assert.DoesNotContain("--no-daemon", arguments);
        Assert.Equal(new[] { "-c", "model_reasoning_summary=detailed" }, arguments.Take(2));
    }

    [Fact]
    public async Task Unsupported_codex_is_refused_before_a_worktree_or_process_is_created()
    {
        CompatibilityExecutable("codex.cmd");
        var config = Config(c => c with { Workspaces = [new("App", app, true)] });
        var launcher = CompatibleLauncher(config, new CompatibilityRunner("Options:\n", "codex-cli 0.999.0"));
        var result = await launcher.LaunchAsync(new() { Request = Guid.NewGuid().ToString(), Agent = "codex", Workspace = "App", Prompt = "fixture" }, "phone");
        Assert.Equal(LaunchOutcome.Refused, result.Outcome);
        Assert.Contains("cannot be verified", result.Reason);
        Assert.Empty(starter.Specs);
        Assert.False(Directory.Exists(config.WorktreeRoot));
    }

    [Fact]
    public async Task Copilot_unsupported_configured_effort_is_removed_and_explicit_selection_is_refused()
    {
        CompatibilityExecutable("copilot");
        var config = Config(c => c with { Agents = new Dictionary<string, AgentConfig> { ["copilot"] = new() { Effort = "minimal" } } });
        var launcher = CompatibleLauncher(config, new CompatibilityRunner("  --reasoning-effort <level> Set effort (choices: \"none\", \"low\", \"medium\", \"high\", \"xhigh\", \"max\")\n"));
        var agent = Assert.Single((await launcher.OptionsAsync()).Agents);
        Assert.DoesNotContain("minimal", agent.Efforts);
        Assert.Null(agent.Defaults!.Effort);
        var result = await launcher.LaunchAsync(new() { Request = Guid.NewGuid().ToString(), Agent = "copilot", Workspace = "App", Effort = "minimal" }, "phone");
        Assert.Equal(LaunchOutcome.Refused, result.Outcome);
        Assert.Contains("Unknown effort", result.Reason);
        Assert.Empty(starter.Specs);
    }
}
