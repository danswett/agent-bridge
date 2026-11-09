using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// manage.* (#801 slice A, docs/protocol/v1-sessions.md, "Managing a machine"): a person's device restarts this host or
// repairs its hooks through the in-process relay over a Noise channel, each press a command the
// device signed for this machine. The restart leaves only through #805's quiet lease.
[Trait("Category", "Network")]
public sealed class ManageFlowTests : IAsyncLifetime
{
    const string Id = "88888888-8888-4888-8888-888888888888";
    // Real time is only ever a failure bound, never the expected path (as in LifecycleCoordinatorTests).
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    readonly Fixture f = new();
    readonly WatchedTime time = new(Now);
    readonly List<string> log = [];
    readonly List<(string Except, string Title, string Body)> notices = [];
    readonly FakeHooks hooks = new();
    DeliveryGate gate = new();
    string? readOnly;
    bool signInOn;
    bool enabled = true;
    // Whether a stop comes while a committed restart settles, so the host never hands over.
    bool stopWins;
    // Runs as the eligibility is refreshed, just before the commit: what may happen in between.
    Action? refreshing;
    // What the Q-O3 notice's delivery has come to: sent at once unless a test holds it.
    Task noticeSent = Task.CompletedTask;
    int restarts;
    readonly TaskCompletionSource[] restarted = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
    TestRelay relay = null!;
    SessionSource source = null!;
    SessionService service = null!;
    ManageService manage = null!;
    Device host = null!;
    string token = "";
    string folder = "";

    public async Task InitializeAsync()
    {
        var events = f.CopilotSession(Id, 100);
        Fixture.Append(events, Copilot.User("go", 1));
        f.Processes.Run(AgentKind.Copilot, 100);
        source = new SessionSource(f.Options(), f.Processes, TimeProvider.System);
        await source.RefreshAsync();
        folder = Path.Combine(f.Root, "profile");
        Directory.CreateDirectory(folder);

        relay = await TestRelay.StartAsync();
        token = relay.TokenFor(1, "octo");
        host = await relay.ConnectAsync(token, "Test host", "host");
        manage = Make();
        service = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0")) { ReadOnly = () => readOnly, Manage = manage };
        host.Hub.JsonOptions = ProtocolJson.Options;
        host.Hub.Handler = service.HandleAsync;
    }

    // A host as Program makes one; another over the same folder is the host after a restart. Given
    // settings, it is Program's own composition (ManageComposition): the switch and the eligibility
    // from them, and #800's sign-in bound by the SessionService it is given to. Without, a test
    // turns them.
    ManageService Make(HostSettings? settings = null)
    {
        gate = new DeliveryGate(time);
        ManageService? made = null;
        // As Program's SettledRestart does: hands over unless a stop came first.
        void Restart()
        {
            if (!stopWins) { made!.HandingOver(); }
            var n = Interlocked.Increment(ref restarts);
            if (n <= restarted.Length) { restarted[n - 1].TrySetResult(); }
        }
        if (settings is not null)
        {
            var composed = made = ManageComposition.For(settings, host.Key, folder, gate, () => readOnly, null, Restart,
                new Notices(notices, () => noticeSent), "Test host", m => { lock (log) { log.Add(m); } }, time).Manage;
            composed.Hooks = hooks;
            composed.Start();
            composed.Attach(host.Relay);
            return composed;
        }
        var conditions = new DepartureConditions();
        var eligibility = new RemoteRestartEligibility(() => readOnly, () => { refreshing?.Invoke(); return enabled; }, new RefuseWhileSignInEnabled(() => signInOn), time);
        conditions.Register(DeparturePurpose.RemoteRestart, eligibility);
        made = new ManageService(host.Key, folder, m => { lock (log) { log.Add(m); } }, time)
        {
            Settings = new HostSettings(true),
            ReadOnly = () => readOnly,
            Lifecycle = new LifecycleCoordinator(gate, conditions, time: time),
            Eligibility = eligibility,
            RestartHost = Restart,
            Hooks = hooks,
            Notices = new Notices(notices, () => noticeSent),
            MachineName = "Test host",
            Enabled = () => enabled,
        };
        made.Start();
        made.Attach(host.Relay);
        return made;
    }

