using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// The Settings page over a host on a real pipe: signing in, the host's settings, the switches
// that install things, restarting and updating. Task Scheduler, processes, the Run entry and
// Velopack are fakes.
[Trait("Category", "Network")]
public sealed class SettingsTests : IAsyncLifetime
{
    readonly FakeHost host = new();
    readonly FakeTasks tasks = new();
    readonly FakeProcesses processes = new();
    readonly Updater updater = new();
    readonly AtSignIn atSignIn = new();
    readonly LockedDispatcher ui = new();
    readonly string app = Path.Combine(Path.GetTempPath(), "abs-" + Guid.NewGuid().ToString("N")[..8]);
    AppPreferences prefs = new();
    HostConnection connection = null!;
    SettingsViewModel settings = null!;
    HostManager manager = null!;

    sealed class Updater : IUpdater
    {
        public Action? OnApply;
        public Exception? CheckFails;
        public Exception? ApplyFails;
        public Exception? DownloadFails;
        public bool Applied;
        public string? Found = "0.2.0";
        public string? Ready { get; set; }
        public string? InstalledVersion => "0.1.0";
        public Task<string?> CheckAsync() => CheckFails is { } e ? Task.FromException<string?>(e) : Task.FromResult(Found);
        // The percentages a download reports, as Velopack's do; by default only its end.
        public int[] Steps = [100];
        public Task DownloadAsync(Action<int> progress)
        {
            if (DownloadFails is { } e) { return Task.FromException(e); }
            foreach (var p in Steps) { progress(p); }
            Ready = "0.2.0";
            return Task.CompletedTask;
        }
        public void ApplyAndRestart(string version)
        {
            OnApply?.Invoke();
            if (ApplyFails is { } e) { throw e; }
            if (Ready != version) { throw new InvalidOperationException($"{version} is no longer the update downloaded"); }
            Applied = true;
        }
    }

