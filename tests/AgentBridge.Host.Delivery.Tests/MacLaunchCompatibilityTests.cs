using AgentBridge.Host.Core.Usage;
using AgentBridge.Host.Delivery.Launch;
using Xunit;

public sealed class MacLaunchCompatibilityTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("ab-cli-help-").FullName;
    string Executable(string name = "codex")
    {
        var file = Path.Combine(root, name);
        File.WriteAllText(file, "fixture executable");
        return file;
    }
    public void Dispose() => Directory.Delete(root, true);
    static Launcher Codex => Launchers.Find("codex")!;
    static Launcher Copilot => Launchers.Find("copilot")!;

    sealed class Runner(string help, string version = "codex-cli 0.140.0") : ICommandRunner
    {
        public int Calls;
        public Action? During;
        public CommandResult? Failure;
        public Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            During?.Invoke();
            Assert.Single(arguments);
            Assert.Contains(arguments[0], new[] { "--help", "--version" });
            Assert.Equal(TimeSpan.FromSeconds(3), timeout);
            return Task.FromResult(Failure ?? new(true, false, 0, arguments[0] == "--help" ? help : version));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Verified_0140_omits_only_the_unsupported_flag_and_keeps_the_embedded_mode_override(bool resume)
    {
        var runner = new Runner("Options:\n  -c, --config <key=value>\n");
        var value = await new MacLaunchCompatibility(runner).InspectAsync(Codex, Executable(), [], default);
        Assert.Null(value.Refusal);
        Assert.False(value.CodexNoDaemon);
        var args = Launchers.Arguments(Codex, "S", resume, "model", "high", "400000", true, [], "", "go", value.CodexNoDaemon);
        Assert.DoesNotContain("--no-daemon", args);
        Assert.Equal(new[] { "-c", "model_reasoning_summary=detailed" }, args.Take(2));
        Assert.Contains("model_reasoning_effort=high", args);
        Assert.Equal(resume, args.Contains("resume"));
        Assert.Equal(new[] { "--", "go" }, args.TakeLast(2));
        Assert.Equal(2, runner.Calls);
    }

    [Theory]
    [InlineData("codex-cli 0.139.0")]
    [InlineData("codex-cli 0.140.1")]
    [InlineData("codex-cli 0.140.0-beta")]
    [InlineData("codex-cli 0.140.0\nextra")]
    public async Task Unknown_versions_without_no_daemon_are_refused(string version)
    {
        var value = await new MacLaunchCompatibility(new Runner("Options:\n", version)).InspectAsync(Codex, Executable(), [], default);
        Assert.Contains("cannot be verified", value.Refusal);
    }

    [Fact]
    public async Task A_modern_advertised_flag_is_retained_without_guessing_from_the_version()
    {
        var runner = new Runner("Options:\n      --no-daemon\n          Run in this process\n", "future");
        var value = await new MacLaunchCompatibility(runner).InspectAsync(Codex, Executable(), [], default);
        Assert.Null(value.Refusal);
        Assert.True(value.CodexNoDaemon);
        Assert.Single(Launchers.Arguments(Codex, "", false, "", "", "", false, [], "", "go"), x => x == "--no-daemon");
        Assert.Equal(1, runner.Calls);
    }

    [Theory]
    [InlineData("--remote=https://fixture.invalid")]
    [InlineData("--remote")]
    [InlineData("remote-control")]
    [InlineData("exec")]
    [InlineData("--")]
    [InlineData("--profile=custom")]
    public async Task Extras_cannot_redirect_any_managed_codex_launch(string extra)
    {
        var runner = new Runner("Options:\n  --no-daemon\n");
        var value = await new MacLaunchCompatibility(runner).InspectAsync(Codex, Executable(), [extra], default);
        Assert.Contains("extraArgs", value.Refusal);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Mentioning_a_flag_in_prose_does_not_advertise_support()
    {
        var value = await new MacLaunchCompatibility(new Runner("Use a newer release with --no-daemon.\n", "unknown")).InspectAsync(Codex, Executable(), [], default);
        Assert.NotNull(value.Refusal);
    }

    [Fact]
    public async Task Copilot_choices_follow_wrapped_help_instead_of_the_generic_fallback_table()
    {
        var help = "  --effort, --reasoning-effort <level>  Set the reasoning effort level (choices:\n" +
            "       \"none\", \"low\", \"medium\", \"high\",\n       \"xhigh\", \"max\")\n  --next  Another flag\n";
        var value = await new MacLaunchCompatibility(new Runner(help)).InspectAsync(Copilot, Executable("copilot"), [], default);
        Assert.Null(value.Refusal);
        Assert.Equal(new[] { "none", "low", "medium", "high", "xhigh", "max" }, value.Efforts);
        Assert.DoesNotContain("minimal", value.Efforts!);
    }

    [Fact]
    public async Task Copilot_native_help_reports_unquoted_possible_values()
    {
        var help = "      --reasoning-effort <level>\n" +
            "          Set the reasoning effort level [possible values: none, minimal, low, medium, high, xhigh, max]\n";
        var value = await new MacLaunchCompatibility(new Runner(help)).InspectAsync(Copilot, Executable("copilot"), [], default);
        Assert.Null(value.Refusal);
        Assert.Equal(new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max" }, value.Efforts);
    }

    [Theory]
    [InlineData("  --reasoning-effort <level>\n  --context <tier> [possible values: low, high]")]
    [InlineData("  --reasoning-effort <level> [possible values: low, high --remote]")]
    [InlineData("  --reasoning-effort <level> [possible values: low, , high]")]
    public async Task Possible_values_from_another_flag_or_malformed_values_do_not_authorize_a_launch(string help)
    {
        var value = await new MacLaunchCompatibility(new Runner(help)).InspectAsync(Copilot, Executable("copilot"), [], default);
        Assert.NotNull(value.Refusal);
    }

    [Fact]
    public async Task Missing_choices_and_failed_or_oversized_probes_refuse_without_exposing_output()
    {
        var file = Executable();
        var missing = await new MacLaunchCompatibility(new Runner("unrecognized private output")).InspectAsync(Copilot, file, [], default);
        Assert.NotNull(missing.Refusal);
        var failed = await new MacLaunchCompatibility(new Runner("") { Failure = new(true, true, 1, "private output") }).InspectAsync(Codex, file, [], default);
        Assert.NotNull(failed.Refusal);
        Assert.DoesNotContain("private output", failed.Refusal);
        var huge = await new MacLaunchCompatibility(new Runner(new string('x', BoundedHelpRunner.MaxCharacters + 1))).InspectAsync(Codex, file, [], default);
        Assert.NotNull(huge.Refusal);
    }

    [Fact]
    public async Task Changing_the_executable_invalidates_cached_capabilities_and_launch_validation()
    {
        var file = Executable();
        var runner = new Runner("Options:\n  --no-daemon\n");
        var policy = new MacLaunchCompatibility(runner);
        var first = await policy.InspectAsync(Codex, file, [], default);
        Assert.Equal(first, await policy.InspectAsync(Codex, file, [], default));
        Assert.Equal(1, runner.Calls);
        File.AppendAllText(file, "changed");
        Assert.False(MacLaunchCompatibility.IsCurrent(file, first));
        var second = await policy.InspectAsync(Codex, file, [], default);
        Assert.True(MacLaunchCompatibility.IsCurrent(file, second));
        Assert.Equal(2, runner.Calls);
    }

    [Fact]
    public async Task A_binary_changed_during_the_probe_or_a_cancelled_probe_never_gets_approved()
    {
        var file = Executable();
        var runner = new Runner("Options:\n  --no-daemon\n") { During = () => File.AppendAllText(file, "changed") };
        Assert.NotNull((await new MacLaunchCompatibility(runner).InspectAsync(Codex, file, [], default)).Refusal);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MacLaunchCompatibility(runner).InspectAsync(Codex, file, [], cancellation.Token));
    }

    [Fact]
    public async Task Bounded_help_probes_reject_a_flood_and_terminate_a_hung_fixture()
    {
        if (OperatingSystem.IsWindows()) { return; }
        var runner = new BoundedHelpRunner();
        var flood = await runner.RunAsync("/bin/sh", ["-c", "while :; do printf '0123456789012345678901234567890123456789012345678901234567890123456789'; done"], TimeSpan.FromSeconds(2));
        Assert.NotEqual(0, flood.ExitCode);
        Assert.Empty(flood.Output);
        var hung = await runner.RunAsync("/bin/sh", ["-c", "sleep 10"], TimeSpan.FromMilliseconds(100));
        Assert.True(hung.TimedOut);
        Assert.Empty(hung.Output);
        var okay = await runner.RunAsync("/bin/sh", ["-c", "printf fixture"], TimeSpan.FromSeconds(2));
        Assert.Equal("fixture", okay.Output);
        Assert.Equal(0, okay.ExitCode);
    }
}