    // The host after a restart, started only once the old one has left (HostHandover): until then the old
    // host may still be writing manage-pending.json, as a handover marks it (CI once read it unmarked).
    async Task RestartedAsHostAsync(int restartsSoFar)
    {
        await RestartedAsync(restartsSoFar);
        manage = Make();
        service = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0")) { ReadOnly = () => readOnly, Manage = manage };
        host.Hub.Handler = service.HandleAsync;
    }

    public async Task DisposeAsync()
    {
        await relay.DisposeAsync();
        await source.DisposeAsync();
        f.Dispose();
    }

    async Task<(Device Device, SecureChannel Channel)> OpenAsync(string name = "Phone", string kind = "pwa")
    {
        var device = await relay.ConnectAsync(token, name, kind);
        await device.Relay.WaitForPeerAsync(p => p.Id == host.Id, Bound);
        await host.Relay.WaitForPeerAsync(p => p.Id == device.Relay.DeviceId, Bound);
        return (device, await device.Hub.OpenAsync(host.Id));
    }

    SignedCommand Sign(P256KeyPair signer, string op, object? args = null, string? request = null, TimeSpan? lifetime = null, DateTimeOffset? issued = null)
    {
        var at = issued ?? time.GetUtcNow();
        var body = new CommandBody
        {
            V = 1,
            Action = op,
            Request = request ?? Guid.NewGuid().ToString(),
            Principal = new CommandPrincipal { Kind = "user", Device = Wire.DeviceId(signer.Public) },
            Target = new CommandTarget { Machine = host.Id },
            To = host.Id,
            IssuedAt = at.ToUnixTimeSeconds(),
            ExpiresAt = (at + (lifetime ?? TimeSpan.FromSeconds(60))).ToUnixTimeSeconds(),
            Args = args is null ? null : JsonSerializer.SerializeToElement(args, ProtocolJson.Options),
        };
        return SignedCommands.Sign(signer, host.Key.Public, body);
    }

    static async Task<ManageResult> SendAsync(SecureChannel ch, string op, SignedCommand command)
    {
        var response = await ch.RequestAsync(op, new ManageCommand { Command = command });
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        return response.GetProperty("result").Deserialize<ManageResult>(ProtocolJson.Options)!;
    }

    Task RestartedAsync(int count) => Task.WhenAll(restarted.Take(count).Select(r => r.Task)).WaitAsync(Bound);

