using System.Text.Json;
using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// The connector's life against an in-process Home Assistant: starting, a session's card, a session
// ending, a restart, and taking a machine over from agent-ha-bridge's daemon.
public sealed class LifecycleTests : IDisposable
{
    const string Machine = "DSWETT-DEV-VM1";
    const string Slug = "dswett_dev_vm1";
    const string A = "6fcbab0c-1d2e-4f50-9a61-7b8c9d0e1f20";
    const string B = "c1a2b3c4-d5e6-4f70-8a91-b2c3d4e5f607";
    static readonly string NodeA = HaContract.NodeId(A);
    static readonly string NodeB = HaContract.NodeId(B);

    readonly string folder = Path.Combine(Path.GetTempPath(), "abha-" + Guid.NewGuid().ToString("N"));
    readonly FakeHomeAssistant ha = new();
    readonly FakeSessions sessions = new();
    readonly FakeTimeProvider time = new(FakeSessions.T0);
    readonly List<string> log = [];
    string? agentHaBridge;
    Func<string?>? agentHaBridgeProbe;
    UsageReport usage = UsageReport.Empty;
    long usageVersion;
    IReadOnlyList<string> accountSensors = [];
    bool usageOff;

    public void Dispose()
    {
        if (Directory.Exists(folder)) { Directory.Delete(folder, true); }
    }

    HaConnector Connector() => new(new HaConnectorOptions
    {
        MachineName = Machine,
        Version = "0.1.0",
        Dev = true,
        StateFile = Path.Combine(folder, "ha-connector.json"),
        UsageAccountSensors = accountSensors,
    }, ha, sessions, () => agentHaBridgeProbe is { } probe ? probe() : agentHaBridge, usageOff ? null : () => (usage, usageVersion), m => { lock (log) { log.Add(m); } }, time);

    async Task Pass(HaConnector c, double seconds = 0)
    {
        if (seconds > 0) { time.Advance(TimeSpan.FromSeconds(seconds)); }
        await c.TickAsync();
    }

    // Enough passes, a second apart, for the reconcile-paced work (pins, the dashboard check).
    async Task Run(HaConnector c, double seconds)
    {
        for (var t = 0; t < seconds; t++) { await Pass(c, 1); }
    }

