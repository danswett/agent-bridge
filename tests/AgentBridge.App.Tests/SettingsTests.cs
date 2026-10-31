using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

// The Settings page over a host on a real pipe: signing in, the host's settings, the switches
// that install things, restarting and updating. Task Scheduler, processes, the Run entry and
// Velopack are fakes.
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

    sealed class Updater : IUpdater
    {
        public Action? OnApply;
        public Exception? CheckFails;
        public bool Applied;
        public string? InstalledVersion => "0.1.0";
        public Task<string?> CheckAsync() => CheckFails is { } e ? Task.FromException<string?>(e) : Task.FromResult<string?>("0.2.0");
        public Task DownloadAsync(Action<int> progress) { progress(100); return Task.CompletedTask; }
        public void ApplyAndRestart() { OnApply?.Invoke(); Applied = true; }
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
        host.Start();
        connection = host.Connect(ui);
        var manager = new HostManager(new InstallLayout(app, host.Folder, "test"), tasks, processes,
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
    public async Task Answering_folder_trust_from_devices_is_turned_on_here_with_this_app_approved_and_each_device_is_approved_here()
    {
        const string phone = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2", gone = "c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3";
        var link = new FleetTests.FakeFleetLink();
        link.PeopleNow.Add(new FleetHost(phone, "Dana's phone"));
        link.PeopleNow.Add(new FleetHost(link.DeviceId!, "This app, as the relay lists it"));
        var main = new MainViewModel(connection, new CardContext { Deliveries = new Deliveries(connection, ui), Folds = new FoldStore(null), Capabilities = () => connection.Capabilities, Host = connection }, new UsageViewModel(), settings);
        await using var fleet = new Fleet(link, main, ui, h => throw new InvalidOperationException("no machines"));
        // The host answers with what it saved.
        host.On(HostOps.Settings, d => new SettingsResult(new HostSettingsView(true, RemoteFolderTrust: true,
            RemoteFolderTrustDevices: d.TryGetProperty("remoteFolderTrustDevices", out var l) ? [.. l.EnumerateArray().Select(e => e.GetString()!)] : null), false));
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
        // The host answers with what it saved; its status event comes only later.
        host.On(HostOps.Settings, d => new SettingsResult(new HostSettingsView(true, RemoteFolderTrust: true,
            RemoteFolderTrustDevices: [.. d.GetProperty("remoteFolderTrustDevices").EnumerateArray().Select(e => e.GetString()!)]), false));
        host.Status = FakeHost.StatusOf() with { Settings = new HostSettingsView(true, RemoteFolderTrust: true, RemoteFolderTrustDevices: [a]) };
        await Connected();
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
    public async Task A_device_ticked_and_unticked_quickly_ends_unapproved_and_so_does_the_switch()
    {
        const string a = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1", b = "b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2";
        var link = new FleetTests.FakeFleetLink();
        link.PeopleNow.Add(new FleetHost(b, "Dana's phone"));
        var main = new MainViewModel(connection, new CardContext { Deliveries = new Deliveries(connection, ui), Folds = new FoldStore(null), Capabilities = () => connection.Capabilities, Host = connection }, new UsageViewModel(), settings);
        await using var fleet = new Fleet(link, main, ui, h => throw new InvalidOperationException("no machines"));
        host.On(HostOps.Settings, d => new SettingsResult(new HostSettingsView(true,
            RemoteFolderTrust: d.TryGetProperty("remoteFolderTrust", out var t) && t.GetBoolean(),
            RemoteFolderTrustDevices: d.TryGetProperty("remoteFolderTrustDevices", out var l) ? [.. l.EnumerateArray().Select(e => e.GetString()!)] : [a]), false));
        host.Status = FakeHost.StatusOf() with { Settings = new HostSettingsView(true, RemoteFolderTrust: true, RemoteFolderTrustDevices: [a]) };
        await Connected();
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