    static async Task<IReadOnlyList<string>> CapabilitiesAsync(SecureChannel ch)
    {
        var hello = await ch.RequestAsync(Ops.Hello, new { });
        return [.. hello.GetProperty("result").GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!)];
    }

    [Fact]
    public async Task A_person_restarts_the_host_through_the_relay_once_the_quiet_lease_is_held()
    {
        var (phone, ch) = await OpenAsync();
        Assert.Contains(ManageProtocol.Capability, await CapabilitiesAsync(ch));

        var result = await SendAsync(ch, ManageProtocol.Restart, Sign(phone.Key, ManageProtocol.Restart));

        Assert.Equal(ManageOutcome.Accepted, result.Outcome);
        Assert.NotNull(result.Transition);
        Assert.True(File.Exists(Path.Combine(folder, ManageService.PendingFile)));
        // The fence holds until the host has gone: nothing new is delivered meanwhile.
        Assert.Null(gate.TryEnterDelivery());
        // The reply leaves without the clock moving; the fallback then fires inside Advance and finds
        // the host already leaving, so it leaves once either way.
        await RestartedAsync(1);
        time.Advance(ManageService.LeaveAnyway);
        Assert.Equal(1, Volatile.Read(ref restarts));
    }

    [Fact]
    public async Task A_restart_without_a_quiet_lease_is_refused_and_uses_up_nothing()
    {
        var (phone, ch) = await OpenAsync();
        using var delivering = gate.TryEnterDelivery();
        var polling = time.NextTimer();

        var sending = SendAsync(ch, ManageProtocol.Restart, Sign(phone.Key, ManageProtocol.Restart));
        // The lease waits out its default patience on the fake clock, once it is actually waiting.
        await polling.WaitAsync(Bound);
        time.Advance(LifecycleCoordinator.DefaultWait);
        var result = await sending.WaitAsync(Bound);

        Assert.Equal((ManageOutcome.Refused, ManageReason.NotQuiet), (result.Outcome, result.Reason));
        Assert.Equal(0, Volatile.Read(ref restarts));
        Assert.False(File.Exists(Path.Combine(folder, ManageService.PendingFile)));
        Assert.Equal(ManageService.RestartsBeforeRoster, result.State!.Restarts.Left);
    }

    [Fact]
    public async Task A_restart_whose_quiet_lease_runs_out_before_the_commit_is_refused_as_changed_and_uses_up_nothing()
    {
        var (phone, ch) = await OpenAsync();
        refreshing = () => time.Advance(LifecycleCoordinator.LeaseLifetime);

        var result = await SendAsync(ch, ManageProtocol.Restart, Sign(phone.Key, ManageProtocol.Restart));

        Assert.Equal((ManageOutcome.Refused, ManageReason.Changed), (result.Outcome, result.Reason));
        Assert.Equal(0, Volatile.Read(ref restarts));
        Assert.Equal(ManageService.RestartsBeforeRoster, result.State!.Restarts.Left);
    }

    [Fact]
    public async Task A_restart_is_refused_while_an_agent_sign_in_could_be_cut_off()
    {
        var (phone, ch) = await OpenAsync();
        signInOn = true;

        var result = await SendAsync(ch, ManageProtocol.Restart, Sign(phone.Key, ManageProtocol.Restart));

        Assert.Equal((ManageOutcome.Refused, ManageReason.Changed), (result.Outcome, result.Reason));
        Assert.Equal(0, Volatile.Read(ref restarts));
        // The refused commit lets the fence go: deliveries go on.
        using var next = gate.TryEnterDelivery();
        Assert.NotNull(next);
    }

    [Fact]
    public async Task A_machine_that_the_relay_lists_as_a_host_cannot_restart_another()
    {
        var (other, ch) = await OpenAsync("Other machine", "host");
        Assert.DoesNotContain(ManageProtocol.Capability, await CapabilitiesAsync(ch));

        var result = await SendAsync(ch, ManageProtocol.Restart, Sign(other.Key, ManageProtocol.Restart));

        Assert.Equal((ManageOutcome.Refused, ManageReason.NotPerson), (result.Outcome, result.Reason));
        Assert.Null(result.State);
        Assert.Equal(0, Volatile.Read(ref restarts));
    }

    [Fact]
    public async Task A_command_signed_by_a_device_other_than_the_one_sending_it_is_refused()
    {
        var (_, ch) = await OpenAsync();
        var (laptop, _) = await OpenAsync("Laptop");

        var result = await SendAsync(ch, ManageProtocol.Restart, Sign(laptop.Key, ManageProtocol.Restart));

        Assert.Equal((ManageOutcome.Refused, ManageReason.NotPeer), (result.Outcome, result.Reason));
        Assert.Equal(0, Volatile.Read(ref restarts));
    }

    [Fact]
    public async Task A_command_that_lasts_more_than_two_minutes_is_refused()
    {
        var (phone, ch) = await OpenAsync();

        var result = await SendAsync(ch, ManageProtocol.Restart, Sign(phone.Key, ManageProtocol.Restart, lifetime: TimeSpan.FromMinutes(5)));

        Assert.Equal((ManageOutcome.Refused, ManageReason.Expired), (result.Outcome, result.Reason));
        Assert.Equal(0, Volatile.Read(ref restarts));
    }

    [Fact]
    public async Task A_command_signed_for_another_machine_is_refused()
    {
        var (phone, ch) = await OpenAsync();
        using var elsewhere = P256KeyPair.Generate();
        var body = new CommandBody
        {
            V = 1,
            Action = ManageProtocol.Restart,
            Request = Guid.NewGuid().ToString(),
            Principal = new CommandPrincipal { Kind = "user", Device = phone.Id },
            Target = new CommandTarget { Machine = Wire.DeviceId(elsewhere.Public) },
            To = Wire.DeviceId(elsewhere.Public),
            IssuedAt = Now.ToUnixTimeSeconds(),
            ExpiresAt = Now.AddSeconds(60).ToUnixTimeSeconds(),
        };

        var result = await SendAsync(ch, ManageProtocol.Restart, SignedCommands.Sign(phone.Key, elsewhere.Public, body));

        Assert.Equal((ManageOutcome.Refused, ManageReason.BadSignature), (result.Outcome, result.Reason));
        Assert.Equal(0, Volatile.Read(ref restarts));
    }

    [Fact]
    public async Task The_app_beside_this_host_cannot_restart_it_through_the_relay()
    {
        var (app, ch) = await OpenAsync("This machine's app", "app");
        manage.NoteLocalApp(app.Id);
        Assert.DoesNotContain(ManageProtocol.Capability, await CapabilitiesAsync(ch));

        var result = await SendAsync(ch, ManageProtocol.Restart, Sign(app.Key, ManageProtocol.Restart));

        Assert.Equal((ManageOutcome.Refused, ManageReason.LocalDevice), (result.Outcome, result.Reason));
        Assert.Equal(0, Volatile.Read(ref restarts));
        // Nor read what manage.get keeps: it reads that from its own Settings.
        var read = await ch.RequestAsync(ManageProtocol.Get, new { });
        Assert.False(read.GetProperty("ok").GetBoolean());
        Assert.Equal(ManageService.ThroughTheRelay, read.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_restart_whose_signed_body_carries_arguments_is_refused_as_bad_args()
    {
        var (phone, ch) = await OpenAsync();

        var withArgs = await SendAsync(ch, ManageProtocol.Restart, Sign(phone.Key, ManageProtocol.Restart, new { now = true }));
        var empty = await SendAsync(ch, ManageProtocol.Restart, Sign(phone.Key, ManageProtocol.Restart, new { }));

        Assert.Equal((ManageOutcome.Refused, ManageReason.BadArgs), (withArgs.Outcome, withArgs.Reason));
        Assert.Equal((ManageOutcome.Refused, ManageReason.BadArgs), (empty.Outcome, empty.Reason));
        Assert.Equal(0, Volatile.Read(ref restarts));
    }

    [Fact]
    public async Task A_repair_whose_arguments_name_anything_but_the_agent_is_refused_as_bad_args()
    {
        var (phone, ch) = await OpenAsync();
        hooks.Owned.Add("copilot");

        var extra = await SendAsync(ch, ManageProtocol.HooksRepair, Sign(phone.Key, ManageProtocol.HooksRepair, new { agent = "copilot", remove = true }));
        var notAnObject = await SendAsync(ch, ManageProtocol.HooksRepair, Sign(phone.Key, ManageProtocol.HooksRepair, "copilot"));

        Assert.Equal((ManageOutcome.Refused, ManageReason.BadArgs), (extra.Outcome, extra.Reason));
        Assert.Equal((ManageOutcome.Refused, ManageReason.BadArgs), (notAnObject.Outcome, notAnObject.Reason));
        Assert.Empty(hooks.Repaired);
    }

    [Fact]
    public async Task With_the_switch_off_nothing_is_offered_read_restarted_or_repaired()
    {
        var (phone, ch) = await OpenAsync();
        hooks.Owned.Add("copilot");
        enabled = false;
        Assert.DoesNotContain(ManageProtocol.Capability, await CapabilitiesAsync(ch));

        foreach (var (op, args) in new (string, object?)[] { (ManageProtocol.Get, null), (ManageProtocol.Restart, null), (ManageProtocol.HooksRepair, new ManageRepairArgs("copilot")) })
        {
            var response = await ch.RequestAsync(op, new ManageCommand { Command = Sign(phone.Key, op, args) });
            Assert.False(response.GetProperty("ok").GetBoolean(), op);
            Assert.Contains("permissions.remoteManage", response.GetProperty("error").GetString());
        }
        Assert.Equal(0, Volatile.Read(ref restarts));
        Assert.Empty(hooks.Repaired);
    }

    [Fact]
    public async Task A_repair_is_refused_when_the_switch_goes_off_after_its_press_was_checked()
    {
        var (phone, ch) = await OpenAsync();
        hooks.Owned.Add("copilot");
        hooks.Checking = () => enabled = false;

        var result = await SendAsync(ch, ManageProtocol.HooksRepair, Sign(phone.Key, ManageProtocol.HooksRepair, new ManageRepairArgs("copilot")));

        Assert.Equal((ManageOutcome.Refused, ManageReason.Disabled), (result.Outcome, result.Reason));
        Assert.Empty(hooks.Repaired);
    }

    [Fact]
    public async Task A_host_whose_settings_turn_remote_management_off_offers_and_takes_none_of_it()
    {
        manage = Make(new HostSettings(true) { RemoteManage = false });
        host.Hub.Handler = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0")) { Manage = manage }.HandleAsync;
        var (phone, ch) = await OpenAsync();
        Assert.DoesNotContain(ManageProtocol.Capability, await CapabilitiesAsync(ch));

        var response = await ch.RequestAsync(ManageProtocol.Restart, new ManageCommand { Command = Sign(phone.Key, ManageProtocol.Restart) });

        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Contains("permissions.remoteManage", response.GetProperty("error").GetString());
        Assert.Equal(0, Volatile.Read(ref restarts));
    }

    [Fact]
    public async Task The_host_as_Program_composes_it_with_remote_management_off_refuses_both_a_restart_and_a_repair()
    {
        manage = Make(new HostSettings(true) { RemoteManage = false });
        host.Hub.Handler = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0")) { Manage = manage }.HandleAsync;
        hooks.Owned.Add("copilot");
        var (phone, ch) = await OpenAsync();

        foreach (var (op, args) in new (string, object?)[] { (ManageProtocol.Restart, null), (ManageProtocol.HooksRepair, new ManageRepairArgs("copilot")) })
        {
            var response = await ch.RequestAsync(op, new ManageCommand { Command = Sign(phone.Key, op, args) });
            Assert.False(response.GetProperty("ok").GetBoolean(), op);
            Assert.Contains("permissions.remoteManage", response.GetProperty("error").GetString());
        }
        Assert.Equal(0, Volatile.Read(ref restarts));
        Assert.Empty(hooks.Repaired);
        Assert.False(File.Exists(Path.Combine(folder, ManageService.PendingFile)));
    }

    [Fact]
    public async Task A_host_wired_from_its_settings_refuses_a_restart_while_its_agent_sign_in_entrypoint_is_on()
    {
        manage = Make(new HostSettings(true));
        // No roster pins anyone (#972 slice 2): it is the entrypoint being on that holds a restart.
        var nobody = new AgentBridge.Host.SignIn.RosterSignInAuthority(new AgentBridge.Host.SignIn.NoPersonRoster(), () => false, _ => true);
        await using var signIn = new AgentBridge.Host.SignIn.AgentSignInService(source, nobody, null!, null!, null!, () => false, time);
        host.Hub.Handler = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0"))
        {
            Manage = manage,
            SignIn = signIn,
            SignInEntrypointApproved = true,
        }.HandleAsync;
        var (phone, ch) = await OpenAsync();

        var refused = await SendAsync(ch, ManageProtocol.Restart, Sign(phone.Key, ManageProtocol.Restart));

        Assert.Equal((ManageOutcome.Refused, ManageReason.Changed), (refused.Outcome, refused.Reason));
        Assert.Equal(0, Volatile.Read(ref restarts));

        // The same host with the entrypoint off restarts.
        host.Hub.Handler = new SessionService(source, new MachineInfo("host-id", "Test host", "windows", "0.1.0")) { Manage = manage, SignIn = signIn, SignInEntrypointApproved = false }.HandleAsync;
        var accepted = await SendAsync(ch, ManageProtocol.Restart, Sign(phone.Key, ManageProtocol.Restart));
        Assert.Equal(ManageOutcome.Accepted, accepted.Outcome);
        await RestartedAsync(1);
    }

    [Fact]
    public async Task Management_on_this_machines_pipe_is_refused_whatever_it_carries()
    {
        var (phone, _) = await OpenAsync();
        var command = JsonSerializer.SerializeToElement(new ManageCommand { Command = Sign(phone.Key, ManageProtocol.Restart) }, ProtocolJson.Options);

        foreach (var op in new[] { ManageProtocol.Get, ManageProtocol.Restart, ManageProtocol.HooksRepair })
        {
            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => manage.HandleAsync(new PipeChannel(), op, command));
            Assert.Equal(ManageService.ThroughTheRelay, e.Message);
        }
        Assert.Equal(0, Volatile.Read(ref restarts));
    }

    [Fact]
    public async Task A_restart_leaves_only_once_the_other_devices_notice_is_sent_or_its_wait_is_up()
    {
        var (phone, ch) = await OpenAsync();
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        noticeSent = sending.Task;

        var result = await SendAsync(ch, ManageProtocol.Restart, Sign(phone.Key, ManageProtocol.Restart));
        Assert.Equal(ManageOutcome.Accepted, result.Outcome);
        // The reply has gone and the host is fenced, but its notice has not: it stays.
        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref restarts));

        sending.SetResult();
        await RestartedAsync(1);
        Assert.Equal(1, Volatile.Read(ref restarts));
    }

    [Fact]
    public async Task A_restart_whose_notice_never_goes_leaves_once_the_notice_wait_is_up()
    {
        var (phone, ch) = await OpenAsync();
        noticeSent = new TaskCompletionSource().Task;

        Assert.Equal(ManageOutcome.Accepted, (await SendAsync(ch, ManageProtocol.Restart, Sign(phone.Key, ManageProtocol.Restart))).Outcome);
        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref restarts));
        time.Advance(ManageService.NoticeWait);

        await RestartedAsync(1);
        Assert.Contains(log, l => l.Contains("notice to other devices was not confirmed sent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_read_only_machine_offers_no_management_and_refuses_a_restart()
    {
        var (phone, ch) = await OpenAsync();
        readOnly = "a read-only comparison host";
        Assert.DoesNotContain(ManageProtocol.Capability, await CapabilitiesAsync(ch));

        var response = await ch.RequestAsync(ManageProtocol.Restart, new ManageCommand { Command = Sign(phone.Key, ManageProtocol.Restart) });

        Assert.False(response.GetProperty("ok").GetBoolean());
        Assert.Contains("read-only", response.GetProperty("error").GetString());
        Assert.Equal(0, Volatile.Read(ref restarts));
    }

    [Fact]
    public async Task A_committed_restart_that_a_stop_won_over_is_reported_failed_by_the_next_host_not_done()
    {
        var (phone, ch) = await OpenAsync();
        stopWins = true;
        var command = Sign(phone.Key, ManageProtocol.Restart);
        Assert.Equal(ManageOutcome.Accepted, (await SendAsync(ch, ManageProtocol.Restart, command)).Outcome);
        await RestartedAsync(1);
        time.Advance(TimeSpan.FromMinutes(5));
        stopWins = false;
        // Started again by hand, well inside the window a restart is reported done in.
        await RestartedAsHostAsync(1);

        var again = await SendAsync(ch, ManageProtocol.Restart, command);

        Assert.Equal((ManageOutcome.Failed, ManageReason.Changed), (again.Outcome, again.Reason));
        Assert.Null(again.State!.Last!.TookSeconds);
    }
    [Fact]
    public async Task A_third_committed_restart_in_six_hours_is_refused_before_the_roster()
    {
        var (phone, ch) = await OpenAsync();
        Assert.Equal(ManageOutcome.Accepted, (await SendAsync(ch, ManageProtocol.Restart, Sign(phone.Key, ManageProtocol.Restart))).Outcome);
        await RestartedAsHostAsync(1);
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(ManageOutcome.Accepted, (await SendAsync(ch, ManageProtocol.Restart, Sign(phone.Key, ManageProtocol.Restart))).Outcome);
        await RestartedAsHostAsync(2);
        time.Advance(TimeSpan.FromMinutes(5));

        var third = await SendAsync(ch, ManageProtocol.Restart, Sign(phone.Key, ManageProtocol.Restart));

        Assert.Equal((ManageOutcome.Refused, ManageReason.RateLimited), (third.Outcome, third.Reason));
        await RestartedAsync(2);
        Assert.Equal(2, Volatile.Read(ref restarts));
        Assert.Equal(0, third.State!.Restarts.Left);
    }

    [Fact]
    public async Task A_restart_sent_again_is_the_same_press_and_the_new_host_reports_how_it_ended()
    {
        var (phone, ch) = await OpenAsync();
        var command = Sign(phone.Key, ManageProtocol.Restart);
        var first = await SendAsync(ch, ManageProtocol.Restart, command);
        time.Advance(TimeSpan.FromSeconds(40));
        await RestartedAsHostAsync(1);

        var again = await SendAsync(ch, ManageProtocol.Restart, command);

        await RestartedAsync(1);
        Assert.Equal(1, Volatile.Read(ref restarts));
        Assert.Equal(first.Request, again.Request);
        Assert.Equal(ManageOutcome.Done, again.Outcome);
        Assert.Equal(40, again.State!.Last!.TookSeconds);
        Assert.False(File.Exists(Path.Combine(folder, ManageService.PendingFile)));
    }

    [Fact]
    public async Task A_repair_runs_only_for_an_agent_this_installation_set_up()
    {
        var (phone, ch) = await OpenAsync();
        Assert.Contains(ManageProtocol.HooksCapability, await CapabilitiesAsync(ch));
        hooks.Owned.Add("copilot");

        var refused = await SendAsync(ch, ManageProtocol.HooksRepair, Sign(phone.Key, ManageProtocol.HooksRepair, new ManageRepairArgs("claude")));
        var repaired = await SendAsync(ch, ManageProtocol.HooksRepair, Sign(phone.Key, ManageProtocol.HooksRepair, new ManageRepairArgs("copilot")));

        Assert.Equal((ManageOutcome.Refused, ManageReason.Unavailable), (refused.Outcome, refused.Reason));
        Assert.Equal(ManageOutcome.Done, repaired.Outcome);
        Assert.Equal(["copilot"], hooks.Repaired);
    }

    [Fact]
    public async Task Each_outcome_writes_an_audit_line_with_who_what_and_when_in_Pacific_time()
    {
        var (phone, ch) = await OpenAsync();
        await SendAsync(ch, ManageProtocol.Restart, Sign(phone.Key, ManageProtocol.Restart));

        var line = Assert.Single(log, l => l.StartsWith("manage restart by pwa", StringComparison.Ordinal));
        Assert.Contains($"pwa {phone.Id[..8]} \"Phone\"", line);
        Assert.Contains(" PT: accepted", line);
        Assert.Contains("2026-11-05 04:00:00 PT", line);
    }

    [Fact]
    public async Task The_owners_other_devices_get_a_quiet_notice_that_names_who_but_carries_no_values()
    {
        var (phone, ch) = await OpenAsync();
        hooks.Owned.Add("codex");

        await SendAsync(ch, ManageProtocol.HooksRepair, Sign(phone.Key, ManageProtocol.HooksRepair, new ManageRepairArgs("codex")));
        await SendAsync(ch, ManageProtocol.Restart, Sign(phone.Key, ManageProtocol.Restart));

        Assert.Equal(2, notices.Count);
        Assert.All(notices, n => Assert.Equal(phone.Id, n.Except));
        Assert.Contains("had its Codex hooks repaired from Phone", notices[0].Body);
        Assert.Contains("was restarted from Phone", notices[1].Body);
        Assert.All(notices, n => Assert.DoesNotContain("fixture detail", n.Body));
    }

    [Theory]
    [InlineData(ManageProtocol.Stop)]
    [InlineData(ManageProtocol.ConfigGet)]
    [InlineData(ManageProtocol.ConfigSet)]
    [InlineData(ManageProtocol.HaTokenSet)]
    public async Task Stop_config_and_the_token_are_refused_until_the_signed_roster(string op)
    {
        var (phone, ch) = await OpenAsync();
        var capabilities = await CapabilitiesAsync(ch);
        Assert.DoesNotContain(ManageProtocol.StopCapability, capabilities);
        Assert.DoesNotContain(ManageProtocol.ConfigCapability, capabilities);
        Assert.DoesNotContain(ManageProtocol.HaTokenCapability, capabilities);

        var response = await ch.RequestAsync(op, new { command = Sign(phone.Key, op), secret = "not-a-real-token-value" });
        var result = response.GetProperty("result").Deserialize<ManageResult>(ProtocolJson.Options)!;

        Assert.Equal((ManageOutcome.Refused, ManageReason.NeedsRoster), (result.Outcome, result.Reason));
        Assert.Null(result.Request);
        Assert.Null(result.State);
        Assert.DoesNotContain(log, l => l.Contains("not-a-real-token-value"));
    }

    [Fact]
    public void Restart_eligibility_reads_only_its_snapshot_and_blocks_until_a_fresh_one_says_eligible()
    {
        var reads = 0;
        var eligibility = new RemoteRestartEligibility(() => { reads++; return null; }, () => true, new RefuseWhileSignInEnabled(() => false), time);
        Assert.True(eligibility.IsBlocking());

        eligibility.Refresh();
        Assert.False(eligibility.IsBlocking());
        Assert.Equal(1, reads);

        time.Advance(RemoteRestartEligibility.MaxAge + TimeSpan.FromSeconds(1));
        Assert.True(eligibility.IsBlocking());
        Assert.Equal(1, reads);
    }

    // A fake clock that says when something next starts waiting on it, so a test advances it only
    // once the wait exists rather than racing the relay to it.
    sealed class WatchedTime(DateTimeOffset start) : FakeTimeProvider(start)
    {
        TaskCompletionSource? next;

        public Task NextTimer()
        {
            var armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref next, armed);
            return armed.Task;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Exchange(ref next, null)?.TrySetResult();
            return timer;
        }
    }

    // A channel on this machine's pipe, as the app or an agent would open one.
    sealed class PipeChannel : IProtocolChannel
    {
        public string PeerId => "local-1234";
        public string PeerName => "agent";
        public string PeerKind => "local";
        public bool IsOpen => true;
        public Task Closed => Task.Delay(Timeout.Infinite);
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }

    sealed class Notices(List<(string, string, string)> into, Func<Task> sent) : IManageNotices
    {
        public Task Notify(string exceptDevice, string machineName, string body)
        {
            lock (into) { into.Add((exceptDevice, machineName, body)); }
            return sent();
        }
    }

    sealed class FakeHooks : IHookRepair
    {
        public readonly HashSet<string> Owned = [];
        public readonly List<string> Repaired = [];
        // Runs as the press is checked, before the repair: what may change in between.
        public Action? Checking;
        public bool Available => true;
        public string? NotOwned(string agent)
        {
            Checking?.Invoke();
            return Owned.Contains(agent) ? null : "this agent was never set up by this machine's app";
        }
        public Task<AgentSetupResult> RepairAsync(string agent)
        {
            lock (Repaired) { Repaired.Add(agent); }
            return Task.FromResult(new AgentSetupResult(true, "fixture detail", false));
        }
    }
}

public sealed class HookOwnershipTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "abh-own-" + Guid.NewGuid().ToString("N")[..8]);

    public HookOwnershipTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Only_an_agent_whose_ownership_record_names_this_profile_counts_as_set_up_here()
    {
        var profile = Path.Combine(root, "profile");
        var mine = Path.Combine(root, "mine");
        var theirs = Path.Combine(root, "theirs");
        var never = Path.Combine(root, "never");
        Directory.CreateDirectory(mine);
        Directory.CreateDirectory(theirs);
        File.WriteAllText(Path.Combine(mine, HookOwnership.FileName), JsonSerializer.Serialize(new { version = 1, profile }));
        File.WriteAllText(Path.Combine(theirs, HookOwnership.FileName), JsonSerializer.Serialize(new { version = 1, profile = Path.Combine(root, "other") }));

        Assert.Null(HookOwnership.NotOwned(mine, profile));
        Assert.NotNull(HookOwnership.NotOwned(theirs, profile));
        Assert.NotNull(HookOwnership.NotOwned(never, profile));
    }
}
