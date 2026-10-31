using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// The machine's update entity and its Install and Check buttons, against an in-process Home
// Assistant and a stand-in for the host's updater: what is published is what the daemon's
// Publish-CopilotMqttUpdate published for the same moments (Fixtures/ha-update.json), and a press
// takes the host through a check, a download and the hand-over to its updater.
public sealed class UpdateTests : IDisposable
{
    const string Machine = "DSWETT-DEV-VM1";
    const string Releases = "https://github.com/danswett/agent-bridge/releases";
    static readonly string Slug = HaContract.MachineSlug(Machine);

    static readonly Lazy<JsonObject> Loaded = new(() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ha-update.json")))!);
    static JsonObject Fixture => Loaded.Value;

    readonly string folder = Path.Combine(Path.GetTempPath(), "abhu-" + Guid.NewGuid().ToString("N"));
    readonly FakeHomeAssistant ha = new();
    readonly FakeSessions sessions = new();
    readonly FakeTimeProvider time = new(FakeSessions.T0);
    readonly List<string> log = [];
    readonly FakeUpdates updates = new();
    string? daemon;

    public void Dispose()
    {
        if (Directory.Exists(folder)) { Directory.Delete(folder, true); }
    }

    string StateFile => Path.Combine(folder, "ha-connector.json");

    HaConnector Connector(bool check = true, IHostUpdates? updater = null) => new(new HaConnectorOptions
    {
        MachineName = Machine,
        Version = "0.0.9-host",
        StateFile = StateFile,
        CheckForUpdates = check,
    }, ha, sessions, () => daemon, log: m => { lock (log) { log.Add(m); } }, time: time, updates: updater ?? updates);

    async Task Pass(HaConnector c, double seconds = 1)
    {
        time.Advance(TimeSpan.FromSeconds(seconds));
        await c.TickAsync();
    }

    // Started, the buttons pinned onto the dashboard's ids, and the first check taken.
    async Task<HaConnector> Ready(bool check = true)
    {
        var c = Connector(check);
        await Pass(c);
        await Pass(c, 2);
        await Pass(c, 6);
        return c;
    }

    static string Button(string key) => HaContract.MachineEntityId("button", key, Slug);

    async Task Press(HaConnector c, string key)
    {
        c.Observe(ha.Press(Button(key)));
        await Pass(c, 0.01);
    }

    // The background update has finished what it can do now (a gate holds it, or it is done).
    async Task Settle(HaConnector c, Func<bool> until)
    {
        for (var i = 0; i < 200 && !until(); i++) { await Task.Delay(10); }
        Assert.True(until(), "the update beside the passes did not get there");
        await Pass(c, 0.01);
    }

    // Passes until what the passes publish shows the update beside them has got there.
    async Task PassUntil(HaConnector c, Func<bool> until)
    {
        for (var i = 0; i < 200 && !until(); i++)
        {
            await Task.Delay(10);
            await Pass(c, 0.01);
        }
        Assert.True(until(), "the update beside the passes did not get there");
    }

    JsonObject State => JsonNode.Parse(ha.Retained[HaContract.UpdateStateTopic(Slug)])!.AsObject();
    JsonObject Attributes => JsonNode.Parse(ha.Retained[HaContract.UpdateAttributesTopic(Slug)])!.AsObject();
    string Stage => Attributes["stage"]!.GetValue<string>();

    static IReadOnlyList<Published> Recorded(string name) =>
        [.. Fixture["updates"]![name]!.AsArray().Select(p => Published.FromFixture(p!.AsObject()))];

    // The state and attributes publications of a recorded moment.
    static (Published State, Published Attributes) Moment(string name)
    {
        var recorded = Recorded(name);
        return (recorded.Single(p => p.Topic == HaContract.UpdateStateTopic(Slug)), recorded.Single(p => p.Topic == HaContract.UpdateAttributesTopic(Slug)));
    }

    void AssertMoment(string name)
    {
        var (state, attributes) = Moment(name);
        Contract.Equal(state, new Published(state.Topic, ha.Retained[state.Topic], true));
        Contract.Equal(attributes, new Published(attributes.Topic, ha.Retained[attributes.Topic], true));
    }

    static UpdateRelease Release(string version = "0.2.0") => new(version, Fixture["notes"]!.GetValue<string>(), Releases);

    [Fact]
    public void The_entity_and_its_buttons_are_the_daemons_at_every_moment_of_an_update()
    {
        var notes = Fixture["notes"]!.GetValue<string>();
        var moments = new (string Name, JsonObject State, JsonObject Attributes)[]
        {
            ("available", HaPayloads.UpdateState("0.1.0", "0.2.0", Releases, notes, false, null), HaPayloads.UpdateAttributes()),
            ("checking", HaPayloads.UpdateState("0.1.0", "0.2.0", Releases, "", true, null), HaPayloads.UpdateAttributes("checking")),
            ("downloading", HaPayloads.UpdateState("0.1.0", "0.2.0", Releases, "", true, 0.424), HaPayloads.UpdateAttributes("downloading")),
            ("unknown", HaPayloads.UpdateState("0.1.0", null, null, "The latest release could not be established: rate limited", false, null), HaPayloads.UpdateAttributes()),
            ("current", HaPayloads.UpdateState("0.1.0", "0.1.0", null, "", false, null), HaPayloads.UpdateAttributes("current", "Checked just now; 0.1.0 is the latest release.")),
            ("completed", HaPayloads.UpdateState("0.2.0", "0.2.0", Releases, "", false, null), HaPayloads.UpdateAttributes("completed", "Updated to 0.2.0")),
        };
        var (updateTopic, updateConfig) = HaContract.UpdateConfig(Slug, Machine);
        foreach (var (name, state, attributes) in moments)
        {
            Contract.Equal(Recorded(name),
            [
                new Published(updateTopic, HaContract.Serialize(updateConfig), true),
                new Published(HaContract.UpdateStateTopic(Slug), HaContract.Serialize(state), true),
                new Published(HaContract.UpdateAttributesTopic(Slug), HaContract.Serialize(attributes), true),
                .. HaContract.UpdateButtonConfigs(Slug, Machine).Select(b => new Published(b.Topic, HaContract.Serialize(b.Config), true)),
            ]);
        }
        Assert.Equal(2000, HaPayloads.UpdateState("0.1.0", "0.2.0", null, notes, false, null)["release_summary"]!.GetValue<string>().Length);
    }

    [Fact]
    public async Task A_copy_its_updater_did_not_install_reports_its_version_and_offers_no_buttons()
    {
        foreach (var button in HaContract.UpdateButtonConfigs(Slug, Machine)) { ha.Seed(button.Topic, HaContract.Serialize(button.Config)); }
        updates.Installed = null;
        await using var c = await Ready();
        foreach (var button in HaContract.UpdateButtonConfigs(Slug, Machine)) { Assert.False(ha.Retained.ContainsKey(button.Topic), button.Topic); }
        Assert.Equal("0.0.9-host", State["latest_version"]!.GetValue<string>());
        Assert.Equal("0.0.9-host", State["installed_version"]!.GetValue<string>());
        Assert.Equal(0, updates.Checks);
        Assert.DoesNotContain(c.WatchedEntities(), e => e.Contains("update", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_checks_turned_off_look_nothing_up_and_offer_no_buttons()
    {
        await using var c = await Ready(check: false);
        foreach (var button in HaContract.UpdateButtonConfigs(Slug, Machine)) { Assert.False(ha.Retained.ContainsKey(button.Topic), button.Topic); }
        Assert.Equal("0.0.9-host", State["latest_version"]!.GetValue<string>());
        await Pass(c, TimeSpan.FromHours(2).TotalSeconds);
        Assert.Equal(0, updates.Checks);
    }

    [Fact]
    public async Task An_installed_host_serves_both_buttons_on_the_dashboards_ids_and_says_what_the_check_found()
    {
        updates.Newer = Release();
        await using var c = await Ready();
        foreach (var key in new[] { "install_update", "check_updates" }) { Assert.True(ha.Exists(Button(key)), key); }
        // Never withdrawn on the way: Home Assistant would drop the entity, and the dashboard's id with it.
        foreach (var button in HaContract.UpdateButtonConfigs(Slug, Machine)) { Assert.DoesNotContain(ha.Log, p => p.Topic == button.Topic && p.Payload.Length == 0); }
        Assert.Contains(Button("install_update"), c.WatchedEntities());
        AssertMoment("available");
        Assert.Equal(1, updates.Checks);
    }

    [Fact]
    public async Task Until_the_first_check_answers_the_version_installed_is_reported_as_current()
    {
        var gate = updates.CheckGate = new TaskCompletionSource<UpdateRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var c = await Ready();
        Assert.Equal("0.1.0", State["installed_version"]!.GetValue<string>());
        Assert.Equal("0.1.0", State["latest_version"]!.GetValue<string>());
        gate.SetResult(Release());
        await PassUntil(c, () => State["latest_version"]!.GetValue<string>() == "0.2.0");
    }

    [Fact]
    public async Task The_feed_is_asked_on_the_interval_and_sooner_after_it_could_not_be_reached()
    {
        await using var c = await Ready();
        Assert.Equal(1, updates.Checks);
        await Pass(c, TimeSpan.FromMinutes(29).TotalSeconds);
        Assert.Equal(1, updates.Checks);
        await Pass(c, TimeSpan.FromMinutes(2).TotalSeconds);
        Assert.Equal(2, updates.Checks);

        updates.CheckFails = new HttpRequestException("rate limited");
        await Pass(c, TimeSpan.FromMinutes(31).TotalSeconds);
        await Pass(c);
        Assert.Equal(3, updates.Checks);
        await Pass(c, TimeSpan.FromMinutes(6).TotalSeconds);
        Assert.Equal(4, updates.Checks);
    }

    [Fact]
    public async Task A_check_that_never_answers_counts_as_one_that_could_not_reach_the_feed()
    {
        updates.CheckGate = new TaskCompletionSource<UpdateRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var c = await Ready();
        Assert.Equal(1, updates.Checks);
        await Pass(c, TimeSpan.FromMinutes(2).TotalSeconds);
        await Pass(c);
        Assert.False(State.ContainsKey("latest_version"));
        Assert.StartsWith("The latest release could not be established", State["release_summary"]!.GetValue<string>(), StringComparison.Ordinal);
        await Pass(c, TimeSpan.FromMinutes(6).TotalSeconds);
        await Pass(c);
        Assert.Equal(2, updates.Checks);
        Assert.Equal("0.1.0", State["latest_version"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_check_that_cannot_reach_the_feed_says_why_and_is_not_called_a_failed_update()
    {
        updates.ReleasesUrl = null;
        updates.CheckFails = new HttpRequestException("rate limited");
        await using var c = await Ready();
        await Press(c, "check_updates");
        await Pass(c);
        AssertMoment("unknown");
        Assert.Empty(ha.NotifiedNow());
    }

    [Fact]
    public async Task A_press_of_Check_on_a_current_host_answers_for_itself_for_ten_minutes()
    {
        updates.ReleasesUrl = null;
        await using var c = await Ready();
        Assert.Equal("", Stage);
        await Press(c, "check_updates");
        await Pass(c);
        Assert.Equal(2, updates.Checks);
        AssertMoment("current");
        await Pass(c, TimeSpan.FromMinutes(9).TotalSeconds);
        Assert.Equal("current", Stage);
        await Pass(c, TimeSpan.FromMinutes(2).TotalSeconds);
        Assert.Equal("", Stage);
    }

    [Fact]
    public async Task A_check_that_finds_a_newer_release_after_one_that_found_none_is_no_longer_current()
    {
        await using var c = await Ready();
        await Press(c, "check_updates");
        await Pass(c);
        Assert.Equal("current", Stage);
        updates.Newer = Release();
        await Press(c, "check_updates");
        await Pass(c);
        Assert.Equal("", Stage);
        Assert.Equal("0.2.0", State["latest_version"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_check_that_cannot_reach_the_feed_after_one_that_found_none_is_no_longer_current()
    {
        await using var c = await Ready();
        await Press(c, "check_updates");
        await Pass(c);
        Assert.Equal("current", Stage);
        updates.CheckFails = new HttpRequestException("rate limited");
        await Press(c, "check_updates");
        await Pass(c);
        Assert.Equal("", Stage);
        Assert.False(State.ContainsKey("latest_version"));
    }

    [Fact]
    public async Task A_press_made_while_a_failed_start_waits_to_be_tried_again_still_counts()
    {
        updates.Newer = Release();
        await using var c = await Ready();
        daemon = "agent-ha-bridge's daemon (pid 4321)";
        await Pass(c);
        daemon = null;
        // The start after agent-ha-bridge has the buttons up, then fails on the entity's state.
        ha.PublishFailsFor = (topic, _) => topic == HaContract.UpdateStateTopic(Slug);
        await Pass(c);
        Assert.False(c.Started);
        ha.Now = time.GetUtcNow().AddSeconds(5);
        c.Observe(ha.Press(Button("install_update")));
        ha.PublishFailsFor = null;
        await Pass(c, 31);
        Assert.True(c.Started);
        await Pass(c);
        await Settle(c, () => updates.Applied.Count == 1);
    }

    [Fact]
    public async Task A_home_assistant_that_times_out_showing_the_restart_does_not_stop_the_update()
    {
        updates.Newer = Release();
        updates.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var c = await Ready();
        await Press(c, "install_update");
        await Settle(c, () => updates.Downloaded.Count == 1);
        // HttpClient's timeout is a cancellation: the next update state publish times out, once.
        var timedOut = 0;
        ha.BeforePublish = topic =>
        {
            if (topic == HaContract.UpdateStateTopic(Slug) && Interlocked.Exchange(ref timedOut, 1) == 0) { throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 15 seconds elapsing."); }
        };
        updates.DownloadGate.SetResult();
        for (var i = 0; i < 200 && updates.Applied.Count == 0 && !log.Any(l => l.Contains("did not start", StringComparison.Ordinal)); i++) { await Task.Delay(10); }
        Assert.Equal(1, timedOut);
        Assert.Single(updates.Applied);
        Assert.Empty(ha.NotifiedNow());
    }

    [Fact]
    public async Task An_update_that_cannot_be_recorded_is_not_handed_over()
    {
        // Something in the way of the connector's state file: the host that would come back could
        // not tell how the update ended.
        Directory.CreateDirectory(StateFile);
        updates.Newer = Release();
        await using var c = await Ready();
        await Press(c, "install_update");
        await Settle(c, () => ha.NotifiedNow().Count == 1);
        var (_, data) = Assert.Single(ha.NotifiedNow());
        Assert.Equal($"Bridge update failed on {Machine}", data["title"]!.GetValue<string>());
        Assert.Contains("could not be recorded", data["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(updates.Applied);
        Assert.Equal("failed", Stage);
    }

    [Fact]
    public async Task A_press_from_before_the_host_started_is_history_not_an_instruction()
    {
        updates.Newer = Release();
        await using var c = await Ready();
        ha.Now = FakeSessions.T0.AddMinutes(-5);
        await Press(c, "install_update");
        await Press(c, "check_updates");
        await Pass(c);
        Assert.Equal(1, updates.Checks);
        Assert.Empty(updates.Downloaded);
        ha.Now = FakeSessions.T0.AddHours(1);
        await Press(c, "install_update");
        await Settle(c, () => updates.Applied.Count == 1);
    }

    [Fact]
    public async Task Install_checks_again_downloads_with_progress_and_hands_the_release_to_the_updater()
    {
        updates.Newer = Release();
        updates.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var c = await Ready();
        var gate = updates.CheckGate = new TaskCompletionSource<UpdateRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Press(c, "install_update");
        // At once, before anything is looked up: the press answers for itself.
        Assert.Equal("checking", Stage);
        Assert.True(State["in_progress"]!.GetValue<bool>());
        gate.SetResult(updates.Newer);
        await Settle(c, () => updates.Downloaded.Count == 1 && updates.Reported);
        Assert.Equal(2, updates.Checks);
        var (downloading, _) = Moment("downloading");
        var expected = downloading.Json!.AsObject();
        expected["release_summary"] = Fixture["notes"]!.GetValue<string>()[..1997] + "...";
        Contract.AssertJson(expected, State);
        Assert.Equal("downloading", Stage);

        updates.DownloadGate.SetResult();
        await Settle(c, () => updates.Applied.Count == 1);
        Assert.Equal("0.2.0", updates.Applied[0].Version);
        Assert.Equal("restarting", Stage);
        Assert.True(State["in_progress"]!.GetValue<bool>());
        var ledger = JsonNode.Parse(File.ReadAllText(StateFile))!["update"]!;
        Assert.Equal("0.2.0", ledger["target"]!.GetValue<string>());
        Assert.Equal("0.1.0", ledger["before"]!.GetValue<string>());
        // The host that recorded it is about to exit, still on the old version: not a failure.
        for (var i = 0; i < 5; i++) { await Pass(c, 10); }
        Assert.Equal("restarting", Stage);
        Assert.Empty(ha.NotifiedNow());
    }

    [Fact]
    public async Task A_second_press_while_an_update_runs_gets_the_daemons_busy_notice_and_starts_nothing()
    {
        updates.Newer = Release();
        updates.DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var c = await Ready();
        await Press(c, "install_update");
        await Settle(c, () => updates.Downloaded.Count == 1);
        await Press(c, "install_update");
        var (service, data) = Assert.Single(ha.NotifiedNow());
        var busy = Fixture["busy"]![0]!;
        Assert.Equal(busy["service"]!.GetValue<string>(), service);
        Assert.Equal(busy["data"]!["title"]!.GetValue<string>(), data["title"]!.GetValue<string>());
        Assert.Equal(busy["data"]!["notification_id"]!.GetValue<string>(), data["notification_id"]!.GetValue<string>());
        Assert.Equal("This press did not start an update: an update to 0.2.0 is already downloading here. That update reports here when it finishes.", data["message"]!.GetValue<string>());
        Assert.Single(updates.Downloaded);
        // Nor does Check look while the update runs: the install found its release itself.
        var checks = updates.Checks;
        await Press(c, "check_updates");
        await Pass(c);
        Assert.Equal(checks, updates.Checks);
        updates.DownloadGate.SetResult();
        await Settle(c, () => updates.Applied.Count == 1);
        // Handed over and waiting for the host to exit: still busy.
        await Press(c, "install_update");
        Assert.Equal(2, ha.NotifiedNow().Count);
        Assert.Single(updates.Applied);
    }

    [Fact]
    public async Task Install_on_a_host_already_current_says_there_is_no_newer_release()
    {
        await using var c = await Ready();
        await Press(c, "install_update");
        await Settle(c, () => ha.NotifiedNow().Count == 1);
        var (_, data) = Assert.Single(ha.NotifiedNow());
        Assert.Equal($"No newer bridge release on {Machine}", data["title"]!.GetValue<string>());
        Assert.Equal("The bridge update did not start: 0.1.0 is already installed, and it is the latest release", data["message"]!.GetValue<string>());
        Assert.Equal($"agent_bridge_update_{Slug}", data["notification_id"]!.GetValue<string>());
        Assert.Equal("current", Stage);
        Assert.False(State["in_progress"]!.GetValue<bool>());
        Assert.Empty(updates.Downloaded);
    }

    [Fact]
    public async Task A_download_that_fails_says_so_clears_the_spinner_and_leaves_Install_usable()
    {
        updates.Newer = Release();
        updates.DownloadFails = new IOException("disk full");
        await using var c = await Ready();
        await Press(c, "install_update");
        await Settle(c, () => ha.NotifiedNow().Count == 1);
        var (_, data) = Assert.Single(ha.NotifiedNow());
        Assert.Equal($"Bridge update failed on {Machine}", data["title"]!.GetValue<string>());
        Assert.Equal("The bridge update did not start: disk full", data["message"]!.GetValue<string>());
        Assert.Equal("failed", Stage);
        Assert.Equal("disk full", Attributes["stage_detail"]!.GetValue<string>());
        Assert.False(State["in_progress"]!.GetValue<bool>());
        Assert.Null(JsonNode.Parse(File.ReadAllText(StateFile))!["update"]);
        Assert.Empty(updates.Applied);

        updates.DownloadFails = null;
        await Press(c, "install_update");
        await Settle(c, () => updates.Applied.Count == 1);
    }

    [Fact]
    public async Task An_updater_that_cannot_take_the_release_is_a_failure_and_no_restart_is_awaited()
    {
        updates.Newer = Release();
        updates.ApplyFails = new FileNotFoundException("Cannot find Update.exe to apply updates.");
        await using var c = await Ready();
        await Press(c, "install_update");
        await Settle(c, () => ha.NotifiedNow().Count == 1);
        Assert.Equal("failed", Stage);
        Assert.False(State["in_progress"]!.GetValue<bool>());
        Assert.Null(JsonNode.Parse(File.ReadAllText(StateFile))!["update"]);
        await Pass(c, 10);
        Assert.Single(ha.NotifiedNow());

        updates.ApplyFails = null;
        await Press(c, "install_update");
        await Settle(c, () => updates.Applied.Count == 1);
        Assert.Single(ha.NotifiedNow());
    }

    void Attempt(string target, DateTimeOffset at)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(StateFile, HaContract.Serialize(new JsonObject
        {
            ["retiring"] = new JsonArray(),
            ["starting"] = new JsonArray(),
            ["update"] = new JsonObject { ["target"] = target, ["before"] = "0.1.0", ["at"] = at.ToString("o"), ["url"] = Releases },
        }));
    }

    [Fact]
    public async Task The_host_that_comes_back_on_the_new_release_says_the_update_completed()
    {
        Attempt("0.2.0", FakeSessions.T0.AddMinutes(-1));
        updates.Installed = "0.2.0";
        await using var c = await Ready();
        var (_, data) = Assert.Single(ha.NotifiedNow());
        Assert.Equal($"Bridge installer completed on {Machine}", data["title"]!.GetValue<string>());
        Assert.Equal($"The bridge updated to **0.2.0**; currently recorded version: **0.2.0**. The host restarted on it. [Release notes]({Releases})", data["message"]!.GetValue<string>());
        Assert.Equal($"agent_bridge_update_{Slug}", data["notification_id"]!.GetValue<string>());
        Assert.Equal("completed", Stage);
        Assert.Equal("Updated to 0.2.0", Attributes["stage_detail"]!.GetValue<string>());
        Assert.Null(JsonNode.Parse(File.ReadAllText(StateFile))!["update"]);
        await Pass(c, TimeSpan.FromMinutes(59).TotalSeconds);
        Assert.Equal("completed", Stage);
        await Pass(c, TimeSpan.FromMinutes(2).TotalSeconds);
        Assert.Equal("", Stage);
        Assert.Single(ha.NotifiedNow());
    }

    [Fact]
    public async Task The_host_that_comes_back_on_the_old_release_says_the_update_failed()
    {
        Attempt("0.2.0", FakeSessions.T0.AddMinutes(-1));
        await using var c = await Ready();
        var (_, data) = Assert.Single(ha.NotifiedNow());
        Assert.Equal($"Bridge update failed on {Machine}", data["title"]!.GetValue<string>());
        Assert.StartsWith("The bridge update to 0.2.0 did not complete: the host restarted on 0.1.0.", data["message"]!.GetValue<string>());
        Assert.Equal("failed", Stage);
    }

    [Fact]
    public async Task An_outcome_is_told_even_after_update_checks_were_turned_off()
    {
        Attempt("0.2.0", FakeSessions.T0.AddMinutes(-1));
        updates.Installed = "0.2.0";
        await using var c = await Ready(check: false);
        var (_, data) = Assert.Single(ha.NotifiedNow());
        Assert.Equal($"Bridge installer completed on {Machine}", data["title"]!.GetValue<string>());
        Assert.Equal("completed", Stage);
        Assert.Null(JsonNode.Parse(File.ReadAllText(StateFile))!["update"]);
        foreach (var button in HaContract.UpdateButtonConfigs(Slug, Machine)) { Assert.False(ha.Retained.ContainsKey(button.Topic), button.Topic); }
        Assert.Equal(0, updates.Checks);
    }

    [Fact]
    public async Task An_outcome_Home_Assistant_will_not_take_holds_up_no_press_and_is_told_once_it_will()
    {
        Attempt("0.2.0", FakeSessions.T0.AddMinutes(-1));
        updates.Installed = "0.2.0";
        ha.NotifyFailsFor = s => s == "persistent_notification.create";
        updates.Newer = Release("0.3.0");
        await using var c = await Ready();
        Assert.Empty(ha.NotifiedNow());
        var checks = updates.Checks;
        await Press(c, "check_updates");
        await Pass(c);
        Assert.Equal(checks + 1, updates.Checks);
        // Trying the outcome again tells how that update ended, and leaves what the checks found since.
        await Pass(c, 16);
        Assert.Empty(ha.NotifiedNow());
        Assert.Equal("0.3.0", State["latest_version"]!.GetValue<string>());
        ha.NotifyFailsFor = null;
        await Pass(c, 16);
        var (_, data) = Assert.Single(ha.NotifiedNow());
        Assert.Equal($"Bridge installer completed on {Machine}", data["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_outcome_whose_notice_times_out_holds_up_nothing_else_in_the_pass()
    {
        Attempt("0.2.0", FakeSessions.T0.AddMinutes(-1));
        updates.Installed = "0.2.0";
        // HttpClient's timeout is a cancellation, not an error.
        ha.NotifyThrows = s => s == "persistent_notification.create" ? new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 15 seconds elapsing.") : null;
        await using var c = await Ready();
        var checks = updates.Checks;
        await Press(c, "check_updates");
        await Pass(c);
        Assert.Equal(checks + 1, updates.Checks);
        ha.NotifyThrows = null;
        await Pass(c, 16);
        Assert.Single(ha.NotifiedNow());
    }

    [Fact]
    public async Task An_outcome_older_than_six_hours_is_not_announced()
    {
        Attempt("0.2.0", FakeSessions.T0.AddHours(-7));
        updates.Installed = "0.2.0";
        await using var c = await Ready();
        Assert.Empty(ha.NotifiedNow());
        Assert.Equal("", Stage);
        Assert.Null(JsonNode.Parse(File.ReadAllText(StateFile))!["update"]);
    }

    [Fact]
    public async Task An_outcome_waits_while_agent_ha_bridge_has_the_machine()
    {
        Attempt("0.2.0", FakeSessions.T0.AddMinutes(-1));
        updates.Installed = "0.2.0";
        daemon = "agent-ha-bridge's daemon (pid 4321)";
        await using var c = Connector();
        await Pass(c);
        await Pass(c, 5);
        Assert.Empty(ha.NotifiedNow());
        Assert.NotNull(JsonNode.Parse(File.ReadAllText(StateFile))!["update"]);
        daemon = null;
        await Pass(c);
        await Pass(c, 2);
        Assert.Single(ha.NotifiedNow());
    }
}

// The host's updater, standing in: what it finds, and gates to hold a check or a download where a
// test wants to look.
internal sealed class FakeUpdates : IHostUpdates
{
    public string? Installed { get; set; } = "0.1.0";
    public string? ReleasesUrl { get; set; } = "https://github.com/danswett/agent-bridge/releases";
    public UpdateRelease? Newer { get; set; }
    public Exception? CheckFails { get; set; }
    public TaskCompletionSource<UpdateRelease?>? CheckGate { get; set; }
    public TaskCompletionSource? DownloadGate { get; set; }
    public Exception? DownloadFails { get; set; }
    public Exception? ApplyFails { get; set; }
    public int Checks;
    public volatile bool Reported;
    public List<UpdateRelease> Downloaded { get; } = [];
    public List<UpdateRelease> Applied { get; } = [];

    public Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Checks);
        if (CheckGate is { } gate)
        {
            CheckGate = null;
            return gate.Task.WaitAsync(cancellationToken);
        }
        return CheckFails is { } e ? Task.FromException<UpdateRelease?>(e) : Task.FromResult(Newer);
    }

    public async Task DownloadAsync(UpdateRelease release, Action<int> progress, CancellationToken cancellationToken)
    {
        lock (Downloaded) { Downloaded.Add(release); }
        if (DownloadFails is { } e) { throw e; }
        progress(42);
        Reported = true;
        if (DownloadGate is { } gate) { await gate.Task.WaitAsync(cancellationToken); }
        progress(100);
    }

    public void ApplyAndRestart(UpdateRelease release)
    {
        if (ApplyFails is { } e) { throw e; }
        lock (Applied) { Applied.Add(release); }
    }
}
