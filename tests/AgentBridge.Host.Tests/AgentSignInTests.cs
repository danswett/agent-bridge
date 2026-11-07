using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Intake;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Host.Delivery.Launch;
using Xunit;

public sealed class AgentSignInTests : IDisposable
{
    readonly Fixture fixture = new();
    readonly Locator locator = new();
    readonly Starter starter = new();
    readonly Commands commands = new();
    int refreshes;

    AgentCliSignInService Service(Action<string>? preflight = null, CancellationToken stopping = default,
        bool? explicitClaude = null, bool defaultClaudeHome = false)
    {
        var roots = fixture.Options();
        return new(fixture.Root, new IntakeState.AgentHomes(roots.CopilotHome,
            defaultClaudeHome ? SessionSourceOptions.ForCurrentUser().ClaudeHome : roots.ClaudeHome, roots.CodexHome)
        {
            ClaudeConfigDirExplicit = explicitClaude,
        }, locator, () => refreshes++, starter, commands, preflight ?? (_ => { }), stopping);
    }

    public void Dispose() => fixture.Dispose();

    [Theory]
    [InlineData("copilot", "login")]
    [InlineData("claude", "auth", "login")]
    [InlineData("codex", "login")]
    public async Task Supported_sign_in_opens_only_the_CLI_command_without_claiming_authentication(string agent, params string[] arguments)
    {
        var result = await Service().OpenAsync(new(agent, true));

        Assert.True(result.Opened, result.Detail);
        var spec = Assert.Single(starter.Specs);
        Assert.Equal("fixture-cli.exe", spec.Executable);
        Assert.Equal(arguments, spec.Arguments);
        Assert.Equal(new[] { "--help" }, commands.Arguments);
        Assert.Equal(1, refreshes);
        Assert.Equal(locator.ChildPath(), spec.Environment!["PATH"]);
        Assert.Equal(fixture.Options().CopilotHome, spec.Environment["COPILOT_HOME"]);
        Assert.Equal(fixture.Options().CodexHome, spec.Environment["CODEX_HOME"]);
        Assert.False(spec.Environment.ContainsKey(Launchers.SessionVariable));
        Assert.Equal(Path.Combine(fixture.Root, "cli-sign-in"), spec.WorkingDirectory);
        Assert.Contains("does not verify", result.Detail);
        Assert.DoesNotContain("private", result.Detail);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(null, false)]
    public async Task Sign_in_preserves_Claudes_recorded_account_directory_mode(bool? explicitMode, bool defaultHome)
    {
        await Service(explicitClaude: explicitMode, defaultClaudeHome: defaultHome).OpenAsync(new("claude", true));
        var environment = Assert.Single(starter.Specs).Environment!;
        Assert.True(environment.ContainsKey("CLAUDE_CONFIG_DIR"));
        Assert.Equal(explicitMode == false ? null : defaultHome
            ? SessionSourceOptions.ForCurrentUser().ClaudeHome : fixture.Options().ClaudeHome,
            environment["CLAUDE_CONFIG_DIR"]);
    }