    sealed class AtSignIn : IAppAtSignIn
    {
        public List<bool> Sets { get; } = [];
        public bool Enabled { get; set; }
        public void Set(bool on) { Sets.Add(on); Enabled = on; }
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, OperatingSystem.IsWindows() ? "agent-bridge-host.exe" : "agent-bridge-host"), "");
        File.WriteAllText(Path.Combine(app, OperatingSystem.IsWindows() ? "agent-bridge-hostw.exe" : "agent-bridge-hostw"), "");
        // A host asked to stop goes, as the real one does.
        host.StopsWhenAsked();
        host.Capabilities.Add(HostOps.AgentSetupCapability);
        host.Capabilities.Add(HostOps.AgentInstallCapability);
        host.Start();
        connection = host.Connect(ui);
        // Start and stop timeouts on a clock that never moves, as in AutoUpdaterTests: they wait
        // for the fake host however slow the runner (#425, #909). No test here needs one to run out.
        manager = new HostManager(new InstallLayout(app, host.Folder, "test"), tasks, processes,
            ct => LocalClient.ConnectAsync(host.Endpoint, TimeSpan.FromMilliseconds(300), ct), userId: @"PC\dana", userSid: "S-1-5-21-1-2-3-1001")
        {
            StartTimeout = TimeSpan.FromSeconds(5),
            StopTimeout = TimeSpan.FromSeconds(2),
            Poll = TimeSpan.FromMilliseconds(50),
            // No machine's real processes: the package is what a test says it is.
            PackageHosts = () => [],
            Elevated = () => false,
            Time = new FakeTimeProvider(Make.T0),
        };
        settings = new SettingsViewModel(connection, manager, updater, atSignIn, () => prefs, p => prefs = p, "0.1.0", "https://relay.default");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await connection.DisposeAsync();
        await host.DisposeAsync();
        Directory.Delete(app, recursive: true);
    }

    Task Connected() => Wait.UntilAsync(() => settings.HostRunning && connection.Status is not null, "the connection");

    // The host's status as the test sets it, seen by the app. The app may have read the status before
    // the test set it (on Linux its socket connects at once), so it is sent as the host would.
    async Task ConnectedWith(HostStatus status)
    {
        host.Status = status;
        await Connected();
        await host.SendAsync(HostOps.StatusEvent, status);
        await Wait.UntilAsync(() => Equals(connection.Status?.Settings, status.Settings), "the host's status");
    }

    // As the host does: answers host.settings with what it saved, which its status says from then
    // on when read; its status event is sent only when a test sends it.
    void Saves(Func<System.Text.Json.JsonElement, HostSettingsView> save) => host.On(HostOps.Settings, d =>
    {
        var saved = save(d);
        host.Status = host.Status with { Settings = saved };
        return new SettingsResult(saved, false);
    });

    static AgentReadinessReport Readiness(string version) => new(Make.T0,
    [
        new("claude", "Claude", "fixture-home", "claude.exe", version,
            new(ReadinessState.Working, "Executable found."),
            new(ReadinessState.Working, $"Version {version}"),
            new(ReadinessState.NeedsAction, "Explicit hook setup is required."),
            new(ReadinessState.Unknown, "Sign-in has not been verified."),
            new(ReadinessState.Unknown, "Trust and a new session have not been verified."),
            HookSetup: HookSetupState.NotSetUp),
    ]);

    [Fact]
    public async Task CLI_installation_sends_explicit_consent_and_preserves_the_result_if_the_readiness_recheck_fails()
    {
        await Connected();
        host.On(HostOps.AgentsInstall, d =>
        {
            var request = d.Deserialize<AgentInstallRequest>(ProtocolJson.Options)!;
            Assert.Equal("claude", request.Agent);
            Assert.True(request.AcceptAgreements);
            return new AgentInstallResult(true, "Fixture executable installed; sign-in is still unverified.", "fixture.exe");
        });
        host.On(HostOps.AgentsRefresh, _ => throw new InvalidOperationException("fixture readiness failure"));
        await settings.InstallAgentAsync("claude");
        Assert.Contains("Fixture executable installed", settings.AgentInstallStatus);
        Assert.Contains("fixture readiness failure", settings.Error);
        Assert.False(settings.AgentInstallInProgress);
        Assert.False(settings.Busy);
        Assert.Empty(host.Sent(HostOps.Restart));
        Assert.Empty(host.Sent(HostOps.AgentsSetup));
        Assert.Empty(host.Sent(HostOps.SignIn));
    }

    [Fact]
    public async Task Cancelling_installation_does_not_clear_busy_before_the_installer_returns()
    {
        await Connected();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnAsync(HostOps.AgentsInstall, async _ =>
        {
            entered.SetResult();
            await stopped.Task;
            return new AgentInstallResult(false, "Fixture cancellation completed.");
        });
        host.On(HostOps.AgentsInstallCancel, _ => new { cancellationRequested = true });
        host.On(HostOps.AgentsRefresh, _ => Readiness("1.2.3"));
        var pending = settings.InstallAgentAsync("claude");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(settings.AgentInstallInProgress);
            await settings.CancelAgentInstallAsync();
            Assert.Single(host.Sent(HostOps.AgentsInstallCancel));
            Assert.True(settings.Busy);
            Assert.False(settings.CanInstallAgents);
            Assert.False(pending.IsCompleted);
        }
        finally { stopped.TrySetResult(); }
        await pending;
        Assert.Contains("cancellation completed", settings.AgentInstallStatus);
        Assert.True(settings.HasError);
        Assert.False(settings.Busy);
    }

    [Fact]
    public async Task A_CLI_installation_says_installing_on_its_row_then_why_it_failed_and_offers_install_cli_again()
    {
        await ConnectedWith(FakeHost.StatusOf() with { Agents = new(Make.T0, [Agent("claude", "Claude Code", ReadinessState.NeedsAction, installation: ReadinessState.Missing, hooksDetail: NoRecordIntakeOff)]) });
        await Wait.UntilAsync(() => settings.Agents.Count == 1, "agent readiness");
        Assert.Equal(("Not installed", true), (settings.Agents.Single().Installation, settings.Agents.Single().CanInstall));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const string failed = "Windows Package Manager exited with code 0x8A15000F. Check network access, package policy and required runtime approvals, then check agents again. Partial installations were not removed.";
        host.OnAsync(HostOps.AgentsInstall, async _ =>
        {
            entered.SetResult();
            await finish.Task;
            host.Status = host.Status with { AgentInstallation = new AgentInstallProgress("claude", false, failed) };
            return new AgentInstallResult(false, failed);
        });
        host.On(HostOps.AgentsRefresh, _ => host.Status.Agents);
        var pending = settings.InstallAgentAsync("claude");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var installing = settings.Agents.Single();
            Assert.Equal("Installing…", installing.Installation);
            Assert.True(installing.Installing);
            // Install CLI stays pressed and focusable, with Cancel beside it (ui.md §9.1 A4).
            Assert.True(installing.InstallEnabled);
            Assert.False(installing.CanInstall);
            Assert.Equal("Cancel CLI installation", installing.CancelInstallName);
            // Named for the product, and busy by its item status, never a toggle's pressed state.
            Assert.Equal(("Install CLI for Claude Code", "Installing…"), (installing.InstallName, installing.InstallItemStatus));
        }
        finally { finish.TrySetResult(); }
        await pending;
        Assert.True(settings.HasError);

        await Wait.UntilAsync(() => settings.Agents.Single().Installation.StartsWith("Install failed", StringComparison.Ordinal), "the failure on the row",
            why: () => settings.Agents.Single().Installation);
        var row = settings.Agents.Single();
        Assert.Equal("Install failed · Windows Package Manager couldn't install it", row.Installation);
        Assert.False(row.Installing);
        Assert.True(row.ShowInstall);
        Assert.True(row.CanInstall);
        Assert.Equal("", row.InstallItemStatus);
    }

    [Theory]
    [InlineData("Installer cleanup could not be confirmed. Further CLI installations are blocked in this host. Check Windows installation activity before restarting the host and checking agents; nothing was uninstalled.", "the installer's processes may still be running")]
    [InlineData("Installation cancelled. Windows may already have installed a runtime or CLI; check agents again. Nothing was uninstalled.", "cancelled")]
    [InlineData("The CLI executable is now available. Its version, account, hooks and trust still need separate checks.", null)]
    public void An_installation_says_why_it_failed_from_a_known_set_and_nothing_when_it_did_not(string detail, string? why)
    {
        Assert.Equal(why, AgentWords.InstallFailure(detail));
    }

    [Fact]
    public async Task Another_Settings_action_or_duplicate_install_cannot_hide_cancellation_for_the_active_installer()
    {
        await Connected();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnAsync(HostOps.AgentsInstall, async _ =>
        {
            entered.SetResult();
            await stopped.Task;
            return new AgentInstallResult(false, "Fixture installation stopped.");
        });
        host.On(HostOps.Settings, _ => new SettingsResult(FakeHost.StatusOf().Settings, false));
        host.On(HostOps.AgentsRefresh, _ => Readiness("1.2.3"));
        var pending = settings.InstallAgentAsync("claude");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await settings.SaveNameAsync();
            Assert.True(settings.Busy);
            Assert.False(settings.CanInstallAgents);
            await settings.InstallAgentAsync("codex");
            Assert.Single(host.Sent(HostOps.AgentsInstall));
            Assert.True(settings.AgentInstallInProgress);
            Assert.True(settings.Busy);
            Assert.Contains("still running", settings.Error);
        }
        finally { stopped.TrySetResult(); }
        await pending;
        Assert.False(settings.AgentInstallInProgress);
        Assert.False(settings.Busy);
    }

    [Fact]
    public async Task A_lost_install_reply_keeps_cancellation_visible_and_recovers_host_progress_on_reconnect()
    {
        await Connected();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnAsync(HostOps.AgentsInstall, async _ =>
        {
            host.Status = host.Status with { AgentInstallation = new("claude", true, "Fixture installation is still running.") };
            entered.SetResult();
            await stopped.Task;
            return new AgentInstallResult(false, "Fixture installation stopped.");
        });
        host.OnAsync(HostOps.AgentsInstallCancel, async _ =>
        {
            host.Status = host.Status with { AgentInstallation = new("claude", false, "Fixture cancellation completed.") };
            await host.SendAsync(HostOps.StatusEvent, host.Status);
            return new { cancellationRequested = true };
        });
        var pending = settings.InstallAgentAsync("claude");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await host.StopAsync();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(settings.AgentInstallInProgress);
            Assert.Contains("result is unavailable", settings.AgentInstallStatus);
            Assert.False(settings.CanInstallAgents);
            settings.Refresh();
            Assert.True(settings.AgentInstallInProgress);

            host.Start();
            await Wait.UntilAsync(() => settings.HostRunning && settings.AgentInstallStatus.Contains("still running"), "reconciled installation");
            Assert.True(settings.AgentInstallInProgress);
            Assert.False(settings.CanInstallAgents);
            Assert.False(settings.CanManageAgents);
            await settings.CancelAgentInstallAsync();
            Assert.Single(host.Sent(HostOps.AgentsInstallCancel));
            await Wait.UntilAsync(() => !settings.AgentInstallInProgress, "confirmed cancellation");
            Assert.Contains("cancellation completed", settings.AgentInstallStatus);
        }
        finally { stopped.TrySetResult(); await pending; }
    }

    [Fact]
    public async Task A_terminal_host_status_received_before_an_error_reply_is_reconciled_without_another_event()
    {
        await Connected();
        host.OnAsync(HostOps.AgentsInstall, async _ =>
        {
            host.Status = host.Status with { AgentInstallation = new("claude", false, "Fixture installation stopped before the error reply.") };
            await host.SendAsync(HostOps.StatusEvent, host.Status);
            await Wait.UntilAsync(() => connection.Status?.AgentInstallation?.Detail.Contains("before the error reply") == true, "terminal installation status");
            throw new InvalidOperationException("fixture installation error");
        });

        await settings.InstallAgentAsync("claude");

        Assert.False(settings.AgentInstallInProgress);
        Assert.Contains("stopped before the error reply", settings.AgentInstallStatus);
        Assert.Contains("fixture installation error", settings.Error);
        Assert.True(settings.CanInstallAgents);
        Assert.True(settings.CanManageAgents);
    }

    [Fact]
    public async Task A_new_Settings_page_recovers_installation_progress_and_a_cleanup_block_from_the_host()
    {
        await Connected();
        host.Status = host.Status with { AgentInstallation = new("codex", true, "Fixture installation started in another window.") };
        await connection.RefreshStatusAsync();
        await Wait.UntilAsync(() => settings.AgentInstallInProgress, "host installation progress");
        var reopened = new SettingsViewModel(connection, manager, updater, atSignIn, () => prefs, p => prefs = p, "0.1.0", "https://relay.default");
        Assert.True(reopened.AgentInstallInProgress);
        Assert.Contains("another window", reopened.AgentInstallStatus);
        Assert.False(reopened.CanInstallAgents);
        host.On(HostOps.AgentsInstallCancel, _ => new { cancellationRequested = true });
        await reopened.CancelAgentInstallAsync();
        Assert.Single(host.Sent(HostOps.AgentsInstallCancel));

        host.Status = host.Status with { AgentInstallation = new("codex", false, "Fixture cleanup is unconfirmed; installations are blocked.", Blocked: true) };
        await host.SendAsync(HostOps.StatusEvent, host.Status);
        await Wait.UntilAsync(() => !reopened.AgentInstallInProgress, "cleanup block");
        Assert.Contains("cleanup is unconfirmed", reopened.AgentInstallStatus);
        Assert.False(reopened.CanInstallAgents);
    }

    [Fact]
    public async Task Settings_shows_host_readiness_without_claiming_an_installed_CLI_is_signed_in_or_ready()
    {
        await Connected();
        Assert.Contains("Checking", settings.AgentsState);
        host.Status = host.Status with { Agents = Readiness("1.2.3") };
        await host.SendAsync(HostOps.StatusEvent, host.Status);
        await Wait.UntilAsync(() => settings.Agents.Count == 1, "agent readiness");

        var row = Assert.Single(settings.Agents);
        Assert.Equal("Installed, version 1.2.3", row.Installation);
        Assert.Contains("Explicit hook setup", row.Checks);
        Assert.Contains("Sign-in has not been verified", row.Checks);
        Assert.Contains("No hook observed", row.HookActivity);

        host.On(HostOps.AgentsRefresh, _ =>
        {
            host.Status = host.Status with { Agents = Readiness("2.3.4") };
            return host.Status.Agents;
        });
        await settings.CheckAgentsAsync();
        await Wait.UntilAsync(() => settings.Agents.Single().Installation.Contains("2.3.4"), "the refreshed version");
        Assert.Single(host.Sent(HostOps.AgentsRefresh));
        Assert.False(settings.HasError);

        await host.StopAsync();
        await Wait.UntilAsync(() => !settings.HostRunning, "the host disconnected");
        Assert.Empty(settings.Agents);
        Assert.False(settings.CanCheckAgents);
    }

    [Fact]
    public async Task The_agent_check_button_is_notified_as_soon_as_a_Settings_action_becomes_busy()
    {
        await ConnectedWith(FakeHost.StatusOf() with { Agents = Readiness("1.2.3") });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnAsync(HostOps.AgentsRefresh, async _ => { await release.Task.ConfigureAwait(ConfigureAwaitOptions.ForceYielding); return host.Status.Agents; });
        var enabled = new List<bool>();
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(settings.CanCheckAgents)) { enabled.Add(settings.CanCheckAgents); }
        };

        var pending = settings.CheckAgentsAsync();
        try
        {
            Assert.False(settings.CanCheckAgents);
            Assert.Contains(false, enabled);
        }
        finally { release.TrySetResult(); await pending; }

        Assert.False(settings.HasError);
        Assert.True(settings.CanCheckAgents);
        Assert.Contains(true, enabled);
    }

    [Fact]
    public async Task An_older_host_is_explained_and_never_sent_a_readiness_request()
    {
        await using var older = new FakeHost();
        older.Capabilities.Remove(HostOps.AgentReadinessCapability);
        older.Start();
        await using var olderConnection = older.Connect(ui);
        var page = new SettingsViewModel(olderConnection, manager, updater, atSignIn, () => prefs, p => prefs = p, "0.1.0", "https://relay.default");
        await Wait.UntilAsync(() => page.HostRunning, "the older host");

        Assert.Contains("does not report agent readiness", page.AgentsState);
        Assert.False(page.CanCheckAgents);
        await page.CheckAgentsAsync();
        Assert.True(page.HasError);
        Assert.Empty(older.Sent(HostOps.AgentsRefresh));
    }

    [Fact]
    public async Task Settings_setup_is_explicit_reports_restart_and_never_claims_fresh_session_readiness()
    {
        await ConnectedWith(FakeHost.StatusOf() with { Agents = Readiness("1.2.3") });
        await Wait.UntilAsync(() => settings.Agents.Count == 1, "agent readiness");
        Assert.True(settings.Agents.Single().CanSetUp);
        host.On(HostOps.AgentsSetup, data =>
        {
            Assert.Equal("claude", data.GetProperty("agent").GetString());
            Assert.Equal("install", data.GetProperty("action").GetString());
            host.Status = host.Status with { RestartRequired = true };
            return new AgentSetupResult(true, "Registered. Start new agent sessions to verify hooks.", true);
        });
        // The scan's status is said as an event too, as soon as its answer has gone. The app takes
        // the status read after the scan through a post, which it drops if a status event came
        // meanwhile; ConnectedWith's, still carrying 1.2.3, could be that event (#925). Then the
        // last status event to arrive is this one, with 2.3.4 in it.
        host.On(HostOps.AgentsRefresh, _ =>
        {
            host.Status = host.Status with { Agents = Readiness("2.3.4") };
            return new AgentBridge.Transport.ChannelReply(host.Status.Agents!, ch => host.SendAsync(HostOps.StatusEvent, host.Status));
        });

        await settings.SetUpAgentAsync("claude");

        Assert.Single(host.Sent(HostOps.AgentsSetup));
        Assert.Single(host.Sent(HostOps.AgentsRefresh));
        // The refreshed status is posted to the UI thread, so it lands after SetUpAgentAsync returns.
        await Wait.UntilAsync(() => settings.Agents.SingleOrDefault()?.Installation.Contains("2.3.4") == true && settings.RestartRequired, "the readiness and restart the setup left",
            why: () => $"installation {settings.Agents.SingleOrDefault()?.Installation ?? "none"}, restart required {settings.RestartRequired}");
        Assert.Empty(host.Sent(HostOps.Restart));
        Assert.Equal("Set up · restart the host, then start a new session to check it", settings.Agents.Single().Status);
        Assert.Contains("Trust and a new session have not been verified", settings.Agents.Single().Checks);
        Assert.False(settings.HasError);
    }

    [Fact]
    public async Task A_failed_post_setup_scan_preserves_the_mutation_outcome_and_shows_the_scan_error()
    {
        await ConnectedWith(FakeHost.StatusOf() with { Agents = Readiness("1.2.3") });
        host.On(HostOps.AgentsSetup, _ => new AgentSetupResult(true, "Registered. Restart explicitly.", true));
        host.On(HostOps.AgentsRefresh, _ => throw new InvalidOperationException("fixture post-setup scan failed"));

        await settings.SetUpAgentAsync("claude");

        Assert.Equal("Set up · restart the host, then start a new session to check it", settings.Agents.Single().Status);
        Assert.Equal("fixture post-setup scan failed", settings.Error);
        Assert.Empty(host.Sent(HostOps.Restart));
    }

    [Fact]
    public async Task Settings_setup_failures_keep_partial_work_visible_and_do_not_restart()
    {
        await ConnectedWith(FakeHost.StatusOf() with { Agents = Readiness("1.2.3") });
        host.On(HostOps.AgentsSetup, _ => new AgentSetupResult(false, "Hook setup incomplete: fixture partial work", true));

        await settings.SetUpAgentAsync("claude");

        Assert.Equal("Hook setup incomplete: fixture partial work", settings.Error);
        // The row says why from a known set; the command's own text stays in the page's error and the log.
        Assert.Equal("Setup failed · the host couldn't finish it", settings.Agents.Single().Status);
        Assert.Equal(Tone.Bad, settings.Agents.Single().StatusTone);
        Assert.Empty(host.Sent(HostOps.Restart));
        Assert.False(settings.Busy);
    }

    // Holds what is posted to it until released, then runs it on the test's UI thread.
    sealed class HeldContext : SynchronizationContext
    {
        readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Run, object? State)> queue = new();
        public bool Waiting => !queue.IsEmpty;
        public override void Post(SendOrPostCallback d, object? state) => queue.Enqueue((d, state));
        public void Release() { while (queue.TryDequeue(out var item)) { TestUi.ForTest.Invoke(() => item.Run(item.State)); } }
    }

    // An agent as a current host reports it: `hookSetup` paired with its hooks as AgentReadinessService.Hooks
    // pairs them, unless a test says otherwise (HookSetupState.Unknown with any but blocked or failed
    // hooks is a host from before the field).
    static AgentReadiness Agent(string id, string label, ReadinessState hooks, ReadinessState installation = ReadinessState.Working, string hooksDetail = "Fixture hooks.", HookSetupState? setup = null) =>
        new(id, label, "fixture-home", $"{id}.exe", "1.2.3",
            new(installation, "Executable found."), new(ReadinessState.Working, "Version 1.2.3"), new(hooks, hooksDetail),
            new(ReadinessState.Unknown, "Sign-in has not been verified."), new(ReadinessState.Unknown, "Trust has not been verified."),
            HookSetup: setup ?? (hooks, hooksDetail) switch
            {
                (ReadinessState.Blocked, _) => HookSetupState.Unknown,
                (_, var d) when d.Contains("did not complete", StringComparison.Ordinal) => HookSetupState.Incomplete,
                (ReadinessState.Failed, _) => HookSetupState.Unknown,
                (_, var d) when d.Contains("owned setup record", StringComparison.Ordinal) => HookSetupState.Completed,
                _ => HookSetupState.NotSetUp,
            });

    [Theory]
    [InlineData("Hook setup incomplete: C:\\Users\\dana\\.codex\\agent-ha-bridge-owner.json is a legacy registration; use Migrate", "Setup failed · an older bridge's hooks are there; migrate them first")]
    [InlineData("agent-ha-bridge is active or cannot be checked (running); use Migrate instead", "Setup failed · this machine's host is older")]
    [InlineData("Hook setup incomplete: C:\\Users\\dana\\.codex\\agent-bridge-owner.json belongs to another installation; it was left alone", "Setup failed · another bridge profile owns its hooks")]
    [InlineData("Hook setup incomplete: Access to the path 'C:\\Users\\dana\\.codex\\config.toml' is denied.", "Setup failed · access denied")]
    [InlineData("Hook setup incomplete: this profile belongs to a migration; use migration status or rollback", "Setup failed · this profile is being migrated")]
    public void A_failed_setup_says_why_from_a_known_set_never_its_path(string raw, string words)
    {
        Assert.Equal(words, AgentWords.SetupFailed(remove: false, "Set up", raw));
    }

    [Fact]
    public void A_settings_row_names_the_product_where_the_host_names_the_agent()
    {
        // The host's labels are the agents' ("Claude", "Copilot"): Launchers.All.
        var claude = new AgentReadinessRow(Agent("claude", "Claude", ReadinessState.NeedsAction), canManage: true);
        var copilot = new AgentReadinessRow(Agent("copilot", "Copilot", ReadinessState.NeedsAction), canManage: true);

        Assert.Equal(("Claude Code", "Set up Claude Code", "Install CLI for Claude Code"), (claude.Label, claude.SetUpName, claude.InstallName));
        Assert.Equal(("Copilot CLI", "Remove Copilot CLI's connection"), (copilot.Label, copilot.RemoveName));
    }

    [Fact]
    public void Each_agent_rows_buttons_are_named_for_its_agent_and_have_ids_of_their_own()
    {
        var copilot = new AgentReadinessRow(Agent("copilot", "Copilot CLI", ReadinessState.NeedsAction), canManage: true);
        var claude = new AgentReadinessRow(Agent("claude", "Claude Code", ReadinessState.Failed, hooksDetail: DidNotComplete), canManage: true);

        Assert.Equal("Set up Copilot CLI", copilot.SetUpName);
        Assert.Equal("Repair Claude Code", claude.SetUpName);
        Assert.Equal("Remove Copilot CLI's connection", copilot.RemoveName);
        Assert.Equal("Remove Claude Code's connection?", claude.RemoveQuestion);
        Assert.Equal("Set up Copilot CLI?", copilot.SetUpQuestion);
        string[] ids = [copilot.SetUpId, copilot.RemoveId, copilot.StatusId, claude.SetUpId, claude.RemoveId, claude.StatusId];
        Assert.Equal(["SetUpAgent_copilot", "RemoveAgent_copilot", "AgentStatus_copilot", "SetUpAgent_claude", "RemoveAgent_claude", "AgentStatus_claude"], ids);
    }

    // The host's own words for its hooks states (AgentReadinessService.Hooks).
    const string NoRecordIntakeOff = "Hook intake is off for this profile. Explicit setup or migration is required.";
    const string RecordIntakeOff = "An owned setup record exists, but this profile's hook intake is off.";
    const string NoRecordIntakeOn = "Intake is enabled; this agent's registration and ownership have not been verified.";
    const string RecordIntakeOn = "An owned setup record exists; registration and activation still need verification.";
    const string UnknownRecord = "The hook ownership record is not recognized. Inspect it before repairing setup.";
    const string DidNotComplete = "Hook setup for this profile did not complete; its registrations may be partial. Repair setup.";
    const string Unreadable = "Hook ownership could not be checked: Access to the path is denied.";
    const string InUse = "Hook ownership could not be checked: The process cannot access the file because it is being used by another process.";
    // `hookSetup` unknown beside hooks a current host would have said it of (#784's words).
    const string OlderHost = "Can't check · this machine's host is older";

    [Theory]
    [InlineData(ReadinessState.Missing, "Fixture hooks.", "Set up")]
    [InlineData(ReadinessState.NeedsAction, NoRecordIntakeOff, "Set up")]
    [InlineData(ReadinessState.NeedsAction, RecordIntakeOff, "Set up")]
    [InlineData(ReadinessState.Unknown, NoRecordIntakeOn, "Set up")]
    [InlineData(ReadinessState.Failed, UnknownRecord, null)]
    [InlineData(ReadinessState.Failed, DidNotComplete, "Repair")]
    [InlineData(ReadinessState.Working, "Fixture hooks.", null)]
    [InlineData(ReadinessState.Blocked, "Another bridge profile owns this agent's hook setup. Do not overwrite its registrations.", null)]
    public void An_agent_row_offers_one_verb_for_its_hooks_state_and_none_once_they_work(ReadinessState hooks, string detail, string? verb)
    {
        var row = new AgentReadinessRow(Agent("copilot", "Copilot CLI", hooks, hooksDetail: detail), canManage: true);

        Assert.Equal(verb, row.SetUpVerb);
        Assert.Equal(verb is not null, row.ShowSetUp);
        Assert.Equal(verb is not null, row.CanSetUp);
    }

    [Fact]
    public void Only_a_missing_CLI_offers_install_named_for_its_agent()
    {
        var missing = new AgentReadinessRow(Agent("codex", "Codex", ReadinessState.NeedsAction, installation: ReadinessState.Missing), canInstall: true);
        var installed = new AgentReadinessRow(Agent("codex", "Codex", ReadinessState.NeedsAction), canInstall: true);

        Assert.True(missing.ShowInstall);
        Assert.True(missing.CanInstall);
        Assert.Equal("Install CLI for Codex", missing.InstallName);
        Assert.Equal("InstallAgent_codex", missing.InstallId);
        Assert.False(installed.ShowInstall);
        Assert.False(installed.CanInstall);
    }

    [Fact]
    public void A_set_up_agent_the_host_cannot_test_yet_offers_no_verb_and_says_it_is_not_tested_yet()
    {
        var row = new AgentReadinessRow(Agent("copilot", "Copilot CLI", ReadinessState.Unknown, hooksDetail: RecordIntakeOn), canManage: true);

        Assert.Null(row.SetUpVerb);
        Assert.False(row.ShowSetUp);
        Assert.False(row.CanSetUp);
        Assert.Equal("Not tested yet", row.Status);
        Assert.Equal(Tone.None, row.StatusTone);
    }

    // The host's `hookSetup` (#705 review r4474869190), on the wire beside the hooks state its host
    // sends with it. Until this build's AgentReadiness carries the field, the adapter reads it as
    // missing, and the fixture's value is handed to the row as the adapter will hand it then.
    [Theory]
    [InlineData("unknown", NoRecordIntakeOn, "notSetUp", "Set up", "")]
    [InlineData("failed", "Hook setup did not finish. Repair it before relying on these hooks.", "incomplete", "Repair", "")]
    [InlineData("needsAction", RecordIntakeOff, "incomplete", "Repair", "")]
    [InlineData("unknown", RecordIntakeOn, "completed", null, "Not tested yet")]
    [InlineData("unknown", RecordIntakeOn, "unknown", null, OlderHost)]
    [InlineData("unknown", NoRecordIntakeOn, null, null, OlderHost)]
    [InlineData("needsAction", NoRecordIntakeOff, null, null, OlderHost)]
    [InlineData("blocked", "Another bridge profile owns this agent's hook setup. Do not overwrite its registrations.", "incomplete", null, "Read-only · another bridge profile owns its hooks")]
    [InlineData("failed", UnknownRecord, "unknown", null, "Hook ownership couldn't be checked · couldn't read")]
    [InlineData("failed", Unreadable, "unknown", null, "Hook ownership couldn't be checked · access denied")]
    [InlineData("failed", InUse, "unknown", null, "Hook ownership couldn't be checked · file in use")]
    [InlineData("failed", DidNotComplete, "incomplete", "Repair", "")]
    [InlineData("failed", DidNotComplete, null, null, "Can't check")]
    public void An_agent_row_takes_its_verb_from_the_hosts_hook_setup_and_reads_a_missing_one_as_unknown(string hooks, string detail, string? hookSetup, string? verb, string status)
    {
        var agent = FromWire(hooks, detail, hookSetup);
        var built = typeof(AgentReadiness).GetProperty("HookSetup") is not null;
        var sent = HookSetups.Parse(hookSetup);
        Assert.Equal(built ? sent : HookSetup.Unknown, HookSetups.Of(agent));

        var row = new AgentReadinessRow(agent, canManage: true, setup: built ? null : sent);

        Assert.Equal(sent, row.Setup);
        Assert.Equal(verb, row.SetUpVerb);
        Assert.Equal(status, row.Status);
    }

    // A readiness row as a host sends it, with `hookSetup` or, for an older host, without.
    static AgentReadiness FromWire(string hooks, string detail, string? hookSetup) => JsonSerializer.Deserialize<AgentReadiness>($$"""
        {"id":"codex","label":"Codex","home":"fixture-home","executable":"codex.exe","version":"0.48.0",
         "installation":{"state":"working","detail":"Executable found."},
         "versionCheck":{"state":"working","detail":"Version 0.48.0"},
         "hooks":{"state":"{{hooks}}","detail":{{JsonSerializer.Serialize(detail)}}},
         "authentication":{"state":"unknown","detail":"Sign-in has not been verified."},
         "activation":{"state":"unknown","detail":"Trust has not been verified."}
         {{(hookSetup is null ? "" : $",\"hookSetup\":\"{hookSetup}\"")}}}
        """, ProtocolJson.Options)!;

    [Fact]
    public void Failed_hooks_the_host_cannot_repair_offer_no_button_and_say_the_hosts_words_in_the_neutral_tone()
    {
        var row = new AgentReadinessRow(FromWire("failed", UnknownRecord, "unknown"), canManage: true);

        Assert.False(row.ShowSetUp);
        Assert.False(row.CanSetUp);
        Assert.False(row.ShowRemove);
        Assert.Equal("Hook ownership couldn't be checked · couldn't read", row.Status);
        Assert.Equal(Tone.None, row.StatusTone);
        Assert.Equal("Codex: Hook ownership couldn't be checked · couldn't read", row.StatusName);
    }

    [Fact]
    public void An_ownership_check_the_host_was_denied_says_why_never_the_path_or_the_exception()
    {
        const string denied = "Hook ownership could not be checked: Access to the path 'C:\\Users\\dana\\.codex\\agent-bridge-owner.json' is denied.";
        var row = new AgentReadinessRow(FromWire("failed", denied, "unknown"), canManage: true);

        Assert.Equal("Hook ownership couldn't be checked · access denied", row.Status);
        // The reason is said once, on the status line; the Hooks line says only the fact.
        Assert.Contains("Hooks: Not checked", row.Checks);
        Assert.DoesNotContain("access denied", row.Checks);
        Assert.DoesNotContain("Access to the path", row.Status + row.Checks + row.Installation);
        Assert.DoesNotContain("agent-bridge-owner.json", row.Status + row.Checks + row.Installation);
        // The host's own words go to the app's log.
        Assert.Contains(denied, row.Raw());
    }

    [Theory]
    [InlineData("Hook setup is unavailable while this host is read-only: agent-ha-bridge is running for this user; answer at the machine", "Read-only · this machine's host is older")]
    [InlineData("Another bridge profile owns this agent's hook setup. Do not overwrite its registrations.", "Read-only · another bridge profile owns its hooks")]
    [InlineData("Hook setup is unavailable while this host is read-only: this is a read-only comparison host (delivery.readOnly in its config); answer at the machine", "Read-only · set in this host's settings")]
    [InlineData("Hook setup is unavailable while this host is read-only: this host's config.json has a problem, so it takes nothing from other devices; answer at the machine", "Read-only · set in this host's settings")]
    public void Hooks_held_back_from_this_app_say_read_only_and_why_with_no_install_or_verb(string detail, string words)
    {
        var row = new AgentReadinessRow(Agent("codex", "Codex", ReadinessState.Blocked, installation: ReadinessState.Missing, hooksDetail: detail), canManage: true, canInstall: true);

        Assert.Equal(words, row.Status);
        Assert.Equal(Tone.None, row.StatusTone);
        Assert.False(row.ShowInstall);
        Assert.False(row.CanInstall);
        Assert.False(row.ShowSetUp);
        Assert.False(row.ShowRemove);
        Assert.Equal("Not installed · install it on this machine", row.Installation);
        Assert.Contains("Hooks: Not checked", row.Checks);
        Assert.DoesNotContain(words, row.Checks);
    }

    [Fact]
    public void A_check_whose_reason_the_status_line_gives_says_only_the_fact_and_others_keep_their_words()
    {
        const string why = "agent-ha-bridge is running for this user; answer at the machine";
        var readOnly = new AgentReadinessRow(Agent("codex", "Codex", ReadinessState.Blocked, hooksDetail: $"Hook setup is unavailable while this host is read-only: {why}")
            with { VersionCheck = new(ReadinessState.Blocked, $"Version command not run while the host is read-only: {why}") });
        var olderHost = new AgentReadinessRow(Agent("codex", "Codex", ReadinessState.NeedsAction, hooksDetail: "Explicit hook setup is needed.", setup: HookSetupState.Unknown));

        Assert.Equal("Read-only · this machine's host is older", readOnly.Status);
        Assert.StartsWith("Version: Not checked\nHooks: Not checked\n", readOnly.Checks, StringComparison.Ordinal);
        Assert.Equal("Can't check · this machine's host is older", olderHost.Status);
        Assert.Contains("Hooks: Explicit hook setup is needed.", olderHost.Checks);
    }

    [Fact]
    public void A_missing_CLI_is_not_installed_with_install_cli_where_it_can_be_and_says_where_to_install_it_where_not()
    {
        var offered = new AgentReadinessRow(Agent("codex", "Codex", ReadinessState.NeedsAction, installation: ReadinessState.Missing), canInstall: true);
        var agency = new AgentReadinessRow(Agent("agency", "Agency", ReadinessState.NeedsAction, installation: ReadinessState.Missing), canInstall: true);
        var olderHost = new AgentReadinessRow(Agent("codex", "Codex", ReadinessState.NeedsAction, installation: ReadinessState.Missing), canInstall: false, installOffered: false);

        Assert.Equal(("Not installed", true), (offered.Installation, offered.ShowInstall));
        Assert.Equal(("Not installed · install it on this machine", false), (agency.Installation, agency.ShowInstall));
        Assert.Equal(("Not installed · install it on this machine", false), (olderHost.Installation, olderHost.ShowInstall));
    }

    // The host removes only hooks this profile's owned record says it set up, and refuses the rest.
    [Theory]
    [InlineData("needsAction", NoRecordIntakeOff, "notSetUp", false)]
    [InlineData("unknown", NoRecordIntakeOn, "notSetUp", false)]
    [InlineData("unknown", RecordIntakeOn, "completed", true)]
    [InlineData("failed", DidNotComplete, "incomplete", true)]
    [InlineData("needsAction", RecordIntakeOff, "incomplete", true)]
    [InlineData("failed", UnknownRecord, "unknown", false)]
    [InlineData("blocked", "Another bridge profile owns this agent's hook setup. Do not overwrite its registrations.", "incomplete", false)]
    [InlineData("needsAction", NoRecordIntakeOff, null, false)]
    [InlineData("unknown", NoRecordIntakeOn, null, false)]
    // An older host can't say whether there's a setup of this profile's to remove (#784: no button
    // that can't work).
    [InlineData("unknown", RecordIntakeOn, null, false)]
    [InlineData("needsAction", RecordIntakeOff, null, false)]
    [InlineData("failed", DidNotComplete, null, false)]
    [InlineData("failed", UnknownRecord, null, false)]
    public void Remove_is_offered_only_for_hooks_this_profiles_owned_record_set_up(string hooks, string detail, string? hookSetup, bool canRemove)
    {
        var row = new AgentReadinessRow(FromWire(hooks, detail, hookSetup), canManage: true);

        Assert.Equal(canRemove, row.ShowRemove);
        Assert.Equal(canRemove, row.CanRemove);
    }

    [Fact]
    public void A_setup_that_did_not_finish_stays_repair_after_a_fresh_check_without_its_failure()
    {
        var row = new AgentReadinessRow(Agent("codex", "Codex", ReadinessState.NeedsAction, hooksDetail: RecordIntakeOff), canManage: true, setup: HookSetup.Incomplete);

        Assert.Equal("Repair", row.SetUpVerb);
        Assert.False(row.Keeps(new("Hook setup incomplete: fixture partial work", Tone.Bad)));
    }

    [Fact]
    public void A_completed_setup_is_not_tested_yet_and_never_ready()
    {
        // Words the host may change: `hookSetup` decides, not the detail.
        var row = new AgentReadinessRow(Agent("codex", "Codex", ReadinessState.Unknown, hooksDetail: "Setup completed; no hook observed yet."), canManage: true, setup: HookSetup.Completed);

        Assert.Null(row.SetUpVerb);
        Assert.True(row.NotTested);
        Assert.Equal("Not tested yet", row.Status);
        Assert.DoesNotContain("Ready", row.StatusName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Copilot_CLI_and_Agency_share_what_a_setup_left_of_their_hooks_through_a_fresh_check()
    {
        // Agency runs on Copilot CLI's hooks, which the host reports set up on both rows. Removing
        // them can leave Copilot CLI's owned record, which it then reads as a setup that didn't finish.
        await ConnectedWith(FakeHost.StatusOf() with { Agents = new(DateTimeOffset.UtcNow,
            [Agent("copilot", "Copilot CLI", ReadinessState.Unknown, hooksDetail: RecordIntakeOn), Agent("agency", "Agency", ReadinessState.Unknown, hooksDetail: RecordIntakeOn)]) });
        await Wait.UntilAsync(() => settings.Agents.Count == 2, "agent readiness");
        var outcome = new AgentSetupResult(true, "Removed this installation's hook registrations.", false);
        host.On(HostOps.AgentsSetup, _ =>
        {
            host.Status = host.Status with { Agents = new(DateTimeOffset.UtcNow,
                [Agent("copilot", "Copilot CLI", ReadinessState.Failed, hooksDetail: DidNotComplete), Agent("agency", "Agency", ReadinessState.Failed, hooksDetail: DidNotComplete)]) };
            return outcome;
        });
        host.On(HostOps.AgentsRefresh, _ => host.Status.Agents);
        AgentReadinessRow Row(string id) => settings.Agents.Single(a => a.Id == id);

        await settings.SetUpAgentAsync("copilot", remove: true);
        await settings.CheckAgentsAsync();

        Assert.Equal("Set up", Row("agency").SetUpVerb);
        Assert.Equal("", Row("agency").Status);
        Assert.Equal("Removed · sessions already running keep their hooks until they end", Row("copilot").Status);

        outcome = new AgentSetupResult(false, "Hook setup incomplete: fixture partial work", true);
        await settings.SetUpAgentAsync("agency");

        Assert.Equal("Setup failed · the host couldn't finish it", Row("agency").Status);
        await settings.CheckAgentsAsync();
        Assert.Equal("Repair", Row("copilot").SetUpVerb);
        Assert.Equal("Repair", Row("agency").SetUpVerb);
        Assert.Equal("", Row("copilot").Status);
    }

    [Fact]
    public async Task A_removal_that_fails_part_way_is_scanned_afresh_so_its_row_offers_repair()
    {
        // The host's readiness is a cached report that only a scan renews: the failed removal left a
        // setup that didn't finish, which the cache doesn't show.
        await ConnectedWith(FakeHost.StatusOf() with { Agents = new(DateTimeOffset.UtcNow, [Agent("codex", "Codex", ReadinessState.Unknown, hooksDetail: RecordIntakeOn)]) });
        await Wait.UntilAsync(() => settings.Agents.Count == 1, "agent readiness");
        host.On(HostOps.AgentsSetup, _ => new AgentSetupResult(false, "Removal incomplete: the Codex plugin is still installed.", false));
        host.On(HostOps.AgentsRefresh, _ =>
        {
            host.Status = host.Status with { Agents = new(DateTimeOffset.UtcNow, [Agent("codex", "Codex", ReadinessState.Failed, hooksDetail: "Hook setup for this profile did not complete; its registrations may be partial. Repair setup.")]) };
            return host.Status.Agents;
        });
        Assert.Null(settings.Agents.Single().SetUpVerb);

        await settings.SetUpAgentAsync("codex", remove: true);

        Assert.Single(host.Sent(HostOps.AgentsRefresh));
        Assert.Equal("Repair", settings.Agents.Single().SetUpVerb);
        Assert.Equal("Removal failed · the host couldn't finish it", settings.Agents.Single().Status);
        Assert.Equal("Removal incomplete: the Codex plugin is still installed.", settings.Error);
    }

    [Fact]
    public async Task A_removal_that_leaves_the_owned_record_offers_set_up_again_through_a_fresh_check()
    {
        // Copilot CLI's removal keeps its record when other entries share the file, and the host then
        // reads it as a setup that didn't finish.
        await ConnectedWith(FakeHost.StatusOf() with { Agents = new(DateTimeOffset.UtcNow, [Agent("copilot", "Copilot CLI", ReadinessState.Unknown, hooksDetail: RecordIntakeOn)]) });
        await Wait.UntilAsync(() => settings.Agents.Count == 1, "agent readiness");
        host.On(HostOps.AgentsSetup, _ =>
        {
            host.Status = host.Status with { Agents = new(DateTimeOffset.UtcNow, [Agent("copilot", "Copilot CLI", ReadinessState.Failed, hooksDetail: DidNotComplete)]) };
            return new AgentSetupResult(true, "Removed this installation's hook registrations.", false);
        });
        host.On(HostOps.AgentsRefresh, _ => host.Status.Agents);
        Assert.Null(settings.Agents.Single().SetUpVerb);

        await settings.SetUpAgentAsync("copilot", remove: true);
        await settings.CheckAgentsAsync();

        Assert.Equal("Set up", settings.Agents.Single().SetUpVerb);
        Assert.Equal("Removed · sessions already running keep their hooks until they end", settings.Agents.Single().Status);
    }

    [Fact]
    public void A_completed_setup_the_host_reports_after_a_removal_clears_the_removal()
    {
        var removed = new AgentSetupNote("Removed this installation's hook registrations.", Tone.Ok, Removed: true);
        var row = new AgentReadinessRow(Agent("copilot", "Copilot CLI", ReadinessState.Unknown, hooksDetail: "An owned setup record exists and setup completed; registration and activation still need verification."),
            canManage: true, shared: removed, setup: HookSetup.Completed);

        Assert.Null(row.SetUpVerb);
        Assert.False(row.Keeps(removed));
    }

    [Fact]
    public void A_removal_the_host_reads_as_a_setup_that_did_not_finish_offers_set_up_not_repair()
    {
        var row = new AgentReadinessRow(Agent("copilot", "Copilot CLI", ReadinessState.Failed), canManage: true,
            note: new("Removed this installation's hook registrations.", Tone.Ok, Removed: true), setup: HookSetup.Incomplete);

        Assert.Equal("Set up", row.SetUpVerb);
        Assert.True(row.Keeps(new("Removed.", Tone.Ok, Removed: true)));
    }

    [Fact]
    public void On_a_host_from_before_hook_setup_a_row_says_it_cant_check_and_offers_nothing_even_after_its_own_setup_failed()
    {
        var failed = new AgentReadinessRow(Agent("copilot", "Copilot CLI", ReadinessState.Unknown, hooksDetail: RecordIntakeOn, setup: HookSetupState.Unknown), canManage: true,
            note: new("Setup failed · the host couldn't finish it", Tone.Bad));
        var quiet = new AgentReadinessRow(Agent("copilot", "Copilot CLI", ReadinessState.Unknown, hooksDetail: RecordIntakeOn, setup: HookSetupState.Unknown), canManage: true);

        Assert.Equal((null, false, false), (failed.SetUpVerb, failed.CanSetUp, failed.ShowRemove));
        Assert.Equal("Setup failed · the host couldn't finish it", failed.Status);
        Assert.Equal((null, false, false), (quiet.SetUpVerb, quiet.CanSetUp, quiet.ShowRemove));
        Assert.Equal(OlderHost, quiet.Status);
        Assert.Equal(Tone.None, quiet.StatusTone);
        Assert.False(quiet.Keeps(new("Setup failed · the host couldn't finish it", Tone.Bad)));
    }

    [Fact]
    public async Task A_setup_that_fails_after_saving_its_record_offers_repair_through_a_fresh_check()
    {
        await ConnectedWith(FakeHost.StatusOf() with { Agents = new(DateTimeOffset.UtcNow, [Agent("copilot", "Copilot CLI", ReadinessState.NeedsAction, hooksDetail: NoRecordIntakeOff)]) });
        await Wait.UntilAsync(() => settings.Agents.Count == 1, "agent readiness");
        // The host saved the record, then failed to register: it reads as a setup that didn't finish.
        host.On(HostOps.AgentsSetup, _ =>
        {
            host.Status = host.Status with { Agents = new(DateTimeOffset.UtcNow, [Agent("copilot", "Copilot CLI", ReadinessState.Failed, hooksDetail: DidNotComplete)]) };
            return new AgentSetupResult(false, "Hook setup incomplete: fixture partial work", true);
        });
        host.On(HostOps.AgentsRefresh, _ => host.Status.Agents);

        await settings.SetUpAgentAsync("copilot");
        Assert.Equal("Setup failed · the host couldn't finish it", settings.Agents.Single().Status);
        Assert.Equal(Tone.Bad, settings.Agents.Single().StatusTone);
        await settings.CheckAgentsAsync();

        Assert.Equal("Repair", settings.Agents.Single().SetUpVerb);
        Assert.Equal("", settings.Agents.Single().Status);
    }

    [Fact]
    public void A_rows_status_is_named_for_its_agent_so_it_is_read_out_whose_it_is()
    {
        var failed = new AgentReadinessRow(Agent("claude", "Claude Code", ReadinessState.Failed), note: new("It took too long, and some hooks may have changed.", Tone.Bad));
        var untested = new AgentReadinessRow(Agent("codex", "Codex", ReadinessState.Unknown, hooksDetail: RecordIntakeOn));
        var quiet = new AgentReadinessRow(Agent("copilot", "Copilot CLI", ReadinessState.NeedsAction));

        Assert.Equal("Claude Code: It took too long, and some hooks may have changed.", failed.StatusName);
        Assert.Equal("Codex: Not tested yet", untested.StatusName);
        Assert.Equal("", quiet.StatusName);
    }

    [Fact]
    public void A_setup_that_ends_before_its_progress_is_drawn_is_announced_once_with_its_result()
    {
        var announcements = new AgentAnnouncements();
        announcements.Begin("copilot");

        Assert.False(announcements.Take("copilot", "Setting up…", "Registered."));
        Assert.True(announcements.Take("copilot", "Registered.", "Registered."));
        Assert.False(announcements.Take("copilot", "Registered.", "Registered."));
    }

    [Fact]
    public void A_later_setup_with_the_same_result_is_announced_again()
    {
        var announcements = new AgentAnnouncements();
        announcements.Begin("claude");
        Assert.True(announcements.Take("claude", "Repairing…", "Repairing…"));
        Assert.True(announcements.Take("claude", "Repair incomplete.", "Repair incomplete."));

        announcements.Begin("claude");

        Assert.True(announcements.Take("claude", "Repair incomplete.", "Repair incomplete."));
        Assert.True(announcements.Take("copilot", "Registered.", "Registered."));
    }

    [Fact]
    public void Only_a_setup_that_did_not_finish_offers_repair_whatever_the_host_says_of_its_hooks()
    {
        var repair = (from state in Enum.GetValues<ReadinessState>()
                      from detail in new[] { NoRecordIntakeOff, RecordIntakeOff, NoRecordIntakeOn, RecordIntakeOn, UnknownRecord, DidNotComplete }
                      from setup in Enum.GetValues<HookSetupState>()
                      let row = new AgentReadinessRow(Agent("copilot", "Copilot CLI", state, hooksDetail: detail, setup: setup), canManage: true)
                      where row.SetUpVerb == "Repair"
                      select (state, row.Setup)).Distinct().ToList();

        Assert.NotEmpty(repair);
        Assert.All(repair, r => Assert.True(r.Setup == HookSetup.Incomplete || (r.state == ReadinessState.Failed && r.Setup != HookSetup.Unknown), $"{r}"));
    }

    [Fact]
    public void An_agent_that_is_not_installed_has_nothing_to_set_up()
    {
        var row = new AgentReadinessRow(Agent("codex", "Codex", ReadinessState.NeedsAction, installation: ReadinessState.Missing), canManage: true);

        Assert.Null(row.SetUpVerb);
        Assert.False(row.ShowSetUp);
    }

    [Fact]
    public async Task A_setup_says_its_progress_and_result_on_its_own_row_and_not_on_the_others()
    {
        await ConnectedWith(FakeHost.StatusOf() with { Agents = new(DateTimeOffset.UtcNow,
            [Agent("copilot", "Copilot CLI", ReadinessState.NeedsAction), Agent("claude", "Claude Code", ReadinessState.Failed, hooksDetail: DidNotComplete)]) });
        await Wait.UntilAsync(() => settings.Agents.Count == 2, "agent readiness");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnAsync(HostOps.AgentsSetup, async _ =>
        {
            await release.Task.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            return new AgentSetupResult(true, "Registered. Start new agent sessions to verify hooks.", true);
        });
        host.On(HostOps.AgentsRefresh, _ => host.Status.Agents);
        AgentReadinessRow Row(string id) => settings.Agents.Single(a => a.Id == id);

        var pending = settings.SetUpAgentAsync("claude");
        try
        {
            Assert.Equal("Repairing…", Row("claude").Status);
            Assert.Equal(Tone.Progress, Row("claude").StatusTone);
            Assert.Equal("", Row("copilot").Status);
        }
        finally { release.TrySetResult(); await pending; }

        Assert.Equal("Repaired · restart the host, then start a new session to check it", Row("claude").Status);
        Assert.Equal(Tone.Ok, Row("claude").StatusTone);
        Assert.Equal("", Row("copilot").Status);
        Assert.Equal(Tone.None, Row("copilot").StatusTone);
    }

    [Fact]
    public async Task A_rows_setup_result_goes_with_a_fresh_check_or_the_host_that_ran_it()
    {
        await ConnectedWith(FakeHost.StatusOf() with { Agents = new(DateTimeOffset.UtcNow, [Agent("copilot", "Copilot CLI", ReadinessState.NeedsAction)]) });
        await Wait.UntilAsync(() => settings.Agents.Count == 1, "agent readiness");
        host.On(HostOps.AgentsSetup, _ => new AgentSetupResult(false, "Hook setup incomplete: fixture partial work", true));
        host.On(HostOps.AgentsRefresh, _ => host.Status.Agents);

        await settings.SetUpAgentAsync("copilot");
        Assert.Equal(Tone.Bad, settings.Agents.Single().StatusTone);
        await settings.CheckAgentsAsync();
        Assert.Equal("", settings.Agents.Single().Status);

        await settings.SetUpAgentAsync("copilot");
        Assert.NotEqual("", settings.Agents.Single().Status);
        await host.StopAsync();
        await Wait.UntilAsync(() => !settings.HostRunning, "the host gone");
        host.Start();
        await Wait.UntilAsync(() => settings.Agents.Count == 1, "the restarted host's agents");
        Assert.Equal("", settings.Agents.Single().Status);
        Assert.Equal(Tone.None, settings.Agents.Single().StatusTone);
    }

    [Fact]
    public async Task A_setup_the_host_never_answers_says_on_its_row_that_hooks_may_have_changed()
    {
        await ConnectedWith(FakeHost.StatusOf() with { Agents = new(DateTimeOffset.UtcNow, [Agent("copilot", "Copilot CLI", ReadinessState.NeedsAction)]) });
        await Wait.UntilAsync(() => settings.Agents.Count == 1, "agent readiness");
        var never = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnAsync(HostOps.AgentsSetup, _ => never.Task);
        settings.SetupTimeout = TimeSpan.FromMilliseconds(200);

        await settings.SetUpAgentAsync("copilot");

        Assert.Contains("took too long, and some hooks may have changed", settings.Agents.Single().Status);
        Assert.Equal(Tone.Bad, settings.Agents.Single().StatusTone);
    }

    [Fact]
    public async Task A_setup_still_running_when_its_host_goes_says_nothing_on_the_host_that_comes_back()
    {
        await ConnectedWith(FakeHost.StatusOf() with { Agents = new(DateTimeOffset.UtcNow, [Agent("copilot", "Copilot CLI", ReadinessState.NeedsAction)]) });
        await Wait.UntilAsync(() => settings.Agents.Count == 1, "agent readiness");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var never = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnAsync(HostOps.AgentsSetup, _ => { started.TrySetResult(); return never.Task; });

        // The setup's own continuation is held until the host has gone and come back, so its
        // failure lands after the app saw the host go, whichever thread would have been first.
        var held = new HeldContext();
        var ui = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(held);
        Task pending;
        try { pending = settings.SetUpAgentAsync("copilot"); }
        finally { SynchronizationContext.SetSynchronizationContext(ui); }
        await started.Task;
        Assert.Equal("Setting up…", settings.Agents.Single().Status);
        await host.StopAsync();
        await Wait.UntilAsync(() => !settings.HostRunning, "the host gone");
        host.Start();
        await Wait.UntilAsync(() => settings.Agents.Count == 1, "the restarted host's agents");
        await Wait.UntilAsync(() => held.Waiting, "the old setup's failure");
        while (!pending.IsCompleted) { held.Release(); await Task.Delay(10); }
        await pending;

        Assert.Equal("", settings.Agents.Single().Status);
        Assert.Equal(Tone.None, settings.Agents.Single().StatusTone);
    }

    [Fact]
    public async Task A_setup_the_host_refuses_says_why_on_its_row()
    {
        await ConnectedWith(FakeHost.StatusOf() with { Agents = new(DateTimeOffset.UtcNow, [Agent("copilot", "Copilot CLI", ReadinessState.NeedsAction)]) });
        await Wait.UntilAsync(() => settings.Agents.Count == 1, "agent readiness");
        const string refused = "C:\\Users\\dana\\.copilot\\agent-bridge-owner.json belongs to another installation; it was left alone";
        host.On(HostOps.AgentsSetup, _ => throw new InvalidOperationException(refused));

        await settings.SetUpAgentAsync("copilot");

        // Why, from a known set: the refusal's own words name a path, so they stay off the row.
        Assert.Equal("Setup failed · another bridge profile owns its hooks", settings.Agents.Single().Status);
        Assert.Equal(Tone.Bad, settings.Agents.Single().StatusTone);
        Assert.Equal(refused, settings.Error);
    }

    [Fact]
    public async Task Settings_never_sends_setup_to_older_or_read_only_hosts()
    {
        await using var older = new FakeHost();
        older.Start();
        await using var olderConnection = older.Connect(ui);
        var oldPage = new SettingsViewModel(olderConnection, manager, updater, atSignIn, () => prefs, p => prefs = p, "0.1.0", "https://relay.default");
        await Wait.UntilAsync(() => oldPage.HostRunning, "the older host");
        await oldPage.SetUpAgentAsync("claude");
        Assert.Contains("does not support agent setup", oldPage.Error);
        Assert.Empty(older.Sent(HostOps.AgentsSetup));
        await ConnectedWith(FakeHost.StatusOf() with { ReadOnly = "fixture legacy host" });
        await Wait.UntilAsync(() => connection.Status?.ReadOnly is not null, "read-only status");

        Assert.False(settings.CanManageAgents);
        await settings.SetUpAgentAsync("claude", remove: true);

        Assert.Contains("fixture legacy host", settings.Error);
        Assert.Empty(host.Sent(HostOps.AgentsSetup));
    }

    [Fact]
    public async Task A_readiness_probe_failure_is_shown_instead_of_reporting_success()
    {
        host.On(HostOps.AgentsRefresh, _ => throw new InvalidOperationException("readiness scan refused"));
        await Connected();

        await settings.CheckAgentsAsync();

        Assert.Equal("readiness scan refused", settings.Error);
        Assert.False(settings.Busy);
    }

    // The same, for a save the host takes its time over: held by awaiting, not by a pool thread (#511).
    void SavesAsync(Func<System.Text.Json.JsonElement, Task<HostSettingsView>> save) => host.OnAsync(HostOps.Settings, async d =>
    {
        var saved = await save(d);
        host.Status = host.Status with { Settings = saved };
        return new SettingsResult(saved, false);
    });

    [Fact]
    public async Task Signing_in_asks_the_host_through_the_relay_named_and_shows_the_code_it_reports()
    {
        host.On(HostOps.SignIn, _ => new SignInStatus(SignInState.Pending));
        await Connected();
        Assert.Equal("https://relay.default", settings.RelayUrl);

        settings.RelayUrl = " https://relay.example ";
        await settings.SignInAsync();
        host.Status = FakeHost.StatusOf() with { SignIn = new SignInStatus(SignInState.Pending, "ABCD-1234", "https://github.com/login/device") };
        await host.SendAsync(HostOps.StatusEvent, host.Status);

        Assert.Equal("https://relay.example", Assert.Single(host.Sent(HostOps.SignIn)).GetProperty("relay").GetString());
        Assert.Equal("https://relay.example", prefs.Relay);
        await Wait.UntilAsync(() => settings.SignInCode == "ABCD-1234", "the device code");
        Assert.True(settings.SignInPending);
        Assert.False(settings.CanSignIn);
    }

    // Home Assistant refusing this machine is shown in the host's words, a rejected token and a ban
    // alike, and goes when the host says it has; each change is announced, so the page redraws.
    [Fact]
    public async Task Home_Assistant_refusing_this_machine_is_shown_in_the_hosts_words_and_goes_when_the_host_says_so()
    {
        await Connected();
        Assert.False(settings.HasHomeAssistantRefused);
        var told = new List<(string? Said, bool Shown)>();
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is "" or null or nameof(SettingsViewModel.HomeAssistantRefused) or nameof(SettingsViewModel.HasHomeAssistantRefused))
            {
                lock (told) { told.Add((settings.HomeAssistantRefused, settings.HasHomeAssistantRefused)); }
            }
        };
        bool Told((string?, bool) seen) { lock (told) { return told.Contains(seen); } }

        foreach (var said in new[]
        {
            "Home Assistant rejected the token: run `agent-bridge-host ha token set`, then restart the host. Stopped after 3 refused attempts with it",
            "Home Assistant has banned this machine's address; see docs/ha-connector.md, Troubleshooting",
        })
        {
            host.Status = FakeHost.StatusOf() with { HomeAssistant = said };
            await host.SendAsync(HostOps.StatusEvent, host.Status);
            await Wait.UntilAsync(() => Told((said, true)), "the refusal, announced");
            Assert.Equal(said, settings.HomeAssistantRefused);
            Assert.True(settings.HasHomeAssistantRefused);
        }

        host.Status = FakeHost.StatusOf();
        await host.SendAsync(HostOps.StatusEvent, host.Status);
        await Wait.UntilAsync(() => Told((null, false)), "the refusal gone, announced");
        Assert.Null(settings.HomeAssistantRefused);
        Assert.False(settings.HasHomeAssistantRefused);
    }

    [Fact]
    public async Task Turning_off_approval_from_other_devices_writes_it_through_the_host_and_offers_a_restart()
    {
        host.On(HostOps.Settings, _ => new SettingsResult(new HostSettingsView(false), RestartRequired: true));
        await Connected();
        Assert.True(settings.RemoteApproval);

        settings.RemoteApproval = false;
        await Wait.UntilAsync(() => host.Sent(HostOps.Settings).Count == 1, "host.settings");
        host.Status = FakeHost.StatusOf() with { Settings = new HostSettingsView(false), RestartRequired = true };
        await host.SendAsync(HostOps.StatusEvent, host.Status);

        Assert.False(host.Sent(HostOps.Settings)[0].GetProperty("remoteApproval").GetBoolean());
        await Wait.UntilAsync(() => settings.RestartRequired && !settings.RemoteApproval, "the restart offer");
    }

    [Fact]
    public async Task Keeping_a_Dev_Box_awake_is_offered_on_a_Dev_Box_only_and_turned_on_through_the_host()
    {
        Saves(d => new HostSettingsView(true, DevBoxKeepAwake: d.GetProperty("devBoxKeepAwake").GetBoolean()));
        await Connected();
        Assert.False(settings.ShowDevBox);
        // Not a Dev Box: nothing to offer, and nothing is sent.
        settings.DevBoxKeepAwake = true;
        Assert.Empty(host.Sent(HostOps.Settings));

        host.Status = FakeHost.StatusOf() with { DevBox = new DevBoxStatus(false, 15) };
        await host.SendAsync(HostOps.StatusEvent, host.Status);
        await Wait.UntilAsync(() => settings.ShowDevBox, "the Dev Box's setting");
        Assert.False(settings.DevBoxKeepAwake);
        Assert.StartsWith("Off:", settings.DevBoxText);

        settings.DevBoxKeepAwake = true;
        await Wait.UntilAsync(() => host.Sent(HostOps.Settings).Count == 1 && !settings.Busy, "host.settings");
        Assert.True(host.Sent(HostOps.Settings)[0].GetProperty("devBoxKeepAwake").GetBoolean());
        // Acknowledged, but the host's status event has not come yet: the switch says on, and
        // turning it off then is a change, not lost as none.
        Assert.True(settings.DevBoxKeepAwake);
        settings.DevBoxKeepAwake = false;
        await Wait.UntilAsync(() => host.Sent(HostOps.Settings).Count == 2 && !settings.Busy, "the change back");
        Assert.False(host.Sent(HostOps.Settings)[1].GetProperty("devBoxKeepAwake").GetBoolean());
        // Turned on and straight back off before either is answered: off is what is kept.
        settings.DevBoxKeepAwake = true;
        settings.DevBoxKeepAwake = false;
        await Wait.UntilAsync(() => host.Sent(HostOps.Settings).Count == 4 && !settings.Busy, "both changes");
        Assert.False(host.Sent(HostOps.Settings)[3].GetProperty("devBoxKeepAwake").GetBoolean());
        Assert.False(settings.DevBoxKeepAwake);

        // A pass that failed is said in words a person can act on.
        host.Status = FakeHost.StatusOf() with
        {
            Settings = new HostSettingsView(true, DevBoxKeepAwake: true),
            DevBox = new DevBoxStatus(true, 15, new DevBoxPass(Make.T0, "failed", "No Dev Center token. Run 'az login' as this user.", Alarming: true)),
        };
        await host.SendAsync(HostOps.StatusEvent, host.Status);
        await Wait.UntilAsync(() => settings.DevBoxAlarm is not null, "the alarm");
        Assert.True(settings.DevBoxKeepAwake);
        Assert.Contains("az login", settings.DevBoxAlarm);
        Assert.Contains("Last pass at", settings.DevBoxText);

        // A Dev Box whose agent settings the host cannot use is still shown, with its alarm,
        // before any pass has run.
        host.Status = FakeHost.StatusOf() with
        {
            Settings = new HostSettingsView(true, DevBoxKeepAwake: true),
            DevBox = new DevBoxStatus(true, 15, Problem: "its devCenterUri is not a Dev Center address"),
        };
        await host.SendAsync(HostOps.StatusEvent, host.Status);
        await Wait.UntilAsync(() => settings.DevBoxAlarm?.Contains("could not be used") == true, "the settings alarm");
        Assert.True(settings.ShowDevBox);
        Assert.Contains("not a Dev Center address", settings.DevBoxAlarm);
    }

    [Fact]
    public async Task A_keep_awake_change_made_elsewhere_after_this_apps_is_shown_even_when_its_own_status_event_was_missed()
    {
        Saves(d => new HostSettingsView(true, DevBoxKeepAwake: d.GetProperty("devBoxKeepAwake").GetBoolean()));
        host.Status = FakeHost.StatusOf() with { DevBox = new DevBoxStatus(false, 15) };
        await Connected();
        await host.SendAsync(HostOps.StatusEvent, host.Status);
        await Wait.UntilAsync(() => settings.ShowDevBox, "the Dev Box's setting");
        settings.DevBoxKeepAwake = true;
        await Wait.UntilAsync(() => host.Sent(HostOps.Settings).Count == 1 && !settings.Busy, "host.settings");

        // Its status event never came; another app turned it off since, and this status says so.
        host.Status = FakeHost.StatusOf() with { DevBox = new DevBoxStatus(false, 15) };
        await host.SendAsync(HostOps.StatusEvent, host.Status);

        await Wait.UntilAsync(() => !settings.DevBoxKeepAwake, "the host's word, read again");
        // And turning it on again is a change, not discarded as none.
        settings.DevBoxKeepAwake = true;
        await Wait.UntilAsync(() => host.Sent(HostOps.Settings).Count == 2, "host.settings again");
        Assert.True(host.Sent(HostOps.Settings)[1].GetProperty("devBoxKeepAwake").GetBoolean());
    }

    [Fact]
    public async Task Answering_folder_trust_from_devices_is_turned_on_here_with_this_app_approved_and_each_device_is_approved_here()
    {
        const string phone = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2", gone = "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3";
        var link = new FleetTests.FakeFleetLink();
        link.PeopleNow.Add(new FleetHost(phone, "Dana's phone"));
        link.PeopleNow.Add(new FleetHost(link.DeviceId!, "This app, as the relay lists it"));
        var main = new MainViewModel(connection, new CardContext { Deliveries = new Deliveries(connection, ui), Folds = new FoldStore(null), Capabilities = () => connection.Capabilities, Host = connection }, new UsageViewModel(), settings);
        await using var fleet = new Fleet(link, main, ui, h => throw new InvalidOperationException("no machines"));
        Saves(d => new HostSettingsView(true, RemoteFolderTrust: true,
            RemoteFolderTrustDevices: d.TryGetProperty("remoteFolderTrustDevices", out var l) ? [.. l.EnumerateArray().Select(e => e.GetString()!)] : null));
        await Connected();
        Assert.False(settings.RemoteFolderTrust);
        Assert.False(settings.ShowTrustDevices);

        settings.RemoteFolderTrust = true;
        await Wait.UntilAsync(() => host.Sent(HostOps.Settings).Count == 1, "host.settings");
        var first = host.Sent(HostOps.Settings)[0];
        Assert.True(first.GetProperty("remoteFolderTrust").GetBoolean());
        Assert.Equal([link.DeviceId], first.GetProperty("remoteFolderTrustDevices").EnumerateArray().Select(e => e.GetString()));
        await Wait.UntilAsync(() => !settings.Busy, "the change made");

        // Another device approved at the machine's terminal since.
        host.Status = FakeHost.StatusOf() with { Settings = new HostSettingsView(true, RemoteFolderTrust: true, RemoteFolderTrustDevices: [link.DeviceId!, gone]) };
        await host.SendAsync(HostOps.StatusEvent, host.Status);
        await Wait.UntilAsync(() => settings.ShowTrustDevices && settings.TrustDevices.Count == 3, "the devices");
        Assert.Equal([$"This app ({Environment.MachineName}) · a1a1a1a1", "Dana's phone · b2b2b2b2", "A device not connected now · c3c3c3c3"], settings.TrustDevices.Select(d => d.Label));
        Assert.Equal([true, false, true], settings.TrustDevices.Select(d => d.Approved));

        var rows = settings.TrustDevices;
        settings.TrustDevices[1].Approved = true;
        await Wait.UntilAsync(() => host.Sent(HostOps.Settings).Count == 2, "the phone approved");
        Assert.Equal([link.DeviceId, gone, phone], host.Sent(HostOps.Settings)[1].GetProperty("remoteFolderTrustDevices").EnumerateArray().Select(e => e.GetString()));
        Assert.False(host.Sent(HostOps.Settings)[1].TryGetProperty("remoteFolderTrust", out _));
        // A presence update keeps the rows on screen as they are.
        link.Raise();
        await Task.Delay(100);
        Assert.Same(rows, settings.TrustDevices);
    }

    [Fact]
    public async Task Settings_says_which_devices_may_approve_one_with_hidden_characters_and_why_whether_folder_trust_is_on_or_off()
    {
        const string phone = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1";
        var inherited = new HostSettingsView(true, RemoteFolderTrust: true, RemoteFolderTrustDevices: [phone], EscapedApproval: EscapedApproval.Confirm);
        await ConnectedWith(FakeHost.StatusOf() with { Settings = inherited });
        await Wait.UntilAsync(() => settings.EscapedApprovalText.Contains("approved above for folder-trust questions"), "inherited while folder trust is on");

        // Folder trust off, the phone still listed and its row hidden: Approve is the machine's only.
        async Task Now(HostSettingsView view)
        {
            host.Status = FakeHost.StatusOf() with { Settings = view };
            await host.SendAsync(HostOps.StatusEvent, host.Status);
            await Wait.UntilAsync(() => Equals(connection.Status?.Settings, view), "the host's status");
            settings.Refresh();
        }
        await Now(inherited with { RemoteFolderTrust = false });
        Assert.False(settings.ShowTrustDevices);
        Assert.Contains("Approve only at this machine while folder-trust questions are answered only here", settings.EscapedApprovalText);

        // A list of config.json's own is shown either way, by short id.
        await Now(inherited with { RemoteFolderTrust = false, EscapedApprovalDevices = [phone] });
        Assert.Contains("1 device(s) listed in config.json (permissions.escapedApprovalDevices): a1a1a1a1", settings.EscapedApprovalText);
        await Now(inherited with { EscapedApproval = EscapedApproval.Machine });
        Assert.Contains("Approve only at this machine (permissions.escapedApproval)", settings.EscapedApprovalText);
    }

    [Fact]
    public async Task Two_quick_device_changes_go_one_after_the_other_so_neither_undoes_the_other()
    {
        const string a = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", b = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";
        var link = new FleetTests.FakeFleetLink();
        link.PeopleNow.Add(new FleetHost(b, "Dana's phone"));
        var main = new MainViewModel(connection, new CardContext { Deliveries = new Deliveries(connection, ui), Folds = new FoldStore(null), Capabilities = () => connection.Capabilities, Host = connection }, new UsageViewModel(), settings);
        await using var fleet = new Fleet(link, main, ui, h => throw new InvalidOperationException("no machines"));
        Saves(d => new HostSettingsView(true, RemoteFolderTrust: true,
            RemoteFolderTrustDevices: [.. d.GetProperty("remoteFolderTrustDevices").EnumerateArray().Select(e => e.GetString()!)]));
        await ConnectedWith(FakeHost.StatusOf() with { Settings = new HostSettingsView(true, RemoteFolderTrust: true, RemoteFolderTrustDevices: [a]) });
        await Wait.UntilAsync(() => settings.TrustDevices.Count == 2, "the devices");

        // Revoke this app, then approve the phone, before any status event.
        settings.TrustDevices.Single(d => d.Id == a).Approved = false;
        settings.TrustDevices.Single(d => d.Id == b).Approved = true;

        await Wait.UntilAsync(() => host.Sent(HostOps.Settings).Count == 2, "both changes");
        var sent = host.Sent(HostOps.Settings).Select(s => s.GetProperty("remoteFolderTrustDevices").EnumerateArray().Select(e => e.GetString()).ToList()).ToList();
        Assert.Equal([], sent[0]);
        Assert.Equal([b], sent[1]);
    }

    [Fact]
    public async Task A_change_made_after_the_last_is_in_but_before_its_status_event_keeps_what_the_last_one_did()
    {
        const string a = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", b = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";
        var link = new FleetTests.FakeFleetLink();
        link.PeopleNow.Add(new FleetHost(b, "Dana's phone"));
        var main = new MainViewModel(connection, new CardContext { Deliveries = new Deliveries(connection, ui), Folds = new FoldStore(null), Capabilities = () => connection.Capabilities, Host = connection }, new UsageViewModel(), settings);
        await using var fleet = new Fleet(link, main, ui, h => throw new InvalidOperationException("no machines"));
        Saves(d => new HostSettingsView(true, RemoteFolderTrust: true,
            RemoteFolderTrustDevices: [.. d.GetProperty("remoteFolderTrustDevices").EnumerateArray().Select(e => e.GetString()!)]));
        await ConnectedWith(FakeHost.StatusOf() with { Settings = new HostSettingsView(true, RemoteFolderTrust: true, RemoteFolderTrustDevices: [a]) });
        await Wait.UntilAsync(() => settings.TrustDevices.Count == 2, "the devices");

        // Revoked, and the host's reply is in; its status event is not.
        settings.TrustDevices.Single(d => d.Id == a).Approved = false;
        await Wait.UntilAsync(() => host.Sent(HostOps.Settings).Count == 1 && !settings.Busy, "the revocation made");
        Assert.Equal([a], connection.Status!.Settings.RemoteFolderTrustDevices!);
        // Shown as the host acknowledged it, not as the status from before says.
        Assert.False(settings.TrustDevices.Single(d => d.Id == a).Approved);

        settings.TrustDevices.Single(d => d.Id == b).Approved = true;
        await Wait.UntilAsync(() => host.Sent(HostOps.Settings).Count == 2, "the phone approved");
        Assert.Equal([b], host.Sent(HostOps.Settings)[1].GetProperty("remoteFolderTrustDevices").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task A_status_that_arrives_while_a_change_is_on_its_way_is_checked_once_the_change_is_in()
    {
        const string a = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", b = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";
        var link = new FleetTests.FakeFleetLink();
        link.PeopleNow.Add(new FleetHost(b, "Dana's phone"));
        var main = new MainViewModel(connection, new CardContext { Deliveries = new Deliveries(connection, ui), Folds = new FoldStore(null), Capabilities = () => connection.Capabilities, Host = connection }, new UsageViewModel(), settings);
        await using var fleet = new Fleet(link, main, ui, h => throw new InvalidOperationException("no machines"));
        host.OnAsync(HostOps.Settings, async d =>
        {
            var saved = new HostSettingsView(true, RemoteFolderTrust: true,
                RemoteFolderTrustDevices: [.. d.GetProperty("remoteFolderTrustDevices").EnumerateArray().Select(e => e.GetString()!)]);
            // Saved, then this app revoked at the machine's terminal before the reply went: that
            // status arrives first.
            host.Status = host.Status with { Settings = new HostSettingsView(true, RemoteFolderTrust: true, RemoteFolderTrustDevices: [b]) };
            await host.SendAsync(HostOps.StatusEvent, host.Status);
            return new SettingsResult(saved, false);
        });
        await ConnectedWith(FakeHost.StatusOf() with { Settings = new HostSettingsView(true, RemoteFolderTrust: true, RemoteFolderTrustDevices: [a]) });
        await Wait.UntilAsync(() => settings.TrustDevices.Count == 2, "the devices");

        settings.TrustDevices.Single(d => d.Id == b).Approved = true;

        await Wait.UntilAsync(() => host.Sent(HostOps.Settings).Count == 1 && !settings.Busy, "the phone approved");
        await Wait.UntilAsync(() => !settings.TrustDevices.Single(d => d.Id == a).Approved && settings.TrustDevices.Single(d => d.Id == b).Approved, "what the host has now");
    }

    [Fact]
    public async Task Turned_on_with_none_approved_this_apps_box_is_ticked_once_the_change_is_in_and_a_refused_switch_shows_the_hosts_word()
    {
        var link = new FleetTests.FakeFleetLink();
        var main = new MainViewModel(connection, new CardContext { Deliveries = new Deliveries(connection, ui), Folds = new FoldStore(null), Capabilities = () => connection.Capabilities, Host = connection }, new UsageViewModel(), settings);
        await using var fleet = new Fleet(link, main, ui, h => throw new InvalidOperationException("no machines"));
        Saves(d => new HostSettingsView(true,
            RemoteFolderTrust: d.TryGetProperty("remoteFolderTrust", out var t) ? t.GetBoolean() : host.Status.Settings.RemoteFolderTrust,
            RemoteFolderTrustDevices: d.TryGetProperty("remoteFolderTrustDevices", out var l) ? [.. l.EnumerateArray().Select(e => e.GetString()!)] : host.Status.Settings.RemoteFolderTrustDevices));
        await Connected();
        await Wait.UntilAsync(() => settings.TrustDevices.Count == 1, "this app's row");

        // No status event follows: the row shows the host's reply once the change is in.
        settings.RemoteFolderTrust = true;
        await Wait.UntilAsync(() => host.Sent(HostOps.Settings).Count == 1 && !settings.Busy, "the change made");
        await Wait.UntilAsync(() => settings.TrustDevices.Single().Approved, "this app's box ticked");

        // Refused: the switch is told the host's word again after the change it asked for.
        var told = new List<bool>();
        settings.PropertyChanged += (_, e) => { if (e.PropertyName is "" or nameof(SettingsViewModel.RemoteFolderTrust)) { lock (told) { told.Add(settings.RemoteFolderTrust); } } };
        host.On(HostOps.Settings, _ => throw new InvalidOperationException("the config file is locked"));
        settings.RemoteFolderTrust = false;
        await Wait.UntilAsync(() => settings.HasError && !settings.Busy, "the refusal");
        await Wait.UntilAsync(() => { lock (told) { return told.Count > 0 && told[^1]; } }, "the switch told it is still on");
        Assert.True(settings.RemoteFolderTrust);
    }

    [Fact]
    public async Task A_status_that_arrives_during_a_change_that_fails_is_still_checked()
    {
        const string a = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", b = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";
        var link = new FleetTests.FakeFleetLink();
        link.PeopleNow.Add(new FleetHost(b, "Dana's phone"));
        var main = new MainViewModel(connection, new CardContext { Deliveries = new Deliveries(connection, ui), Folds = new FoldStore(null), Capabilities = () => connection.Capabilities, Host = connection }, new UsageViewModel(), settings);
        await using var fleet = new Fleet(link, main, ui, h => throw new InvalidOperationException("no machines"));
        Saves(d => new HostSettingsView(true, RemoteFolderTrust: true,
            RemoteFolderTrustDevices: [.. d.GetProperty("remoteFolderTrustDevices").EnumerateArray().Select(e => e.GetString()!)]));
        await ConnectedWith(FakeHost.StatusOf() with { Settings = new HostSettingsView(true, RemoteFolderTrust: true, RemoteFolderTrustDevices: [a]) });
        await Wait.UntilAsync(() => settings.TrustDevices.Count == 2, "the devices");
        // The phone approved; the reply is in and held, its status event never sent.
        settings.TrustDevices.Single(d => d.Id == b).Approved = true;
        await Wait.UntilAsync(() => host.Sent(HostOps.Settings).Count == 1 && !settings.Busy, "the phone approved");

        // The next change fails, after the phone was revoked at the terminal and that status came.
        host.OnAsync(HostOps.Settings, async _ =>
        {
            host.Status = host.Status with { Settings = new HostSettingsView(true, RemoteFolderTrust: true, RemoteFolderTrustDevices: [a]) };
            await host.SendAsync(HostOps.StatusEvent, host.Status);
            throw new InvalidOperationException("the config file is locked");
        });
        settings.TrustDevices.Single(d => d.Id == a).Approved = false;

        await Wait.UntilAsync(() => settings.HasError && !settings.Busy, "the refusal");
        await Wait.UntilAsync(() => !settings.TrustDevices.Single(d => d.Id == b).Approved && settings.TrustDevices.Single(d => d.Id == a).Approved, "what the host has now");
    }

    [Fact]
    public async Task A_device_revoked_at_the_terminal_while_another_is_approved_here_stays_revoked()
    {
        const string a = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", b = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2", c = "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3";
        var link = new FleetTests.FakeFleetLink();
        link.PeopleNow.Add(new FleetHost(b, "Dana's phone"));
        link.PeopleNow.Add(new FleetHost(c, "Dana's browser"));
        var main = new MainViewModel(connection, new CardContext { Deliveries = new Deliveries(connection, ui), Folds = new FoldStore(null), Capabilities = () => connection.Capabilities, Host = connection }, new UsageViewModel(), settings);
        await using var fleet = new Fleet(link, main, ui, h => throw new InvalidOperationException("no machines"));
        static string[] List(System.Text.Json.JsonElement d, string name) => d.TryGetProperty(name, out var l) ? [.. l.EnumerateArray().Select(e => e.GetString()!)] : [];
        var calls = 0;
        // As the host does: a list made from one that is no longer saved changes nothing.
        host.On(HostOps.Settings, d =>
        {
            // The phone is revoked at the machine's terminal just as the first change goes.
            if (Interlocked.Increment(ref calls) == 1) { host.Status = host.Status with { Settings = new HostSettingsView(true, RemoteFolderTrust: true, RemoteFolderTrustDevices: [a]) }; }
            var current = host.Status.Settings.RemoteFolderTrustDevices ?? [];
            if (d.TryGetProperty("remoteFolderTrustDevicesWas", out _) && !List(d, "remoteFolderTrustDevicesWas").ToHashSet().SetEquals(current)) { return new SettingsResult(host.Status.Settings, false, Conflict: true); }
            host.Status = host.Status with { Settings = new HostSettingsView(true, RemoteFolderTrust: true, RemoteFolderTrustDevices: List(d, "remoteFolderTrustDevices")) };
            return new SettingsResult(host.Status.Settings, false);
        });
        await ConnectedWith(FakeHost.StatusOf() with { Settings = new HostSettingsView(true, RemoteFolderTrust: true, RemoteFolderTrustDevices: [a, b]) });
        await Wait.UntilAsync(() => settings.TrustDevices.Count == 3, "the devices");

        settings.TrustDevices.Single(d => d.Id == c).Approved = true;

        await Wait.UntilAsync(() => host.Sent(HostOps.Settings).Count == 2 && !settings.Busy, "the browser approved");
        Assert.Equal([a, c], List(host.Sent(HostOps.Settings)[1], "remoteFolderTrustDevices"));
        Assert.Equal([a, c], host.Status.Settings.RemoteFolderTrustDevices!);
        Assert.False(settings.HasError);
        await Wait.UntilAsync(() => !settings.TrustDevices.Single(d => d.Id == b).Approved && settings.TrustDevices.Single(d => d.Id == c).Approved, "the phone still revoked");
    }

    [Fact]
    public async Task A_device_that_connects_while_a_change_is_on_its_way_leaves_the_boxes_showing_what_was_asked()
    {
        const string a = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", b = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2", c = "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3";
        var link = new FleetTests.FakeFleetLink();
        link.PeopleNow.Add(new FleetHost(b, "Dana's phone"));
        var main = new MainViewModel(connection, new CardContext { Deliveries = new Deliveries(connection, ui), Folds = new FoldStore(null), Capabilities = () => connection.Capabilities, Host = connection }, new UsageViewModel(), settings);
        await using var fleet = new Fleet(link, main, ui, h => throw new InvalidOperationException("no machines"));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SavesAsync(async d =>
        {
            await FakeHost.Until(gate.Task);
            return new HostSettingsView(true, RemoteFolderTrust: true, RemoteFolderTrustDevices: [.. d.GetProperty("remoteFolderTrustDevices").EnumerateArray().Select(e => e.GetString()!)]);
        });
        await ConnectedWith(FakeHost.StatusOf() with { Settings = new HostSettingsView(true, RemoteFolderTrust: true, RemoteFolderTrustDevices: [a]) });
        await Wait.UntilAsync(() => settings.TrustDevices.Count == 2 && settings.TrustDevices.Single(d => d.Id == a).Approved, "the devices");

        try
        {
            settings.TrustDevices.Single(d => d.Id == b).Approved = true;
            await Wait.UntilAsync(() => host.Sent(HostOps.Settings).Count == 1, "the phone's approval sent");
            // A browser connects while it is on its way: the rows are made again, the phone's still ticked.
            link.PeopleNow.Add(new FleetHost(c, "Dana's browser"));
            link.Raise();
            await Wait.UntilAsync(() => settings.TrustDevices.Count == 3, "the browser's row");
            Assert.Equal([true, true, false], settings.TrustDevices.Select(d => d.Approved));
        }
        finally { gate.TrySetResult(); }
        await Wait.UntilAsync(() => !settings.Busy, "the phone approved");
        Assert.Equal([true, true, false], settings.TrustDevices.Select(d => d.Approved));
    }

    [Fact]
    public async Task A_box_not_being_changed_shows_the_hosts_word_while_another_change_is_on_its_way()
    {
        const string a = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", b = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";
        var link = new FleetTests.FakeFleetLink();
        link.PeopleNow.Add(new FleetHost(b, "Dana's phone"));
        var main = new MainViewModel(connection, new CardContext { Deliveries = new Deliveries(connection, ui), Folds = new FoldStore(null), Capabilities = () => connection.Capabilities, Host = connection }, new UsageViewModel(), settings);
        await using var fleet = new Fleet(link, main, ui, h => throw new InvalidOperationException("no machines"));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SavesAsync(async d =>
        {
            await FakeHost.Until(gate.Task);
            return new HostSettingsView(true, RemoteFolderTrust: true, RemoteFolderTrustDevices: [.. d.GetProperty("remoteFolderTrustDevices").EnumerateArray().Select(e => e.GetString()!)]);
        });
        await ConnectedWith(FakeHost.StatusOf() with { Settings = new HostSettingsView(true, RemoteFolderTrust: true, RemoteFolderTrustDevices: []) });
        await Wait.UntilAsync(() => settings.TrustDevices.Count == 2, "the devices");

        try
        {
            // A click runs on the UI thread, not concurrently with a posted redraw.
            ui.Post(() => settings.TrustDevices.Single(d => d.Id == b).Approved = true);
            await Wait.UntilAsync(() => host.Sent(HostOps.Settings).Count == 1, "the phone's approval sent");
            // This app approved at the machine's terminal meanwhile: its box says so at once.
            host.Status = host.Status with { Settings = new HostSettingsView(true, RemoteFolderTrust: true, RemoteFolderTrustDevices: [a]) };
            await host.SendAsync(HostOps.StatusEvent, host.Status);
            await Wait.UntilAsync(() => settings.TrustDevices.Single(d => d.Id == a).Approved, "this app's box ticked");
            Assert.True(settings.TrustDevices.Single(d => d.Id == b).Approved);
        }
        finally { gate.TrySetResult(); }
    }

    [Fact]
    public async Task This_app_allowed_from_the_Launch_page_is_ticked_here_even_when_its_status_comes_before_the_reply()
    {
        var link = new FleetTests.FakeFleetLink();
        var main = new MainViewModel(connection, new CardContext { Deliveries = new Deliveries(connection, ui), Folds = new FoldStore(null), Capabilities = () => connection.Capabilities, Host = connection }, new UsageViewModel(), settings);
        await using var fleet = new Fleet(link, main, ui, h => throw new InvalidOperationException("no machines"));
        host.OnAsync(HostOps.Settings, async d =>
        {
            var saved = new HostSettingsView(true, RemoteFolderTrust: true, RemoteFolderTrustDevices: [.. d.GetProperty("remoteFolderTrustDevices").EnumerateArray().Select(e => e.GetString()!)]);
            host.Status = host.Status with { Settings = saved };
            await host.SendAsync(HostOps.StatusEvent, host.Status);
            return new SettingsResult(saved, false);
        });
        await ConnectedWith(FakeHost.StatusOf() with { Settings = new HostSettingsView(true, RemoteFolderTrust: true, RemoteFolderTrustDevices: []) });
        await Wait.UntilAsync(() => settings.TrustDevices.Count == 1, "this app's row");
        Assert.False(settings.TrustDevices.Single().Approved);

        // As the Launch page's "Allow answering from this app" does.
        await TrustEditor.For(connection).EditAsync(now => new SettingsRequest(RemoteFolderTrustDevices: [.. now.RemoteFolderTrustDevices ?? [], link.DeviceId!]));

        await Wait.UntilAsync(() => settings.TrustDevices.Single().Approved, "this app's box ticked");
    }

    [Fact]
    public async Task A_status_that_comes_while_the_host_is_read_again_is_checked_after_the_read()
    {
        const string a = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", b = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";
        var link = new FleetTests.FakeFleetLink();
        link.PeopleNow.Add(new FleetHost(b, "Dana's phone"));
        var main = new MainViewModel(connection, new CardContext { Deliveries = new Deliveries(connection, ui), Folds = new FoldStore(null), Capabilities = () => connection.Capabilities, Host = connection }, new UsageViewModel(), settings);
        await using var fleet = new Fleet(link, main, ui, h => throw new InvalidOperationException("no machines"));
        Saves(d => new HostSettingsView(true, RemoteFolderTrust: true,
            RemoteFolderTrustDevices: [.. d.GetProperty("remoteFolderTrustDevices").EnumerateArray().Select(e => e.GetString()!)]));
        var before = FakeHost.StatusOf() with { Settings = new HostSettingsView(true, RemoteFolderTrust: true, RemoteFolderTrustDevices: [a]) };
        await ConnectedWith(before);
        await Wait.UntilAsync(() => settings.TrustDevices.Count == 2, "the devices");
        // The phone approved; the reply is in and held, its status event not sent yet.
        settings.TrustDevices.Single(d => d.Id == b).Approved = true;
        await Wait.UntilAsync(() => host.Sent(HostOps.Settings).Count == 1 && !settings.Busy, "the phone approved");
        var approved = host.Status;

        // A status from before arrives late, so the host is read again; that read is answered
        // from before the phone's status event, which comes while it is on its way.
        var reads = 0;
        host.OnAsync(HostOps.Status, async _ =>
        {
            if (Interlocked.Increment(ref reads) > 1) { return host.Status; }
            await host.SendAsync(HostOps.StatusEvent, approved);
            return before;
        });
        await host.SendAsync(HostOps.StatusEvent, before);

        await Wait.UntilAsync(() => Volatile.Read(ref reads) >= 1, "the host read again");
        // Its answer, older than the status that came meanwhile, has been taken in by now.
        await Task.Delay(300);
        await Wait.UntilAsync(() => settings.TrustDevices.Single(d => d.Id == b).Approved, "what the host has now");
    }
    [Fact]
    public async Task A_device_ticked_and_unticked_quickly_ends_unapproved_and_so_does_the_switch()
    {
        const string a = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", b = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";
        var link = new FleetTests.FakeFleetLink();
        link.PeopleNow.Add(new FleetHost(b, "Dana's phone"));
        var main = new MainViewModel(connection, new CardContext { Deliveries = new Deliveries(connection, ui), Folds = new FoldStore(null), Capabilities = () => connection.Capabilities, Host = connection }, new UsageViewModel(), settings);
        await using var fleet = new Fleet(link, main, ui, h => throw new InvalidOperationException("no machines"));
        Saves(d => new HostSettingsView(true,
            RemoteFolderTrust: d.TryGetProperty("remoteFolderTrust", out var t) ? t.GetBoolean() : host.Status.Settings.RemoteFolderTrust,
            RemoteFolderTrustDevices: d.TryGetProperty("remoteFolderTrustDevices", out var l) ? [.. l.EnumerateArray().Select(e => e.GetString()!)] : host.Status.Settings.RemoteFolderTrustDevices));
        await ConnectedWith(FakeHost.StatusOf() with { Settings = new HostSettingsView(true, RemoteFolderTrust: true, RemoteFolderTrustDevices: [a]) });
        await Wait.UntilAsync(() => settings.TrustDevices.Count == 2, "the devices");

        var phone = settings.TrustDevices.Single(d => d.Id == b);
        phone.Approved = true;
        phone.Approved = false;
        await Wait.UntilAsync(() => host.Sent(HostOps.Settings).Count == 2, "both clicks");
        Assert.Equal([a], host.Sent(HostOps.Settings)[1].GetProperty("remoteFolderTrustDevices").EnumerateArray().Select(e => e.GetString()));
        Assert.False(phone.Approved);

        settings.RemoteFolderTrust = false;
        settings.RemoteFolderTrust = true;
        await Wait.UntilAsync(() => host.Sent(HostOps.Settings).Count == 4, "both switches");
        Assert.True(host.Sent(HostOps.Settings)[3].GetProperty("remoteFolderTrust").GetBoolean());
    }

    [Fact]
    public async Task A_switch_set_to_what_it_already_says_installs_or_sends_nothing()
    {
        await Connected();
        await Wait.UntilAsync(() => !settings.Busy, "nothing under way");

        // What a binding does when it refreshes: the same values pushed back. A setter acts only
        // through RunAsync, which is busy from the moment it starts until its host call is
        // answered, so a page not busy now started nothing, and nothing is on its way.
        settings.StartAtSignIn = false;
        settings.AppStartsAtSignIn = false;
        settings.RemoteApproval = true;
        Assert.False(settings.Busy);

        Assert.Empty(tasks.Registered);
        Assert.Empty(atSignIn.Sets);
        Assert.Empty(host.Sent(HostOps.Settings));

        settings.StartAtSignIn = true;
        settings.AppStartsAtSignIn = true;
        await Wait.UntilAsync(() => tasks.Registered.Count == 1 && atSignIn.Sets.Count == 1, "the task and the Run entry");
        Assert.True(settings.StartAtSignIn);
    }

    [Fact]
    public async Task Restarting_a_host_installed_beside_the_app_goes_through_its_logon_task()
    {
        await Connected();
        settings.StartAtSignIn = true;
        await Wait.UntilAsync(() => tasks.Registered.Count == 1, "the task");
        tasks.OnRun = () => host.Start();

        await settings.RestartHostAsync();

        Assert.Null(settings.Error);
        Assert.Single(host.Sent(HostOps.Stop));
        Assert.Equal([tasks.Registered.Keys.Single()], tasks.Ran);
        Assert.Empty(host.Sent(HostOps.Restart));
    }

    [Fact]
    public async Task An_update_stops_the_host_before_the_app_restarts_into_the_new_version()
    {
        await Connected();
        var stoppedFirst = false;
        updater.OnApply = () => stoppedFirst = host.Sent(HostOps.Stop).Count == 1;

        await settings.CheckForUpdatesAsync();
        Assert.True(settings.UpdateAvailable);
        Assert.Equal("Version 0.2.0 is available.", settings.UpdateStatus);
        await settings.InstallUpdateAsync();

        Assert.Null(settings.Error);
        Assert.True(updater.Applied);
        Assert.True(stoppedFirst);
    }

    [Fact]
    public async Task A_check_GitHub_is_limiting_says_when_the_next_one_is_as_the_status_not_as_an_error_or_up_to_date()
    {
        await Connected();
        var limited = new AgentBridge.Host.Core.Updates.ReleaseDiscoveryException(
            "GitHub is limiting how often this network can check for updates.", DateTimeOffset.UtcNow + TimeSpan.FromMinutes(5), limited: true);
        updater.CheckFails = limited;

        await settings.CheckForUpdatesAsync();

        Assert.Null(settings.Error);
        Assert.StartsWith("GitHub is limiting how often this network can check for updates.", settings.UpdateStatus);
        // Its times said here, in this machine's local time.
        Assert.Equal(limited.Message, settings.UpdateStatus);
        Assert.False(settings.UpdateAvailable);
    }

    [Fact]
    public async Task A_release_downloaded_in_the_background_is_offered_in_Settings_as_Restart_to_update_without_a_check_there()
    {
        await Connected();
        // The background download, through the updater Settings shares.
        await updater.DownloadAsync(_ => { });
        settings.UpdatesChanged();

        Assert.True(settings.UpdateAvailable);
        Assert.Equal("Version 0.2.0 is ready. Restart to update.", settings.UpdateStatus);
        await settings.InstallUpdateAsync();

        Assert.Null(settings.Error);
        Assert.True(updater.Applied);
    }

    [Fact]
    public async Task Settings_Restart_to_update_takes_the_apps_own_path_and_says_why_it_was_refused()
    {
        await Connected();
        const string why = "another profile's host (pid 77) runs from this installation";
        var asked = 0;
        settings.RestartDownloaded = _ => { asked++; return Task.FromResult<string?>(why); };
        await updater.DownloadAsync(_ => { });

        await settings.InstallUpdateAsync();

        Assert.Equal(1, asked);
        Assert.Equal(AppUpdateViewModel.DidNotInstall(why, again: true), settings.Error);
        Assert.False(updater.Applied);
        Assert.Empty(host.Sent(HostOps.Stop));
    }

    [Fact]
    public async Task Up_to_date_from_an_earlier_check_gives_way_to_a_release_downloaded_in_the_background_since()
    {
        await Connected();
        updater.Found = null;
        await settings.CheckForUpdatesAsync();
        Assert.Equal("Up to date (0.1.0).", settings.UpdateStatus);

        await updater.DownloadAsync(_ => { });
        settings.UpdatesChanged();

        Assert.True(settings.UpdateAvailable);
        Assert.Equal("Version 0.2.0 is ready. Restart to update.", settings.UpdateStatus);
    }

    [Fact]
    public async Task A_release_Settings_said_was_ready_and_replaced_in_the_background_since_is_no_longer_offered()
    {
        await Connected();
        await updater.DownloadAsync(_ => { });
        await settings.CheckForUpdatesAsync();
        Assert.Equal("Version 0.2.0 is ready. Restart to update.", settings.UpdateStatus);

        // A background check found a newer release, whose download has not finished.
        updater.Ready = null;
        settings.UpdatesChanged();

        Assert.False(settings.UpdateAvailable);
        Assert.Equal("Version 0.2.0 is no longer the newest; check for updates again.", settings.UpdateStatus);
    }

    [Fact]
    public async Task A_release_the_person_said_Not_now_to_and_replaced_in_the_background_since_is_no_longer_offered()
    {
        await Connected();
        settings.RestartDownloaded = _ => Task.FromResult<string?>(AutoUpdater.NotNow);
        await updater.DownloadAsync(_ => { });
        await settings.CheckForUpdatesAsync();
        await settings.InstallUpdateAsync(_ => Task.FromResult(false));

        updater.Ready = null;
        settings.UpdatesChanged();

        Assert.False(settings.UpdateAvailable);
        Assert.Equal("Version 0.2.0 is no longer the newest; check for updates again.", settings.UpdateStatus);
    }

    // Replaced while Settings was at work (here, while the person was asked): settled once it is idle.
    [Fact]
    public async Task A_release_replaced_while_Settings_was_busy_is_no_longer_offered_once_Settings_is_idle()
    {
        await Connected();
        await updater.DownloadAsync(_ => { });
        settings.RestartDownloaded = _ =>
        {
            updater.Ready = null;
            settings.UpdatesChanged();
            return Task.FromResult<string?>(AutoUpdater.NotNow);
        };

        await settings.InstallUpdateAsync(_ => Task.FromResult(false));

        Assert.False(settings.UpdateAvailable);
        Assert.Equal("Version 0.2.0 is no longer the newest; check for updates again.", settings.UpdateStatus);
    }

    // The worst ordering (Copilot's review of 746a8616): Settings is at work on the release it saw
    // ready when a background check replaces it, and the replacement's download then fails, both
    // said while Settings is busy. Its progress stays until it is done, and the stale release goes
    // then. Gated, no sleeps.
    [Fact]
    public async Task A_replacement_whose_download_fails_while_Settings_is_busy_leaves_no_stale_release_once_Settings_is_idle()
    {
        await Connected();
        await updater.DownloadAsync(_ => { });
        await settings.CheckForUpdatesAsync();
        var asked = new TaskCompletionSource<string?>();
        settings.RestartDownloaded = _ => asked.Task;
        var install = settings.InstallUpdateAsync(_ => Task.FromResult(true));
        Assert.True(settings.Busy);

        // The background check finds 0.3.0, dropping 0.2.0 (as Velopack's does) and saying so...
        updater.Ready = null;
        settings.UpdatesChanged();
        // ...then its download fails, and that is said too.
        updater.DownloadFails = new IOException("the connection was reset");
        await Assert.ThrowsAsync<IOException>(() => updater.DownloadAsync(_ => { }));
        settings.UpdatesChanged();
        Assert.Equal("Stopping the host to update it…", settings.UpdateStatus);

        asked.SetResult("the update downloaded has changed since (0.2.0 is no longer the newest for this machine): the newest is being downloaded now, so try again in a moment");
        await install;

        Assert.False(settings.UpdateAvailable);
        Assert.Equal("Version 0.2.0 is no longer the newest; check for updates again.", settings.UpdateStatus);
        // And an event after Settings went idle changes nothing.
        settings.UpdatesChanged();
        Assert.False(settings.UpdateAvailable);
    }

    // Turning "Include pre-releases" off or on changes the updater's source, which drops what it
    // found and downloaded without a word (VelopackUpdater.Manager reads the choice when next used).
    [Fact]
    public async Task Changing_the_pre_release_choice_stops_offering_what_was_found_or_ready_on_the_old_source()
    {
        await Connected();
        await updater.DownloadAsync(_ => { });
        await settings.CheckForUpdatesAsync();
        Assert.True(settings.UpdateAvailable);

        updater.Ready = null;
        settings.Prereleases = !settings.Prereleases;

        Assert.False(settings.UpdateAvailable);
        Assert.DoesNotContain("0.2.0", settings.UpdateStatus);
    }

    // Include pre-releases changed while Settings was busy with something else: the release a
    // check here found on the old source, never downloaded, is not offered once Settings is idle.
    [Fact]
    public async Task A_release_found_on_the_old_source_is_not_offered_after_the_pre_release_choice_changes_while_Settings_is_busy()
    {
        await Connected();
        await settings.CheckForUpdatesAsync();
        Assert.Equal("Version 0.2.0 is available.", settings.UpdateStatus);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnAsync(HostOps.AgentsInstall, async _ =>
        {
            entered.SetResult();
            await FakeHost.Until(release.Task);
            return new AgentInstallResult(true, "Fixture executable installed.", "fixture.exe");
        });
        host.On(HostOps.AgentsRefresh, _ => Readiness("1.2.3"));
        var busy = settings.InstallAgentAsync("claude");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(settings.Busy);

        settings.Prereleases = !settings.Prereleases;
        release.SetResult();
        await busy;

        Assert.False(settings.Busy);
        Assert.False(settings.UpdateAvailable);
        Assert.DoesNotContain("0.2.0", settings.UpdateStatus);
    }

    // Copilot's review of ac778916: "Up to date" from a check here came back once a background
    // download since was gone, hiding the newer release a background check had found.
    [Fact]
    public async Task Up_to_date_from_a_check_here_never_hides_a_newer_release_found_in_the_background_since()
    {
        await Connected();
        var observed = new ObservedUpdater(updater);
        var page = new SettingsViewModel(connection, manager, observed, atSignIn, () => prefs, p => prefs = p, "0.1.0", "https://relay.default");
        updater.Found = null;
        await page.CheckForUpdatesAsync();
        Assert.Equal("Up to date (0.1.0).", page.UpdateStatus);

        // In the background: 0.2.0 found and downloaded, then 0.3.0 found, which drops it, and
        // whose download fails.
        updater.Found = "0.2.0";
        await observed.CheckAsync();
        await observed.DownloadAsync(_ => { });
        page.UpdatesChanged();
        updater.Found = "0.3.0";
        await observed.CheckAsync();
        updater.Ready = null;
        page.UpdatesChanged();

        Assert.DoesNotContain("Up to date", page.UpdateStatus);
        Assert.Contains("0.3.0", page.UpdateStatus);
    }

    // Copilot's review of ac778916: a check here that found nothing left "Up to date" standing
    // after Include pre-releases changed, though the new source was never asked.
    [Fact]
    public async Task Up_to_date_from_the_old_source_goes_when_the_pre_release_choice_changes()
    {
        await Connected();
        updater.Found = null;
        await settings.CheckForUpdatesAsync();
        Assert.Equal("Up to date (0.1.0).", settings.UpdateStatus);

        settings.Prereleases = !settings.Prereleases;

        Assert.DoesNotContain("Up to date", settings.UpdateStatus);
    }

    // Copilot's review of 9e1ee18f: a release a check here found, never downloaded, then replaced by
    // a background check whose own download fails, was still offered, and its Restart to update
    // could only fail. Through the ObservedUpdater the app shares, as App wires it.
    [Fact]
    public async Task A_release_found_here_and_replaced_by_a_background_check_whose_download_fails_is_no_longer_offered()
    {
        await Connected();
        var observed = new ObservedUpdater(updater);
        var page = new SettingsViewModel(connection, manager, observed, atSignIn, () => prefs, p => prefs = p, "0.1.0", "https://relay.default");
        await page.CheckForUpdatesAsync();
        Assert.Equal("Version 0.2.0 is available.", page.UpdateStatus);
        page.UpdatesChanged();
        Assert.True(page.UpdateAvailable);

        updater.Found = "0.3.0";
        Assert.Equal("0.3.0", await observed.CheckAsync());
        updater.DownloadFails = new IOException("the connection was reset");
        await Assert.ThrowsAsync<IOException>(() => observed.DownloadAsync(_ => { }));
        page.UpdatesChanged();

        Assert.False(page.UpdateAvailable);
        Assert.Equal("Version 0.2.0 is no longer the newest; check for updates again.", page.UpdateStatus);
    }

    // Copilot's summary of 5d32c599: what the last check found on the old source was still what the
    // machine reported as available to its devices after Include pre-releases changed.
    [Fact]
    public async Task Changing_the_pre_release_choice_stops_reporting_the_old_sources_find_and_says_so()
    {
        await Connected();
        var observed = new ObservedUpdater(updater);
        var page = new SettingsViewModel(connection, manager, observed, atSignIn, () => prefs, p => prefs = p, "0.1.0", "https://relay.default");
        await page.CheckForUpdatesAsync();
        Assert.Equal("0.2.0", observed.Found);
        var changes = 0;
        observed.Changed += () => changes++;

        page.Prereleases = !page.Prereleases;

        Assert.Null(observed.Found);
        Assert.Null(observed.CheckedAt);
        Assert.True(changes > 0);
    }

    // Copilot's review of 35d9157a: the update line changes on its own, so it is announced, once
    // per change (ui.md §9.1 A6); what Settings' own Check says is not announced as such.
    [Fact]
    public async Task The_update_line_is_announced_once_when_it_changes_on_its_own_and_not_for_Settings_own_check()
    {
        await Connected();
        var said = 0;
        settings.UpdateStatusAnnounced += () => said++;

        await settings.CheckForUpdatesAsync();
        settings.UpdatesChanged();
        Assert.Equal(0, said);

        await updater.DownloadAsync(_ => { });
        settings.UpdatesChanged();
        settings.UpdatesChanged();

        Assert.Equal(1, said);
        Assert.Equal("Version 0.2.0 is ready. Restart to update.", settings.UpdateStatus);
    }

    // Copilot's review of 9039771d: a release downloaded in the background while Settings was busy
    // with something else was recorded as seen when it went idle, and never announced.
    [Fact]
    public async Task A_release_ready_while_Settings_is_busy_with_something_else_is_announced_once_when_it_is_idle()
    {
        await Connected();
        var said = 0;
        settings.UpdateStatusAnnounced += () => said++;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.OnAsync(HostOps.AgentsInstall, async _ =>
        {
            entered.SetResult();
            await FakeHost.Until(release.Task);
            return new AgentInstallResult(true, "Fixture executable installed.", "fixture.exe");
        });
        host.On(HostOps.AgentsRefresh, _ => Readiness("1.2.3"));
        var busy = settings.InstallAgentAsync("claude");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await updater.DownloadAsync(_ => { });
        settings.UpdatesChanged();
        Assert.Equal(0, said);

        release.SetResult();
        await busy;
        Assert.Equal(1, said);
        Assert.Equal("Version 0.2.0 is ready. Restart to update.", settings.UpdateStatus);

        settings.UpdatesChanged();
        Assert.Equal(1, said);
    }

    [Fact]
    public async Task A_release_found_here_and_not_yet_downloaded_stays_available_when_the_updater_changes()
    {
        await Connected();
        await settings.CheckForUpdatesAsync();

        settings.UpdatesChanged();

        Assert.True(settings.UpdateAvailable);
        Assert.Equal("Version 0.2.0 is available.", settings.UpdateStatus);
    }

    [Fact]
    public async Task Settings_Restart_to_update_hands_the_warning_to_the_apps_path_and_Not_now_there_is_no_error()
    {
        await Connected();
        Func<string, Task<bool>>? given = null;
        settings.RestartDownloaded = confirm => { given = confirm; return Task.FromResult<string?>(AutoUpdater.NotNow); };
        await updater.DownloadAsync(_ => { });

        await settings.InstallUpdateAsync(_ => Task.FromResult(false));

        Assert.NotNull(given);
        Assert.Null(settings.Error);
        Assert.Equal("Version 0.2.0 is ready. Restart to update.", settings.UpdateStatus);
        Assert.False(updater.Applied);
    }

    // A session that starts a turn while the release downloads is warned of: asked once it has
    // downloaded, just before the host stops.
    [Fact]
    public async Task Settings_asks_what_a_restart_would_interrupt_after_the_download_not_before()
    {
        await Connected();
        await settings.CheckForUpdatesAsync();
        var downloadedWhenAsked = false;
        string? warned = null;
        await host.SendAsync(Events.SessionUpsert, Make.Session("s1", SessionStatus.Working));
        await Wait.UntilAsync(() => connection.Sessions.All.Count == 1, "the session");

        await settings.InstallUpdateAsync(warning => { (warned, downloadedWhenAsked) = (warning, updater.Ready == "0.2.0"); return Task.FromResult(false); });

        Assert.StartsWith("1 session here has a turn running.", warned);
        Assert.True(downloadedWhenAsked);
        Assert.Null(settings.Error);
        Assert.False(updater.Applied);
        Assert.Empty(host.Sent(HostOps.Stop));
    }

    [Fact]
    public async Task An_update_that_cannot_be_applied_starts_the_host_again_and_says_why()
    {
        await Connected();
        settings.StartAtSignIn = true;
        await Wait.UntilAsync(() => tasks.Registered.Count == 1, "the task");
        tasks.OnRun = () => host.Start();
        updater.ApplyFails = new InvalidOperationException("no downloaded update to install; check for updates again");

        await settings.CheckForUpdatesAsync();
        await settings.InstallUpdateAsync();

        Assert.Equal("The update didn't install because no downloaded update to install; check for updates again; choose Restart to update to try again.", settings.Error);
        Assert.Single(host.Sent(HostOps.Stop));
        Assert.Single(tasks.Ran);
        // Still downloaded, so still offered, for the person to try again (as the banner does).
        Assert.True(settings.UpdateAvailable);
        Assert.Null(prefs.UpdatedFrom);
    }

    [Fact]
    public async Task An_update_from_Settings_that_fails_is_reported_to_the_devices_and_leaves_their_press_to_the_app()
    {
        await Connected();
        prefs = prefs with { UpdateInstallAsked = "a-device-press" };
        updater.ApplyFails = new InvalidOperationException("no downloaded update to install; check for updates again");
        // The host the failed update stopped is started again, as its launcher would. Without
        // it, the start waited out its 5 s of wall time; its timeout is now on a clock that never moves.
        processes.OnStart = () => host.Start();

        await settings.CheckForUpdatesAsync();
        await settings.InstallUpdateAsync();

        var last = prefs.LastUpdate!;
        Assert.Equal((UpdateOutcome.Failed, "0.1.0", "0.2.0", (string?)null), (last.Outcome, last.From, last.To, last.Request));
        Assert.StartsWith("no downloaded update to install", last.Reason);
        Assert.Equal(AppUpdateViewModel.DidNotInstall(last.Reason!, again: true), settings.Error);
        Assert.Null(prefs.UpdateRequest);
        Assert.Equal("a-device-press", prefs.UpdateInstallAsked);
    }

    [Fact]
    public async Task An_update_from_Settings_whose_download_fails_is_reported_to_the_devices()
    {
        await Connected();
        updater.DownloadFails = new HttpRequestException("The response ended prematurely.");

        await settings.CheckForUpdatesAsync();
        await settings.InstallUpdateAsync();

        var last = prefs.LastUpdate!;
        Assert.Equal((UpdateOutcome.Failed, "0.2.0", "downloading 0.2.0 failed: The response ended prematurely."), (last.Outcome, last.To, last.Reason));
        Assert.Equal("The update didn't download; choose Restart to update to try again.", settings.Error);
        Assert.Empty(host.Sent(HostOps.Stop));
    }

    // Creative on #719 (6643004091): an error names the next step in one sentence; with downloads
    // in the background, that is when it is tried again.
    [Fact]
    public async Task A_download_that_fails_says_when_it_is_tried_again_in_the_background()
    {
        await Connected();
        var observed = new ObservedUpdater(updater);
        var page = new SettingsViewModel(connection, manager, observed, atSignIn, () => prefs, p => prefs = p, "0.1.0", "https://relay.default");
        updater.DownloadFails = new HttpRequestException("The response ended prematurely.");

        await page.CheckForUpdatesAsync();
        await page.InstallUpdateAsync();

        Assert.Equal($"The update didn't download. {Product.Name} will try again at {observed.RetryAt!.Value.ToLocalTime():t}.", page.Error);
    }

    // When an update is ready (#689, 6674007815): Restart to update unless this machine was set
    // otherwise, kept per machine and profile, each choice saying what it does.
    [Fact]
    public async Task When_an_update_is_ready_starts_at_Restart_to_update_and_keeps_Install_between_turns_once_chosen()
    {
        await Connected();
        Assert.Equal(0, settings.WhenReadyIndex);
        Assert.Equal(SettingsViewModel.WhenReadyRestart, settings.WhenReadyNote);
        Assert.Null(prefs.WhenUpdateReady);

        settings.WhenReadyIndex = 1;

        Assert.Equal(UpdateWhenReady.InstallBetweenTurns, prefs.WhenUpdateReady);
        Assert.Equal(SettingsViewModel.WhenReadyBetweenTurns, settings.WhenReadyNote);
        Assert.DoesNotContain("at the machine", settings.WhenReadyNote, StringComparison.Ordinal);
    }
    // Copilot on eae43827 (r4488279975): the app ticks its updater when a preference changes, so
    // the shared updater must already know the source changed when it hears Include pre-releases did.
    [Fact]
    public async Task Include_pre_releases_changes_the_shared_source_before_anyone_hears_of_it()
    {
        await Connected();
        var observed = new ObservedUpdater(updater);
        var page = new SettingsViewModel(connection, manager, observed, atSignIn, () => prefs, p => prefs = p, "0.1.0", "https://relay.default");
        var before = observed.Source;
        string? heard = null;
        page.PreferencesChanged += () => heard = observed.Source;

        page.Prereleases = !page.Prereleases;

        Assert.NotNull(heard);
        Assert.NotEqual(before, heard);
        Assert.Equal(observed.Source, heard);
    }

    // Creative on #719 (6643004091): Settings' own download is said at most every quarter.
    [Fact]
    public async Task A_download_from_Settings_is_announced_at_most_every_quarter()
    {
        await Connected();
        var said = 0;
        settings.UpdateStatusAnnounced += () => said++;
        settings.RestartDownloaded = _ => Task.FromResult<string?>(AutoUpdater.NotNow);
        updater.Steps = [.. Enumerable.Range(0, 101)];

        await settings.CheckForUpdatesAsync();
        await settings.InstallUpdateAsync();

        Assert.Equal(4, said);
    }

    [Fact]
    public async Task A_download_from_Settings_that_fails_leaves_what_another_install_recorded_for_its_restart()
    {
        await Connected();
        // The automatic update recorded its install (AutoUpdater.Record) while Settings downloaded.
        prefs = prefs with { UpdatedFrom = "0.1.0", UpdateNotes = "notes", UpdateRequest = "a-device-press" };
        updater.DownloadFails = new HttpRequestException("The response ended prematurely.");

        await settings.CheckForUpdatesAsync();
        await settings.InstallUpdateAsync();

        Assert.Equal(("0.1.0", "notes", "a-device-press"), (prefs.UpdatedFrom, prefs.UpdateNotes, prefs.UpdateRequest));
    }

    [Fact]
    public async Task An_update_from_Settings_answers_the_install_a_device_pressed_and_the_app_had_not_yet_carried_out()
    {
        await Connected();
        prefs = prefs with { UpdateInstallAsked = "a-device-press" };

        await settings.CheckForUpdatesAsync();
        await settings.InstallUpdateAsync();

        Assert.True(updater.Applied);
        Assert.Equal("a-device-press", prefs.UpdateRequest);
    }

    [Fact]
    public async Task An_update_check_that_cannot_reach_the_network_says_so_on_the_page()
    {
        await Connected();
        updater.CheckFails = new HttpRequestException("No such host is known.");

        await settings.CheckForUpdatesAsync();

        Assert.Equal("No such host is known.", settings.Error);
        Assert.False(settings.Busy);
        Assert.False(settings.UpdateAvailable);

        updater.CheckFails = new TaskCanceledException();
        await settings.CheckForUpdatesAsync();
        Assert.Equal("it took too long; try again", settings.Error);
    }

    [Fact]
    public async Task A_host_of_another_version_than_the_app_is_pointed_out()
    {
        await Connected();
        Assert.False(settings.VersionSkew);

        await host.SendAsync(HostOps.StatusEvent, FakeHost.StatusOf() with { Version = "0.0.9" });

        await Wait.UntilAsync(() => settings.VersionSkew, "the skew");
    }
}
