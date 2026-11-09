using System.Collections.Concurrent;
using System.Text.Json;
using AgentBridge.Hooks;
using AgentBridge.Host;
using AgentBridge.Host.Core.Intake;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Local;
using Xunit;

public sealed class AgentReadinessTests : IDisposable
{
    readonly Fixture f = new();
    readonly Locator locator = new();
    readonly Commands commands = new();
    string? readOnly;

    sealed class Locator : IAgentLocator
    {
        public ConcurrentDictionary<string, string> Installed { get; } = new();
        public ConcurrentQueue<(string Id, AgentConfig Config)> Asked { get; } = new();
        public string? Find(Launcher launcher, AgentConfig config)
        {
            Asked.Enqueue((launcher.Id, config));
            return Installed.TryGetValue(launcher.Id, out var path) ? path : null;
        }
    }

    sealed class Commands : ICommandRunner
    {
        public ConcurrentQueue<(string Path, IReadOnlyList<string> Args)> Calls { get; } = new();
        public Func<CancellationToken, Task<CommandResult>> Run { get; set; } = _ => Task.FromResult(new CommandResult(true, false, 0, "test-cli 1.2.3"));
        public Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue((program, arguments));
            return Run(cancellationToken);
        }
    }

    string Profile => Path.Combine(f.Root, "profile");
    IntakeState.AgentHomes Homes => new(f.CopilotHome, f.ClaudeHome, f.CodexHome);
    AgentReadinessService Service(LaunchConfig? config = null, HookIntake? intake = null, TimeSpan? timeout = null) =>
        new(Profile, Homes, config ?? LaunchConfig.Default, locator, commands, () => readOnly, intake, f.Time)
        {
            VersionTimeout = timeout ?? TimeSpan.FromSeconds(3),
        };
    static AgentReadiness Agent(AgentReadinessReport report, string id) => report.Agents.Single(a => a.Id == id);
    public void Dispose() => f.Dispose();

    [Fact]
    public async Task A_newly_installed_agent_is_found_with_the_same_launcher_configuration_on_the_next_check()
    {
        var configured = new AgentConfig { Path = Path.Combine(f.Root, "custom-claude.exe") };
        await using var service = Service(LaunchConfig.Default with { Agents = new Dictionary<string, AgentConfig> { ["claude"] = configured } });
        Assert.All((await service.RefreshAsync()).Agents, a => Assert.Equal(ReadinessState.Missing, a.Installation.State));
        Assert.Empty(commands.Calls);

        locator.Installed["claude"] = configured.Path!;
        var found = Agent(await service.RefreshAsync(), "claude");

        Assert.Equal(configured.Path, found.Executable);
        Assert.Equal("1.2.3", found.Version);
        Assert.Equal(ReadinessState.Working, found.Installation.State);
        Assert.Equal(ReadinessState.NeedsAction, found.Hooks.State);
        Assert.Equal(ReadinessState.Unknown, found.Authentication.State);
        Assert.Equal(ReadinessState.Unknown, found.Activation.State);
        Assert.All(locator.Asked.Where(a => a.Id == "claude"), a => Assert.Same(configured, a.Config));
        Assert.Equal(["--version"], Assert.Single(commands.Calls).Args);
        Assert.False(Directory.Exists(Profile));
    }

    [Fact]
    public async Task A_read_only_host_detects_executables_but_runs_no_agent_and_offers_no_hook_setup()
    {
        readOnly = "agent-ha-bridge is running";
        locator.Installed["agency"] = "agency.cmd";
        await using var service = Service();

        var report = await service.RefreshAsync();

        Assert.Equal(readOnly, report.ReadOnlyReason);
        Assert.Equal(ReadinessState.Working, Agent(report, "agency").Installation.State);
        Assert.Equal(ReadinessState.Blocked, Agent(report, "agency").VersionCheck.State);
        Assert.All(report.Agents, a => Assert.Equal(ReadinessState.Blocked, a.Hooks.State));
        Assert.Empty(commands.Calls);
    }

    [Theory]
    [InlineData(false, false, -1, "", "could not start")]
    [InlineData(true, true, -1, "", "timed out")]
    [InlineData(true, false, 7, "token-like output must not be shown", "code 7")]
    [InlineData(true, false, 0, "unrecognized output", "not recognized")]
    public async Task Version_failures_remain_actionable_without_hiding_that_the_executable_was_found(bool ran, bool timedOut, int exit, string output, string detail)
    {
        locator.Installed["codex"] = "codex.cmd";
        commands.Run = _ => Task.FromResult(new CommandResult(ran, timedOut, exit, output));
        await using var service = Service();

        var agent = Agent(await service.RefreshAsync(), "codex");

        Assert.Equal(ReadinessState.Working, agent.Installation.State);
        Assert.Null(agent.Version);
        Assert.Equal(ReadinessState.NeedsAction, agent.VersionCheck.State);
        Assert.Contains(detail, agent.VersionCheck.Detail);
        Assert.DoesNotContain("token-like", agent.VersionCheck.Detail);
    }

    [Fact]
    public async Task An_owned_record_and_enabled_intake_are_evidence_not_a_claim_of_verified_hooks_or_sign_in()
    {
        new IntakeState(f.Runtime, Path.Combine(f.Root, "bridge"), "test", f.Time.GetUtcNow()) { Homes = Homes }.Save(Profile);
        Directory.CreateDirectory(f.ClaudeHome);
        var marker = Path.Combine(f.ClaudeHome, "agent-bridge-owner.json");
        var original = JsonSerializer.Serialize(new { version = 1, profile = Profile, setupCompleted = true });
        File.WriteAllText(marker, original);
        await using var service = Service();

        var agent = Agent(await service.RefreshAsync(), "claude");

        Assert.Equal(HookSetupState.Completed, agent.HookSetup);
        Assert.Equal(ReadinessState.Unknown, agent.Hooks.State);
        Assert.Contains("owned setup record", agent.Hooks.Detail);
        Assert.Null(agent.LastHookHandledAt);
        Assert.Equal(ReadinessState.Unknown, agent.Authentication.State);
        Assert.Equal(ReadinessState.Unknown, agent.Activation.State);
        Assert.Equal(original, File.ReadAllText(marker));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task An_owned_record_without_completed_setup_stays_incomplete_and_repairable_on_every_check(bool intake, bool explicitFalse)
    {
        if (intake) { new IntakeState(f.Runtime, Path.Combine(f.Root, "bridge"), "test", f.Time.GetUtcNow()) { Homes = Homes }.Save(Profile); }
        Directory.CreateDirectory(f.CodexHome);
        var marker = Path.Combine(f.CodexHome, "agent-bridge-owner.json");
        // Records written before setupCompleted existed are never migrated to completed on read.
        var original = explicitFalse ? JsonSerializer.Serialize(new { version = 1, profile = Profile, setupCompleted = false })
            : JsonSerializer.Serialize(new { version = 1, profile = Profile });
        File.WriteAllText(marker, original);
        await using var service = Service();

        foreach (var report in new[] { await service.RefreshAsync(), await service.RefreshAsync(fresh: true) })
        {
            var agent = Agent(report, "codex");
            Assert.Equal(HookSetupState.Incomplete, agent.HookSetup);
            Assert.Equal(intake ? ReadinessState.Failed : ReadinessState.NeedsAction, agent.Hooks.State);
            Assert.Contains("did not complete", agent.Hooks.Detail);
            Assert.DoesNotContain("owned setup record", agent.Hooks.Detail);
        }
        Assert.Equal(original, File.ReadAllText(marker));
    }

    [Fact]
    public async Task Without_an_ownership_record_setup_is_not_set_up_and_foreign_or_malformed_completion_is_not_trusted()
    {
        Directory.CreateDirectory(f.CodexHome);
        File.WriteAllText(Path.Combine(f.CodexHome, "agent-bridge-owner.json"), JsonSerializer.Serialize(new { version = 1, profile = Profile, setupCompleted = "yes" }));
        Directory.CreateDirectory(f.ClaudeHome);
        File.WriteAllText(Path.Combine(f.ClaudeHome, "agent-bridge-owner.json"),
            JsonSerializer.Serialize(new { version = 1, profile = Path.Combine(f.Root, "other-profile"), setupCompleted = true }));
        await using var service = Service();

        var report = await service.RefreshAsync();

        Assert.Equal(HookSetupState.NotSetUp, Agent(report, "copilot").HookSetup);
        Assert.Equal((ReadinessState.Failed, HookSetupState.Unknown), (Agent(report, "codex").Hooks.State, Agent(report, "codex").HookSetup));
        Assert.Equal((ReadinessState.Blocked, HookSetupState.Unknown), (Agent(report, "claude").Hooks.State, Agent(report, "claude").HookSetup));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Foreign_or_broken_ownership_is_reported_instead_of_offering_to_overwrite_it(bool broken)
    {
        Directory.CreateDirectory(f.CodexHome);
        File.WriteAllText(Path.Combine(f.CodexHome, "agent-bridge-owner.json"), broken ? "{broken"
            : JsonSerializer.Serialize(new { version = 1, profile = Path.Combine(f.Root, "other-profile") }));
        await using var service = Service();

        var check = Agent(await service.RefreshAsync(), "codex").Hooks;

        Assert.Equal(broken ? ReadinessState.Failed : ReadinessState.Blocked, check.State);
    }

    [Fact]
    public async Task An_unreadable_intake_record_is_a_failure_not_an_unconfigured_fresh_install()
    {
        Directory.CreateDirectory(Profile);
        File.WriteAllText(Path.Combine(Profile, IntakeState.FileName), "{broken");
        await using var service = Service();

        Assert.All((await service.RefreshAsync()).Agents, a => Assert.Equal(ReadinessState.Failed, a.Hooks.State));
    }

    [Fact]
    public async Task A_version_IO_error_keeps_the_executable_detection_and_explicitly_fails_the_probe()
    {
        locator.Installed["claude"] = "claude.exe";
        commands.Run = _ => throw new IOException("version output could not be read");
        await using var service = Service();

        var agent = Agent(await service.RefreshAsync(), "claude");

        Assert.Equal(ReadinessState.Working, agent.Installation.State);
        Assert.Equal(ReadinessState.Failed, agent.VersionCheck.State);
        Assert.Contains("version output could not be read", agent.VersionCheck.Detail);
    }

    [Fact]
    public async Task Cancelling_a_scan_stops_its_probe_without_publishing_a_partial_report_and_allows_retry()
    {
        locator.Installed["claude"] = "claude.exe";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        commands.Run = async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new CommandResult(true, false, 0, "1.2.3");
        };
        await using var service = Service(timeout: TimeSpan.FromMinutes(1));
        using var cancel = new CancellationTokenSource();

        var pending = service.RefreshAsync(cancel.Token);
        await TestTimeoutDiagnostics.WaitAsync(entered.Task, TimeSpan.FromSeconds(30));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(service.Snapshot);

        commands.Run = _ => Task.FromResult(new CommandResult(true, false, 0, "1.2.3"));
        Assert.Equal("1.2.3", Agent(await service.RefreshAsync(), "claude").Version);
    }

    [Fact]
    public async Task Concurrent_refreshes_share_the_same_bounded_version_probe()
    {
        locator.Installed["claude"] = "claude.exe";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        commands.Run = async ct => { entered.TrySetResult(); return await release.Task.WaitAsync(ct); };
        // The probe ends when the test releases it, never by the clock: with the usual three
        // seconds, a loaded Windows runner (#553) reached the timeout before the second refresh
        // had started, and both reports read as timed out.
        await using var service = Service(timeout: TimeSpan.FromMinutes(1));

        var first = service.RefreshAsync();
        await TestTimeoutDiagnostics.WaitAsync(entered.Task, TimeSpan.FromSeconds(30));
        var second = service.RefreshAsync();
        release.SetResult(new CommandResult(true, false, 0, "2.3.4"));
        var reports = await TestTimeoutDiagnostics.WaitAsync(Task.WhenAll(first, second), TimeSpan.FromSeconds(30));

        Assert.Single(commands.Calls);
        Assert.All(reports, r => Assert.Equal("2.3.4", Agent(r, "claude").Version));
    }

    [Fact]
    public async Task A_fresh_check_after_setup_does_not_reuse_a_scan_that_started_before_the_mutation()
    {
        locator.Installed["claude"] = "claude.exe";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        commands.Run = async ct => { entered.TrySetResult(); return await release.Task.WaitAsync(ct); };
        await using var service = Service(timeout: TimeSpan.FromMinutes(1));

        var beforeSetup = service.RefreshAsync();
        await TestTimeoutDiagnostics.WaitAsync(entered.Task, TimeSpan.FromSeconds(30));
        Task<AgentReadinessReport> afterSetup;
        Task<AgentReadinessReport> overlappingCheck;
        try
        {
            new IntakeState(f.Runtime, Path.Combine(f.Root, "bridge"), "test", f.Time.GetUtcNow()) { Homes = Homes }.Save(Profile);
            Directory.CreateDirectory(f.ClaudeHome);
            File.WriteAllText(Path.Combine(f.ClaudeHome, "agent-bridge-owner.json"), JsonSerializer.Serialize(new { version = 1, profile = Profile, setupCompleted = true }));
            afterSetup = service.RefreshAsync(fresh: true);
            overlappingCheck = service.RefreshAsync(fresh: true);
        }
        finally { release.TrySetResult(new CommandResult(true, false, 0, "1.2.3")); }
        var reports = await TestTimeoutDiagnostics.WaitAsync(Task.WhenAll(beforeSetup, afterSetup, overlappingCheck), TimeSpan.FromSeconds(30));

        Assert.Contains("owned setup record", Agent(reports[1], "claude").Hooks.Detail);
        Assert.Contains("owned setup record", Agent(reports[2], "claude").Hooks.Detail);
        Assert.Equal(2, commands.Calls.Count);
    }

    [Fact]
    public async Task A_runner_that_ignores_its_timeout_cannot_hold_a_scan_forever()
    {
        locator.Installed["claude"] = "claude.exe";
        var release = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        commands.Run = _ => release.Task;
        await using var service = Service(timeout: TimeSpan.FromMilliseconds(50));
        try
        {
            // The probe still times out at 50 ms, then has two seconds to clean up. This
            // outer bound catches a hang, not a loaded runner's scheduling delay (#664).
            var agent = Agent(await TestTimeoutDiagnostics.WaitAsync(service.RefreshAsync(), TimeSpan.FromSeconds(30)), "claude");
            Assert.Equal(ReadinessState.Failed, agent.VersionCheck.State);
            Assert.Contains("timed out", agent.VersionCheck.Detail);
            Assert.Contains("without confirming cleanup", agent.VersionCheck.Detail);
        }
        finally { release.TrySetResult(new CommandResult(true, false, 0, "1.2.3")); }
    }

    [Fact]
    public async Task Shutdown_cancels_an_active_scan_even_when_its_runner_ignores_cancellation()
    {
        locator.Installed["claude"] = "claude.exe";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        commands.Run = _ => { entered.TrySetResult(); return release.Task; };
        // Only shutdown ends the probe here, never its own timeout: with the usual three seconds,
        // a test stalled that long before shutting down would see the scan finish with a report
        // instead. Shutdown still waits out the two seconds the service gives a cancelled probe
        // to clean up, so the bounds below only stop a real hang: five seconds left three for
        // everything else, and a loaded windows-11-arm runner took longer (#638).
        var service = Service(timeout: TimeSpan.FromMinutes(1));
        var pending = service.RefreshAsync();
        await TestTimeoutDiagnostics.WaitAsync(entered.Task, TimeSpan.FromSeconds(30));
        var disposing = service.DisposeAsync().AsTask();
        try
        {
            await TestTimeoutDiagnostics.WaitAsync(disposing, TimeSpan.FromSeconds(30));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Null(service.Snapshot);
        }
        finally
        {
            release.TrySetResult(new CommandResult(true, false, 0, "1.2.3"));
            await disposing;
        }
    }

    [Fact]
    public async Task Starting_twice_does_not_create_another_polling_loop()
    {
        await using var service = Service();
        service.Start();
        service.Start();

        Assert.Single(locator.Asked, a => a.Id == "agency");
    }

    [Fact]
    public async Task A_legacy_bridge_starting_during_a_scan_blocks_the_remaining_probes()
    {
        foreach (var launcher in Launchers.All) { locator.Installed[launcher.Id] = launcher.Command; }
        commands.Run = _ =>
        {
            readOnly = "agent-ha-bridge started during detection";
            return Task.FromResult(new CommandResult(true, false, 0, "1.2.3"));
        };
        await using var service = Service();

        var report = await service.RefreshAsync();

        Assert.Single(commands.Calls);
        Assert.Equal(readOnly, report.ReadOnlyReason);
        Assert.Equal(ReadinessState.Working, report.Agents[0].VersionCheck.State);
        Assert.All(report.Agents.Skip(1), a => Assert.Equal(ReadinessState.Blocked, a.VersionCheck.State));
    }

    [Fact]
    public async Task Periodic_detection_finds_later_installs_and_actual_intake_events_are_reported_separately()
    {
        await using var intake = new HookIntake(new HookIntakeOptions
        {
            RuntimeRoot = f.Runtime,
            ClaudeHome = f.ClaudeHome,
            UseFileSystemWatcher = false,
        }, f.Processes, f.Time);
        await using var service = Service(intake: intake);
        var initiallyChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterInstalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookHeard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += () =>
        {
            if (service.Snapshot is not { } report) { return; }
            initiallyChecked.TrySetResult();
            if (Agent(report, "copilot").Executable is not null) { laterInstalled.TrySetResult(); }
            if (Agent(report, "copilot").LastHookHandledAt is not null) { hookHeard.TrySetResult(); }
        };
        service.Start();
        await TestTimeoutDiagnostics.WaitAsync(initiallyChecked.Task, TimeSpan.FromSeconds(30));
        locator.Installed["copilot"] = "copilot.exe";
        f.Time.Advance(TimeSpan.FromMinutes(10));
        await TestTimeoutDiagnostics.WaitAsync(laterInstalled.Task, TimeSpan.FromSeconds(30));

        SpoolContract.Write(f.Runtime, "copilot", "agent_stop", "{}"u8, [], f.Time.GetUtcNow(), 1);
        Assert.Equal(1, intake.Pass());
        await TestTimeoutDiagnostics.WaitAsync(hookHeard.Task, TimeSpan.FromSeconds(30));

        var copilot = Agent(service.Snapshot!, "copilot");
        Assert.Equal(f.Time.GetUtcNow(), copilot.LastHookHandledAt);
        Assert.Equal(copilot.LastHookHandledAt, Agent(service.Snapshot!, "agency").LastHookHandledAt);
        Assert.Null(Agent(service.Snapshot!, "claude").LastHookHandledAt);
        Assert.Equal(ReadinessState.Unknown, copilot.Activation.State);
    }
}