    [Fact]
    public async Task Nothing_is_published_while_agent_ha_bridge_runs_for_this_user_and_everything_is_once_it_stops()
    {
        agentHaBridge = "agent-ha-bridge is running for this user (AgentBridgeDaemon_0123)";
        sessions.Set(FakeSessions.Copilot(A));
        await using var c = Connector();
        await Run(c, 30);
        Assert.Empty(ha.Log);
        Assert.False(c.Started);
        Assert.Contains(log, l => l.Contains("paused", StringComparison.Ordinal));

        agentHaBridge = null;
        await Pass(c, 1);
        Assert.True(c.Started);
        Assert.NotEmpty(ha.To(HaContract.Topics(NodeA).StatusState));

        // And again if it starts while the connector runs: not one more message.
        agentHaBridge = "agent-ha-bridge is running for this user (task AgentBridgeDaemon (running))";
        var before = ha.Log.Count;
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Working));
        await Run(c, 120);
        Assert.Equal(before, ha.Log.Count);
    }

    [Fact]
    public async Task Agent_ha_bridge_starting_part_way_through_a_pass_stops_the_rest_of_it()
    {
        sessions.Set(FakeSessions.Copilot(A));
        sessions.Set(FakeSessions.Copilot(B));
        await using var c = Connector();
        // The daemon appears once three messages have gone: the startup pass is far from done.
        var probe = () => ha.Log.Count >= 3 ? "agent-ha-bridge is running for this user (AgentBridgeDaemon_0123)" : null;
        agentHaBridgeProbe = probe;
        await Pass(c);
        Assert.Equal(3, ha.Log.Count);
        Assert.False(c.Started);
        Assert.Contains(log, l => l.Contains("paused", StringComparison.Ordinal));
        await Run(c, 30);
        Assert.Equal(3, ha.Log.Count);
    }

    [Fact]
    public async Task Liveness_is_declared_before_its_first_beat_which_is_never_retained_and_every_entity_gets_its_contract_id()
    {
        sessions.Set(FakeSessions.Copilot(A));
        await using var c = Connector();
        await Pass(c);
        var online = HaContract.OnlineConfig(Slug, Machine).Topic;
        Assert.Single(ha.To(online));
        Assert.Empty(ha.To(HaContract.OnlineStateTopic(Slug)));

        await Run(c, 3);
        var beat = Assert.Single(ha.To(HaContract.OnlineStateTopic(Slug)));
        Assert.Equal("online", beat.Payload);
        Assert.False(beat.Retain);
        Assert.True(ha.Log.IndexOf(ha.To(online)[0]) < ha.Log.IndexOf(beat));

        await Run(c, 60);
        Assert.Equal(2, ha.To(HaContract.OnlineStateTopic(Slug)).Count);

        // Home Assistant named them after their devices; the connector moved them onto the ids
        // the dashboard and the other machines use.
        Assert.True(ha.Exists($"binary_sensor.agent_bridge_{Slug}_online"));
        Assert.True(ha.Exists($"sensor.agent_bridge_{Slug}_sessions"));
        Assert.True(ha.Exists($"update.agent_bridge_{Slug}_update"));
        Assert.True(ha.Exists($"sensor.{NodeA}_status"));
        Assert.True(ha.Exists($"sensor.{NodeA}_activity"));
    }

    [Fact]
    public async Task A_live_session_gets_its_two_sensors_and_nothing_this_stage_does_not_serve()
    {
        sessions.Set(FakeSessions.Copilot(A, response: "Done.\nMore"));
        await using var c = Connector();
        await Run(c, 3);
        var t = HaContract.Topics(NodeA);
        Assert.Equal("online", ha.Retained[t.Availability]);
        Assert.True(ha.Retained.ContainsKey(HaContract.DiscoveryTopic("sensor", NodeA, "status")));
        Assert.True(ha.Retained.ContainsKey(HaContract.DiscoveryTopic("sensor", NodeA, "activity")));
        Assert.Equal("idle", ha.Retained[t.StatusState]);
        Assert.Equal("Done.", ha.Retained[t.ActivityState]);
        var detail = JsonDocument.Parse(ha.Retained[t.ActivityAttributes]).RootElement;
        Assert.Equal("Done.\nMore", detail.GetProperty("response").GetString());
        Assert.Equal("human", detail.GetProperty("driver").GetString());

        // The question, reply and button entities are withdrawn, not left as controls nothing
        // here would act on.
        foreach (var topic in HaContract.SessionDiscoveryTopics(NodeA).Where(x => !x.EndsWith("/status/config", StringComparison.Ordinal) && !x.EndsWith("/activity/config", StringComparison.Ordinal)))
        {
            Assert.False(ha.Retained.ContainsKey(topic), topic);
            Assert.Contains(ha.To(topic), p => p.Payload.Length == 0 && p.Retain);
        }

        var global = JsonDocument.Parse(ha.Retained[HaContract.GlobalAttributesTopic(Slug)]).RootElement;
        var listed = Assert.Single(global.GetProperty("sessions").EnumerateArray());
        Assert.Equal(NodeA, listed.GetProperty("node").GetString());
        Assert.Equal("Copilot: Fixture session", listed.GetProperty("name").GetString());
        Assert.False(global.GetProperty("capabilities").GetProperty("newSession").GetBoolean());
        Assert.Equal("1", ha.Retained[HaContract.GlobalStateTopic(Slug)]);

        // Unchanged, nothing is sent again; a change sends only what changed.
        var statusWrites = ha.To(t.StatusState).Count;
        var activityWrites = ha.To(t.ActivityState).Count;
        await Run(c, 10);
        Assert.Equal(statusWrites, ha.To(t.StatusState).Count);
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Working, response: "Done.\nMore"));
        await Pass(c, 1);
        Assert.Equal(statusWrites + 1, ha.To(t.StatusState).Count);
        Assert.Equal("working", ha.Retained[t.StatusState]);
        Assert.Equal(activityWrites, ha.To(t.ActivityState).Count);
    }

    [Fact]
    public async Task What_holds_the_hosts_discovery_reaches_the_status_line_as_discovery_held_and_clears_with_it()
    {
        sessions.Set(FakeSessions.Copilot(A));
        await using var c = Connector();
        await Run(c, 3);
        string Held() => JsonDocument.Parse(ha.Retained[HaContract.GlobalAttributesTopic(Slug)]).RootElement.GetProperty("discovery_held").GetString()!;
        Assert.Equal("", Held());

        sessions.Held = new DiscoveryHeld("an unrecognised copilot process (pid 4120)", [AgentKind.Copilot]);
        await Pass(c, 1);
        Assert.Equal("an unrecognised copilot process (pid 4120)", Held());

        sessions.Held = null;
        await Pass(c, 1);
        Assert.Equal("", Held());
    }

    [Fact]
    public async Task A_session_ending_leaves_the_list_at_once_and_its_entities_only_after_its_card_has_left_the_dashboard()
    {
        sessions.Set(FakeSessions.Copilot(A));
        sessions.Set(FakeSessions.Copilot(B));
        ha.Draw(NodeA, NodeB);
        await using var c = Connector();
        await Run(c, 3);

        sessions.End(B);
        await Pass(c, 1);
        var global = JsonDocument.Parse(ha.Retained[HaContract.GlobalAttributesTopic(Slug)]).RootElement;
        Assert.Equal([NodeA], global.GetProperty("sessions").EnumerateArray().Select(s => s.GetProperty("node").GetString()));

        // Still drawn: the entities stay, however long.
        await Run(c, 120);
        Assert.True(ha.Exists($"sensor.{NodeB}_status"));
        Assert.Contains(NodeB, c.PendingRetirement);

        // Gone from the served dashboard: one more pass for browsers to catch up, then all 29.
        ha.Draw(NodeA);
        ha.ClearLog();
        await Run(c, 16);
        Assert.True(ha.Exists($"sensor.{NodeB}_status"));
        await Run(c, 30);
        Assert.False(ha.Exists($"sensor.{NodeB}_status"));
        Assert.False(ha.Exists($"sensor.{NodeB}_activity"));
        Assert.Empty(c.PendingRetirement);
        var removal = HaContract.SessionRemoval(NodeB).Select(Published.Of).ToList();
        Assert.Equal(removal, ha.Log.Where(p => p.Topic.Contains(NodeB, StringComparison.Ordinal)).ToList());
        Assert.DoesNotContain(ha.Retained.Keys, k => k.Contains(NodeB, StringComparison.Ordinal));
        Assert.True(ha.Exists($"sensor.{NodeA}_status"));
    }

    [Fact]
    public async Task A_change_landing_while_a_pass_reads_the_sessions_is_published_by_the_next_one()
    {
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Working));
        await using var c = Connector();
        await Run(c, 3);
        var t = HaContract.Topics(NodeA);
        Assert.Equal("working", ha.Retained[t.StatusState]);

        // The session's last change: noticed once, between this pass's snapshot and its publishing.
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Working, response: "Halfway"));
        sessions.AfterSnapshot = () => sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Idle, response: "Done"));
        await Run(c, 5);
        Assert.Equal("idle", ha.Retained[t.StatusState]);
    }

    [Fact]
    public async Task A_session_that_ends_before_its_first_publication_finished_is_still_retired()
    {
        sessions.Set(FakeSessions.Copilot(A));
        ha.Draw(NodeA);
        await using var c = Connector();
        await Run(c, 3);

        // B's sensors and availability go, then a withdrawal fails; B ends before the retry.
        ha.PublishFailsFor = (topic, payload) => payload.Length == 0 && topic.Contains(NodeB, StringComparison.Ordinal);
        sessions.Set(FakeSessions.Copilot(B));
        await Pass(c, 1);
        Assert.Contains(ha.Retained.Keys, k => k.Contains(NodeB, StringComparison.Ordinal));
        sessions.End(B);
        ha.PublishFailsFor = null;
        await Run(c, 60);

        Assert.False(ha.Exists($"sensor.{NodeB}_status"));
        Assert.DoesNotContain(ha.Retained.Keys, k => k.Contains(NodeB, StringComparison.Ordinal));
        Assert.Empty(c.PendingRetirement);
    }

    [Fact]
    public async Task A_session_whose_first_publication_a_crash_interrupted_is_retired_if_it_ended_meanwhile()
    {
        sessions.Set(FakeSessions.Copilot(A));
        ha.Draw(NodeA);
        // Never disposed: the host is killed, so only what was on disk before each write survives.
        var first = Connector();
        await Run(first, 3);
        ha.PublishFailsFor = (topic, payload) => payload.Length == 0 && topic.Contains(NodeB, StringComparison.Ordinal);
        sessions.Set(FakeSessions.Copilot(B));
        await Pass(first, 1);
        Assert.Contains(ha.Retained.Keys, k => k.Contains(NodeB, StringComparison.Ordinal));

        // B ends while the host is down; nothing B left behind is in the retained session list.
        sessions.End(B);
        ha.PublishFailsFor = null;
        await using var second = Connector();
        await Run(second, 60);

        Assert.DoesNotContain(ha.Retained.Keys, k => k.Contains(NodeB, StringComparison.Ordinal));
        Assert.Empty(second.PendingRetirement);
        Assert.Equal("idle", ha.Retained[HaContract.Topics(NodeA).StatusState]);
    }

    [Fact]
    public async Task A_dashboard_that_cannot_be_read_holds_every_retirement()
    {
        sessions.Set(FakeSessions.Copilot(B));
        await using var c = Connector();
        await Run(c, 3);
        sessions.End(B);
        ha.DashboardFails = new HttpRequestException("Home Assistant answered 502 Bad Gateway");
        await Run(c, 120);
        Assert.True(ha.Exists($"sensor.{NodeB}_status"));
        Assert.Contains(NodeB, c.PendingRetirement);

        // No dashboard at all is an answer, not a failure: nothing renders the card.
        ha.DashboardFails = null;
        ha.Dashboard = null;
        await Run(c, 31);
        Assert.False(ha.Exists($"sensor.{NodeB}_status"));
    }

    [Fact]
    public async Task A_restart_retires_what_was_waiting_and_republishes_live_sessions_without_duplicates()
    {
        sessions.Set(FakeSessions.Copilot(A));
        sessions.Set(FakeSessions.Copilot(B));
        ha.Draw(NodeA, NodeB);
        await using (var first = Connector())
        {
            await Run(first, 3);
            sessions.End(B);
            await Run(first, 20);
            Assert.Contains(NodeB, first.PendingRetirement);
        }
        var entities = ha.Registry.Count;

        // The list no longer names B, so only the remembered retirement brings it back.
        await using var second = Connector();
        await Pass(second);
        Assert.Contains(NodeB, second.PendingRetirement);
        await Run(second, 5);
        Assert.Equal(entities, ha.Registry.Count);
        Assert.True(ha.Exists($"sensor.{NodeA}_status"));
        Assert.DoesNotContain(ha.Registry.Values, id => id.EndsWith("_2", StringComparison.Ordinal));

        ha.Draw(NodeA);
        await Run(second, 31);
        Assert.False(ha.Exists($"sensor.{NodeB}_status"));
        Assert.True(ha.Exists($"sensor.{NodeA}_status"));
    }

    static string Config(string name, string uniqueId, string device) => HaContract.Serialize(new System.Text.Json.Nodes.JsonObject
    {
        ["name"] = name,
        ["unique_id"] = uniqueId,
        ["device"] = new System.Text.Json.Nodes.JsonObject { ["name"] = device },
    });

    [Fact]
    public async Task Taking_over_from_agent_ha_bridge_retires_its_ended_sessions_and_withdraws_its_controls_this_stage_does_not_serve()
    {
        // What the PowerShell daemon left: its session list naming A (still running) and B
        // (ended while nothing ran), B's sensors, A's question selector, and its launch and
        // install controls.
        var (sessionsTopic, sessionsConfig) = HaContract.SessionsConfig(Slug, Machine);
        ha.Seed(sessionsTopic, HaContract.Serialize(sessionsConfig));
        ha.Seed(HaContract.GlobalAttributesTopic(Slug), $$"""{"sessions":[{"node":"{{NodeA}}","name":"Copilot: x","machine":"{{Machine}}","kind":"copilot"},{"node":"{{NodeB}}","name":"Claude: y","machine":"{{Machine}}","kind":"claude"}],"machine":"{{Machine}}","machine_slug":"{{Slug}}"}""");
        var (statusTopic, statusConfig) = HaContract.StatusConfig(HaContract.Topics(NodeB), "Claude: y", Machine);
        ha.Seed(statusTopic, HaContract.Serialize(statusConfig));
        ha.Seed(HaContract.DiscoveryTopic("select", NodeA, "decision"), Config("Decision", $"{NodeA}_decision", "Copilot: x"));
        ha.Seed(HaContract.DiscoveryTopic("button", HaContract.MachineNode(Slug), "install_update"), Config("Install Bridge Update", $"agent_bridge_{Slug}_install_update", $"AI Agent Bridge ({Machine})"));
        ha.Seed(HaContract.DiscoveryTopic("button", HaContract.MachineNode(Slug), "new_session"), Config("Launch", $"agent_bridge_{Slug}_new_session", $"AI Agent Bridge ({Machine})"));
        ha.Seed($"{HaContract.MachineTopicRoot(Slug)}/newsession/result", "Started");
        sessions.Set(FakeSessions.Copilot(A));
        ha.Draw(NodeA);
        // As the daemon left them: pinned onto the contract's ids.
        ha.Registry[$"agent_bridge_{Slug}_sessions"] = $"sensor.agent_bridge_{Slug}_sessions";
        ha.Registry[$"{NodeB}_status"] = $"sensor.{NodeB}_status";

        await using var c = Connector();
        await Run(c, 3);
        Assert.False(ha.Retained.ContainsKey(HaContract.DiscoveryTopic("select", NodeA, "decision")));
        Assert.False(ha.Retained.ContainsKey(HaContract.DiscoveryTopic("button", HaContract.MachineNode(Slug), "install_update")));
        Assert.False(ha.Retained.ContainsKey(HaContract.DiscoveryTopic("button", HaContract.MachineNode(Slug), "new_session")));
        Assert.False(ha.Retained.ContainsKey($"{HaContract.MachineTopicRoot(Slug)}/newsession/result"));
        Assert.Contains(NodeB, c.PendingRetirement);

        await Run(c, 31);
        Assert.DoesNotContain(ha.Retained.Keys, k => k.Contains(NodeB, StringComparison.Ordinal));
        Assert.True(ha.Exists($"sensor.{NodeA}_status"));
        // Exactly one sessions sensor, one online sensor and one update entity for the machine.
        Assert.Single(ha.Registry, e => e.Key == $"agent_bridge_{Slug}_sessions");
        Assert.Equal($"sensor.agent_bridge_{Slug}_sessions", ha.Registry[$"agent_bridge_{Slug}_sessions"]);
    }

    [Fact]
    public async Task Taking_over_withdraws_the_update_check_the_launch_outcomes_and_further_logins_usage_but_not_the_agents_own_usage()
    {
        var node = HaContract.MachineNode(Slug);
        var root = HaContract.MachineTopicRoot(Slug);
        var device = $"AI Agent Bridge ({Machine})";
        ha.Seed(HaContract.DiscoveryTopic("button", node, "check_updates"), Config("Check for Bridge Update", $"agent_bridge_{Slug}_check_updates", device));
        ha.Seed($"{root}/newsession/outcome", """{"recent":[{"request":"r-12345678","outcome":"started"}]}""");
        foreach (var key in new[] { "copilot", "copilot_2" })
        {
            ha.Seed(HaContract.DiscoveryTopic("sensor", node, $"usage_{key}"), Config("Copilot usage", $"agent_bridge_{Slug}_usage_{key}", device));
            ha.Seed(HaContract.UsageStateTopic(Slug, key), "42");
            ha.Seed(HaContract.UsageAttributesTopic(Slug, key), "{}");
        }

        await using var c = Connector();
        await Run(c, 3);

        Assert.False(ha.Retained.ContainsKey(HaContract.DiscoveryTopic("button", node, "check_updates")));
        Assert.False(ha.Retained.ContainsKey($"{root}/newsession/outcome"));
        Assert.DoesNotContain(ha.Retained.Keys, k => k.Contains("copilot_2", StringComparison.Ordinal));
        Assert.True(ha.Retained.ContainsKey(HaContract.DiscoveryTopic("sensor", node, "usage_copilot")));
        Assert.Equal("42", ha.Retained[HaContract.UsageStateTopic(Slug, "copilot")]);
        // The beat's time is never retained, and never cleared as if it were.
        Assert.DoesNotContain(ha.Log, p => p.Topic == HaContract.OnlineAttributesTopic(Slug) && (p.Retain || p.Payload.Length == 0));
    }

    [Fact]
    public async Task A_further_copilot_login_goes_on_the_numbered_sensor_of_its_place_and_one_no_longer_listed_is_withdrawn()
    {
        var node = HaContract.MachineNode(Slug);
        var device = $"AI Agent Bridge ({Machine})";
        // An earlier configuration's readings: copilot_3 is no longer listed, copilot_2 still is.
        foreach (var key in new[] { "copilot_2", "copilot_3" })
        {
            ha.Seed(HaContract.DiscoveryTopic("sensor", node, $"usage_{key}"), Config("GitHub Copilot usage (old)", $"agent_bridge_{Slug}_usage_{key}", device));
            ha.Seed(HaContract.UsageStateTopic(Slug, key), "12");
        }
        accountSensors = ["copilot_2"];
        usage = new UsageReport(
        [
            new AgentUsage { Agent = AgentKind.Copilot, Label = "GitHub Copilot", Account = "octocat", Source = UsageSource.Api, MeasuredAt = FakeSessions.T0,
                Windows = [new UsageWindow { Key = "plan", Label = "Plan", Percent = 40 }] },
            new AgentUsage { Agent = AgentKind.Copilot, Label = "GitHub Copilot", Account = "hubot", Source = UsageSource.Api, MeasuredAt = FakeSessions.T0, Slot = 2,
                Windows = [new UsageWindow { Key = "plan", Label = "Plan", Percent = 7.5 }] },
            // A reading for a place this host was not configured with is not published.
            new AgentUsage { Agent = AgentKind.Copilot, Label = "GitHub Copilot", Account = "monalisa", Source = UsageSource.None, MeasuredAt = FakeSessions.T0, Slot = 3,
                Error = "No Copilot credential was available for monalisa." },
        ]);
        usageVersion = 1;

        await using var c = Connector();
        await Run(c, 3);

        Assert.DoesNotContain(ha.Retained.Keys, k => k.Contains("copilot_3", StringComparison.Ordinal));
        Assert.Equal("40", ha.Retained[HaContract.UsageStateTopic(Slug, "copilot")]);
        Assert.Equal("7.5", ha.Retained[HaContract.UsageStateTopic(Slug, "copilot_2")]);
        using var config = JsonDocument.Parse(ha.Retained[HaContract.DiscoveryTopic("sensor", node, "usage_copilot_2")]);
        Assert.Equal("GitHub Copilot usage (hubot)", config.RootElement.GetProperty("name").GetString());
        using var attributes = JsonDocument.Parse(ha.Retained[HaContract.UsageAttributesTopic(Slug, "copilot_2")]);
        Assert.Equal(("copilot", "hubot"), (attributes.RootElement.GetProperty("client").GetString(), attributes.RootElement.GetProperty("account").GetString()));
        // Its entity id forced onto the one the dashboard lists, as the agents' own are.
        await Run(c, 15);
        Assert.Equal($"sensor.agent_bridge_{Slug}_usage_copilot_2", ha.Registry[$"agent_bridge_{Slug}_usage_copilot_2"]);
    }

    [Fact]
    public async Task With_usage_turned_off_every_usage_sensor_this_machine_left_is_withdrawn()
    {
        var node = HaContract.MachineNode(Slug);
        foreach (var key in new[] { "copilot", "claude", "copilot_2" })
        {
            ha.Seed(HaContract.DiscoveryTopic("sensor", node, $"usage_{key}"), Config("usage", $"agent_bridge_{Slug}_usage_{key}", $"AI Agent Bridge ({Machine})"));
            ha.Seed(HaContract.UsageStateTopic(Slug, key), "42");
        }
        usageOff = true;

        await using var c = Connector();
        await Run(c, 3);

        Assert.DoesNotContain(ha.Retained.Keys, k => k.Contains("/usage", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_heartbeat_says_online_and_when()
    {
        await using var c = Connector();
        await Run(c, 3);
        var beats = ha.Log.Where(p => p.Topic.StartsWith($"{HaContract.MachineTopicRoot(Slug)}/online/", StringComparison.Ordinal)).ToList();
        Assert.Contains(beats, p => p.Topic == HaContract.OnlineStateTopic(Slug) && p.Payload == "online" && !p.Retain);
        var at = Assert.Single(beats, p => p.Topic == HaContract.OnlineAttributesTopic(Slug)).Payload;
        Assert.True(DateTimeOffset.TryParse(System.Text.Json.Nodes.JsonNode.Parse(at)!["at"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _));
    }

    [Fact]
    public async Task The_orphan_sweep_takes_unlisted_sessions_and_leaves_another_machines_alone()
    {
        const string Other = "dswett_home";
        const string Theirs = "9f8e7d6c-5b4a-4321-8765-0123456789ab";
        const string Orphan = "0a1b2c3d-4e5f-4607-8899-aabbccddeeff";
        var theirs = HaContract.NodeId(Theirs);
        var orphan = HaContract.NodeId(Orphan);
        ha.States[$"sensor.agent_bridge_{Other}_sessions"] = new HaState($"sensor.agent_bridge_{Other}_sessions", "1",
            JsonDocument.Parse($$"""{"sessions":[{"node":"{{theirs}}"}]}""").RootElement.Clone());
        ha.States[$"sensor.{theirs}_status"] = new HaState($"sensor.{theirs}_status", "working", default);
        ha.States[$"sensor.{orphan}_status"] = new HaState($"sensor.{orphan}_status", "idle", default);
        await using var c = Connector();
        await Pass(c);
        Assert.Contains(orphan, c.PendingRetirement);
        Assert.DoesNotContain(theirs, c.PendingRetirement);
    }

    [Fact]
    public async Task With_no_machine_list_visible_the_orphan_sweep_takes_nothing()
    {
        const string Orphan = "0a1b2c3d-4e5f-4607-8899-aabbccddeeff";
        var orphan = HaContract.NodeId(Orphan);
        ha.States[$"sensor.{orphan}_status"] = new HaState($"sensor.{orphan}_status", "idle", default);
        await using var c = Connector();
        await Pass(c);
        Assert.DoesNotContain(orphan, c.PendingRetirement);
        Assert.Contains(log, l => l.Contains("no machine's session list is visible", StringComparison.Ordinal));
    }

    [Fact]
    public async Task While_delegates_run_the_card_shows_the_main_agents_own_text_and_lists_the_delegates_beside_it()
    {
        // The latest text is a delegate's (source names it); the main agent's own is in own.
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Working) with
        {
            Activity = new Activity
            {
                Response = "Delegate: all 40 tests pass",
                Reasoning = "Delegate: checking the log",
                Source = new ActivitySource { Response = "agent-3a91", Reasoning = "agent-3a91" },
                Own = new OwnActivity { Response = "Main answer.\nMore of it", Reasoning = "**Main thinking**" },
                Background = Background.Of(1, 0),
            },
            Subagents =
            [
                new Subagent { Id = "agent-3a91", Kind = SubagentKind.Background, Name = "Run full test suite", Status = SubagentStatus.Working, Line = "Delegate: all 40 tests pass" },
                new Subagent { Id = "agent-c204", Kind = SubagentKind.Task, Name = "Read the {{ log }}", Status = SubagentStatus.Ended, Outcome = SubagentOutcome.Done },
            ],
        });
        await using var c = Connector();
        await Run(c, 3);
        var t = HaContract.Topics(NodeA);
        Assert.Equal("Main answer.", ha.Retained[t.ActivityState]);
        var detail = JsonDocument.Parse(ha.Retained[t.ActivityAttributes]).RootElement;
        Assert.Equal("Main answer.\nMore of it", detail.GetProperty("response").GetString());
        Assert.Contains("Main answer.", detail.GetProperty("history").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain(detail.GetProperty("history").EnumerateArray(), e => e.GetString()!.Contains("Delegate", StringComparison.Ordinal));

        var listed = detail.GetProperty("subagents").EnumerateArray().ToList();
        Assert.Equal(2, listed.Count);
        Assert.Equal(("Run full test suite", "working", "Delegate: all 40 tests pass"),
            (listed[0].GetProperty("name").GetString(), listed[0].GetProperty("status").GetString(), listed[0].GetProperty("line").GetString()));
        Assert.Equal(("Read the {\u200B{ log }}", "ended"), (listed[1].GetProperty("name").GetString(), listed[1].GetProperty("status").GetString()));
        Assert.False(listed[1].TryGetProperty("line", out _));
        Assert.False(detail.TryGetProperty("subagents_more", out _));

        // The main agent writes again: its own words are the top-level ones and nothing is attributed.
        sessions.Set(FakeSessions.Copilot(A, response: "Final answer.") with { Subagents = null });
        await Pass(c, 1);
        detail = JsonDocument.Parse(ha.Retained[t.ActivityAttributes]).RootElement;
        Assert.Equal("Final answer.", detail.GetProperty("response").GetString());
        Assert.False(detail.TryGetProperty("subagents", out _));
    }

    [Fact]
    public async Task Detailed_activity_follows_the_machines_switch_and_falls_back_to_the_setting_without_one()
    {
        sessions.Set(FakeSessions.Copilot(A) with { Activity = new Activity { Reasoning = "**Weighing it**", Response = null } });
        // A Home Assistant that will not say what helpers it has gets no switch made for it.
        ha.HelperListFails = new HaCommandException("input_boolean/list", "unauthorized", "Unauthorized");
        await using var c = Connector();
        await Run(c, 3);
        var attr = HaContract.Topics(NodeA).ActivityAttributes;
        Assert.True(JsonDocument.Parse(ha.Retained[attr]).RootElement.GetProperty("verbose").GetBoolean());
        var capabilities = JsonDocument.Parse(ha.Retained[HaContract.GlobalAttributesTopic(Slug)]).RootElement.GetProperty("capabilities");
        Assert.False(capabilities.GetProperty("detailed").GetBoolean());

        var toggle = $"input_boolean.agent_bridge_{Slug}_detailed_activity";
        ha.States[toggle] = new HaState(toggle, "off", default);
        await Run(c, 16);
        var detail = JsonDocument.Parse(ha.Retained[attr]).RootElement;
        Assert.False(detail.GetProperty("verbose").GetBoolean());
        Assert.False(detail.TryGetProperty("response", out _));
        capabilities = JsonDocument.Parse(ha.Retained[HaContract.GlobalAttributesTopic(Slug)]).RootElement.GetProperty("capabilities");
        Assert.True(capabilities.GetProperty("detailed").GetBoolean());

        ha.States[toggle] = new HaState(toggle, "on", default);
        await Run(c, 16);
        detail = JsonDocument.Parse(ha.Retained[attr]).RootElement;
        Assert.Equal("reasoning", detail.GetProperty("response_kind").GetString());
        Assert.Contains("Thinking: Weighing it", detail.GetProperty("history").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task A_new_turns_thinking_replaces_the_previous_turns_reply_until_its_own_reply_arrives()
    {
        ha.Helper(HaContract.DetailedHelperId(Slug), HaContract.DetailedHelperName(Slug), "on");
        sessions.Set(FakeSessions.Copilot(A, response: "First answer"));
        await using var c = Connector();
        await Run(c, 3);
        var attr = HaContract.Topics(NodeA).ActivityAttributes;
        JsonElement Detail() => JsonDocument.Parse(ha.Retained[attr]).RootElement;
        Assert.Equal("text", Detail().GetProperty("response_kind").GetString());

        // A new turn: the host clears the reasoning and keeps the last reply, then thinks.
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Working, response: "First answer"));
        await Pass(c, 1);
        Assert.Equal("First answer", Detail().GetProperty("response").GetString());
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Working) with { Activity = new Activity { Response = "First answer", Reasoning = "Weighing the second" } });
        await Pass(c, 1);
        Assert.Equal("reasoning", Detail().GetProperty("response_kind").GetString());
        Assert.Equal("Weighing the second", Detail().GetProperty("response").GetString());

        // Another step that changes neither text keeps the thought.
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Working) with { Activity = new Activity { Response = "First answer", Reasoning = "Weighing the second", TurnCount = 2 } });
        await Pass(c, 1);
        Assert.Equal("reasoning", Detail().GetProperty("response_kind").GetString());

        sessions.Set(FakeSessions.Copilot(A) with { Activity = new Activity { Response = "Second answer", Reasoning = "Weighing the second" } });
        await Pass(c, 1);
        Assert.Equal("text", Detail().GetProperty("response_kind").GetString());
        Assert.Equal("Second answer", Detail().GetProperty("response").GetString());
    }

    [Fact]
    public async Task Usage_is_published_when_a_reading_changes_and_not_again_when_it_does_not()
    {
        var reading = new AgentUsage
        {
            Agent = AgentKind.Copilot, Label = "GitHub Copilot", Source = UsageSource.Api, MeasuredAt = FakeSessions.T0,
            Windows = [new UsageWindow { Key = "premium", Label = "Premium requests", Percent = 10 }],
        };
        usage = new UsageReport([reading]);
        usageVersion = 1;
        await using var c = Connector();
        await Run(c, 3);
        var state = HaContract.UsageStateTopic(Slug, "copilot");
        Assert.Equal("10", ha.Retained[state]);
        Assert.True(ha.Exists($"sensor.agent_bridge_{Slug}_usage_copilot"));

        usageVersion = 2;
        await Run(c, 3);
        Assert.Single(ha.To(state));

        usage = new UsageReport([reading with { Windows = [new UsageWindow { Key = "premium", Label = "Premium requests", Percent = 12.5 }] }]);
        usageVersion = 3;
        await Pass(c, 1);
        Assert.Equal("12.5", ha.Retained[state]);
    }

    [Fact]
    public async Task A_home_assistant_that_is_down_is_retried_and_said_once()
    {
        ha.PublishFails = new HttpRequestException("Connection refused");
        sessions.Set(FakeSessions.Copilot(A));
        await using var c = Connector();
        await Run(c, 100);
        Assert.False(c.Started);
        Assert.Single(log, l => l.Contains("could not start", StringComparison.Ordinal));

        ha.PublishFails = null;
        await Run(c, 31);
        Assert.True(c.Started);
        Assert.Equal("idle", ha.Retained[HaContract.Topics(NodeA).StatusState]);
    }

    [Fact]
    public async Task Nothing_is_published_on_the_way_out()
    {
        sessions.Set(FakeSessions.Copilot(A));
        var c = Connector();
        await Run(c, 3);
        var count = ha.Log.Count;
        await c.DisposeAsync();
        Assert.Equal(count, ha.Log.Count);
        Assert.Equal("online", ha.Retained[HaContract.Topics(NodeA).Availability]);
    }
}