    [Theory]
    [InlineData("agency", true)]
    [InlineData("unknown", true)]
    [InlineData("copilot", false)]
    public async Task Unsupported_agents_and_absent_confirmation_cannot_probe_or_start_a_CLI(string agent, bool confirmed)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Service().OpenAsync(new(agent, confirmed)));
        Assert.Equal(0, refreshes);
        Assert.Equal(0, commands.Calls);
        Assert.Empty(starter.Specs);
    }

    [Fact]
    public async Task Missing_executables_or_an_unavailable_desktop_never_start_sign_in()
    {
        locator.Executable = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service().OpenAsync(new("codex", true)));
        locator.Executable = "fixture-cli.exe";
        starter.Problem = "fixture unavailable desktop";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Service().OpenAsync(new("claude", true)));
        Assert.Contains("unavailable desktop", error.Message);
        Assert.Equal(0, commands.Calls);
        Assert.Empty(starter.Specs);
    }

    [Theory]
    [InlineData(false, false, -1)]
    [InlineData(true, true, -1)]
    [InlineData(true, false, 1)]
    [InlineData(true, false, 0)]
    public async Task Failed_help_or_an_unsupported_login_command_never_becomes_a_conversation(bool ran, bool timedOut, int exit)
    {
        commands.Result = new(ran, timedOut, exit, "private output without a supported command");
        var result = await Service().OpenAsync(new("claude", true));
        Assert.False(result.Opened);
        Assert.DoesNotContain("private", result.Detail);
        Assert.Empty(starter.Specs);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Read_only_or_ownership_preflight_blocks_both_before_and_after_the_probe(int blockAt)
    {
        var calls = 0;
        var service = Service(_ =>
        {
            if (++calls == blockAt) { throw new InvalidOperationException("fixture now read-only"); }
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.OpenAsync(new("claude", true)));
        Assert.Equal(blockAt, calls);
        Assert.Equal(blockAt - 1, commands.Calls);
        Assert.Empty(starter.Specs);
    }

    [Fact]
    public async Task A_second_open_is_rejected_while_the_first_support_probe_is_pending()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        commands.Run = async ct =>
        {
            entered.SetResult();
            return await released.Task.WaitAsync(ct);
        };
        var service = Service();
        var first = service.OpenAsync(new("claude", true));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.OpenAsync(new("codex", true)));
            Assert.Empty(starter.Specs);
            Assert.Equal(1, commands.Calls);
        }
        finally { released.TrySetResult(commands.Result); }
        Assert.True((await first).Opened);
        Assert.Single(starter.Specs);
    }

    [Fact]
    public async Task An_existing_window_is_reused_and_a_closed_one_can_be_reopened()
    {
        var service = Service();
        await service.OpenAsync(new("claude", true));
        var reused = await service.OpenAsync(new("claude", true));
        Assert.True(reused.Opened);
        Assert.Contains("already open", reused.Detail);
        Assert.Single(starter.Specs);
        Assert.Equal(1, commands.Calls);
        starter.IsRunning = false;
        await service.OpenAsync(new("claude", true));
        Assert.Equal(2, starter.Specs.Count);
    }

    [Fact]
    public async Task Shutdown_during_support_detection_never_opens_a_window()
    {
        using var stop = new CancellationTokenSource();
        commands.OnRun = stop.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(stopping: stop.Token).OpenAsync(new("copilot", true)));
        Assert.Empty(starter.Specs);
    }

    sealed class Locator : IAgentLocator
    {
        public string? Executable = "fixture-cli.exe";
        public string? Find(Launcher launcher, AgentConfig config) => Executable;
        public string ChildPath() => Path.Combine(Path.GetTempPath(), "fixture-runtime");
    }

    sealed class Commands : ICommandRunner
    {
        public int Calls;
        public IReadOnlyList<string> Arguments = [];
        public Action? OnRun;
        public Func<CancellationToken, Task<CommandResult>>? Run;
        public CommandResult Result = new(true, false, 0, "Commands:\n  auth  Manage authentication\n  login [options] Sign in\nprivate output");
        public Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls++;
            Arguments = arguments;
            Assert.Equal(TimeSpan.FromSeconds(5), timeout);
            OnRun?.Invoke();
            return Run is { } run ? run(cancellationToken) : Task.FromResult(Result);
        }
    }

    public sealed class Starter : IAgentStarter
    {
        public string? Problem;
        public bool IsRunning = true;
        public List<StartSpec> Specs = [];
        public string? Unavailable() => Problem;
        public string? CommandLine(StartSpec spec, out string? problem) { problem = null; return "fixture command"; }
        public Started Start(StartSpec spec) { Specs.Add(spec); return new(Specs.Count, DateTimeOffset.UtcNow); }
        public bool Running(Started started) => IsRunning;
    }
}
