using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
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
        public string? Ready { get; private set; }
        public string? InstalledVersion => "0.1.0";
        public Task<string?> CheckAsync() => CheckFails is { } e ? Task.FromException<string?>(e) : Task.FromResult<string?>("0.2.0");
        public Task DownloadAsync(Action<int> progress)
        {
            if (DownloadFails is { } e) { return Task.FromException(e); }
            progress(100);
            Ready = "0.2.0";
            return Task.CompletedTask;
        }
        public void ApplyAndRestart() { OnApply?.Invoke(); if (ApplyFails is { } e) { throw e; } Applied = true; }
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
        host.On(HostOps.Stop, data =>
        {
            _ = Task.Run(async () => { await Task.Delay(50); await host.StopAsync(); });
            return new { stopping = true };
        });
        host.Capabilities.Add(HostOps.AgentSetupCapability);
        host.Capabilities.Add(HostOps.AgentInstallCapability);
        host.Capabilities.Add(HostOps.AgentSignInCapability);
        host.Start();
        connection = host.Connect(ui);
        manager = new HostManager(new InstallLayout(app, host.Folder, "test"), tasks, processes,
            ct => LocalClient.ConnectAsync(host.Endpoint, TimeSpan.FromMilliseconds(300), ct), userId: @"PC\dana", userSid: "S-1-5-21-1-2-3-1001")
        {
            StartTimeout = TimeSpan.FromSeconds(5),
            StopTimeout = TimeSpan.FromSeconds(2),
            Poll = TimeSpan.FromMilliseconds(50),
            Elevated = () => false,
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

    static AgentReadinessReport Readiness(string version) => new(DateTimeOffset.UtcNow,
    [
        new("claude", "Claude", "fixture-home", "claude.exe", version,
            new(ReadinessState.Working, "Executable found."),
            new(ReadinessState.Working, $"Version {version}"),
            new(ReadinessState.NeedsAction, "Explicit hook setup is required."),
            new(ReadinessState.Unknown, "Sign-in has not been verified."),
            new(ReadinessState.Unknown, "Trust and a new session have not been verified.")),
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CLI_sign_in_sends_person_confirmation_but_does_not_mark_authentication_or_trust_verified(bool opened)
    {
        await Connected();
        host.Status = host.Status with { Agents = Readiness("1.2.3") };
        await host.SendAsync(HostOps.StatusEvent, host.Status);
        await Wait.UntilAsync(() => settings.Agents.Count == 1, "agent readiness");
        Assert.True(Assert.Single(settings.Agents).CanSignIn);
        host.On(HostOps.AgentsSignIn, d =>
        {
            var request = d.Deserialize<AgentSignInRequest>(ProtocolJson.Options)!;
            Assert.Equal("claude", request.Agent);
            Assert.True(request.Confirmed);
            return new AgentSignInResult(opened, "Fixture sign-in window outcome; account access remains unverified.");
        });
        await settings.SignInAgentAsync("claude");
        Assert.Contains("account access remains unverified", settings.AgentSignInStatus);
        Assert.Equal(!opened, settings.HasError);
        Assert.Contains("Sign-in has not been verified", Assert.Single(settings.Agents).Checks);
        Assert.Empty(host.Sent(HostOps.SignIn));
        Assert.Empty(host.Sent(HostOps.AgentsSetup));
        Assert.Empty(host.Sent(HostOps.Restart));
        Assert.False(settings.Busy);
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
        Assert.False(reopened.CanSignInAgents);
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
        host.On(HostOps.AgentsRefresh, _ =>
        {
            host.Status = host.Status with { Agents = Readiness("2.3.4") };
            return host.Status.Agents;
        });

        await settings.SetUpAgentAsync("claude");

        Assert.Single(host.Sent(HostOps.AgentsSetup));
        Assert.Single(host.Sent(HostOps.AgentsRefresh));
        Assert.Contains("2.3.4", settings.Agents.Single().Installation);
        Assert.Empty(host.Sent(HostOps.Restart));
        Assert.True(settings.RestartRequired);
        Assert.Contains("Start new agent sessions", settings.AgentSetupStatus);
        Assert.Contains("Trust and a new session have not been verified", settings.Agents.Single().Checks);
        Assert.False(settings.HasError);
    }

    [Fact]
    public async Task A_failed_post_setup_scan_preserves_the_mutation_outcome_and_shows_the_scan_error()
    {
        await Connected();
        host.On(HostOps.AgentsSetup, _ => new AgentSetupResult(true, "Registered. Restart explicitly.", true));
        host.On(HostOps.AgentsRefresh, _ => throw new InvalidOperationException("fixture post-setup scan failed"));

        await settings.SetUpAgentAsync("claude");

        Assert.Equal("Registered. Restart explicitly.", settings.AgentSetupStatus);
        Assert.Equal("fixture post-setup scan failed", settings.Error);
        Assert.Empty(host.Sent(HostOps.Restart));
    }

    [Fact]
    public async Task Settings_setup_failures_keep_partial_work_visible_and_do_not_restart()
    {
        await Connected();
        host.On(HostOps.AgentsSetup, _ => new AgentSetupResult(false, "Hook setup incomplete: fixture partial work", true));

        await settings.SetUpAgentAsync("codex");

        Assert.Equal("Hook setup incomplete: fixture partial work", settings.Error);
        Assert.Equal(settings.Error, settings.AgentSetupStatus);
        Assert.Empty(host.Sent(HostOps.Restart));
        Assert.False(settings.Busy);
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
            DevBox = new DevBoxStatus(true, 15, new DevBoxPass(DateTimeOffset.UtcNow, "failed", "No Dev Center token. Run 'az login' as this user.", Alarming: true)),
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

        // What a binding does when it refreshes: the same values pushed back.
        settings.StartAtSignIn = false;
        settings.AppStartsAtSignIn = false;
        settings.RemoteApproval = true;
        await Task.Delay(200);

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
    public async Task An_update_that_cannot_be_applied_starts_the_host_again_and_says_why()
    {
        await Connected();
        settings.StartAtSignIn = true;
        await Wait.UntilAsync(() => tasks.Registered.Count == 1, "the task");
        tasks.OnRun = () => host.Start();
        updater.ApplyFails = new InvalidOperationException("no downloaded update to install; check for updates again");

        await settings.CheckForUpdatesAsync();
        await settings.InstallUpdateAsync();

        Assert.Equal("no downloaded update to install; check for updates again", settings.Error);
        Assert.Single(host.Sent(HostOps.Stop));
        Assert.Single(tasks.Ran);
        Assert.False(settings.UpdateAvailable);
        Assert.Null(prefs.UpdatedFrom);
    }

    [Fact]
    public async Task An_update_from_Settings_that_fails_is_reported_to_the_devices_and_leaves_their_press_to_the_app()
    {
        await Connected();
        prefs = prefs with { UpdateInstallAsked = "a-device-press" };
        updater.ApplyFails = new InvalidOperationException("no downloaded update to install; check for updates again");

        await settings.CheckForUpdatesAsync();
        await settings.InstallUpdateAsync();

        var last = prefs.LastUpdate!;
        Assert.Equal((UpdateOutcome.Failed, "0.1.0", "0.2.0", (string?)null), (last.Outcome, last.From, last.To, last.Request));
        Assert.Equal(settings.Error, last.Reason);
        Assert.StartsWith("no downloaded update to install", last.Reason);
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
        Assert.Equal("The response ended prematurely.", settings.Error);
        Assert.Empty(host.Sent(HostOps.Stop));
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
