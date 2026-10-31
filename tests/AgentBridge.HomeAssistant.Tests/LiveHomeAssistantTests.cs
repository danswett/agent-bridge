using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using Xunit;

// The connector against a real Home Assistant and Mosquitto: the containers the ha-integration CI
// job starts empty, onboards (tools/ha-ci/onboard.py) and throws away. Skipped everywhere else,
// and refused outright unless the address is loopback and the run says it is disposable, so these
// can never write to a Home Assistant somebody lives with.
public sealed class HomeAssistantFactAttribute : FactAttribute
{
    public HomeAssistantFactAttribute()
    {
        if (LiveHomeAssistant.WhyNot() is { } why) { Skip = why; }
    }
}

internal static class LiveHomeAssistant
{
    public static string? Url => Environment.GetEnvironmentVariable("AGENT_BRIDGE_HA_TEST_URL");
    public static string? Token => Environment.GetEnvironmentVariable("AGENT_BRIDGE_HA_TEST_TOKEN");

    public static string? WhyNot()
    {
        if (string.IsNullOrEmpty(Url) || string.IsNullOrEmpty(Token)) { return "needs the disposable Home Assistant of the ha-integration CI job"; }
        if (Environment.GetEnvironmentVariable("AGENT_BRIDGE_HA_TEST_DISPOSABLE") != "1") { return "AGENT_BRIDGE_HA_TEST_DISPOSABLE is not 1"; }
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || !uri.IsLoopback) { return $"{Url} is not a loopback address"; }
        return null;
    }
}

[Trait("Category", "HomeAssistantIntegration")]
public sealed class LiveHomeAssistantTests : IDisposable
{
    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    readonly HomeAssistantClient ha;
    readonly FakeSessions sessions = new();
    readonly List<AnswerRequest> answered = [];
    readonly List<HaReply> replied = [];
    readonly List<StopRequest> stopped = [];
    readonly List<LaunchRequest> launched = [];
    readonly List<string> log = [];
    HaNotifySettings notify = HaNotifySettings.Default;
    IHostUpdates? hostUpdates;

    public LiveHomeAssistantTests()
    {
        ha = new HomeAssistantClient(new Uri(LiveHomeAssistant.Url ?? "http://127.0.0.1:1"), LiveHomeAssistant.Token ?? "", http);
    }

    public void Dispose() => http.Dispose();

    // Each test is its own machine, so none sees another's entities.
    HaConnector Connector(string machine, params string[] agents) => Connector(machine, null, agents);

    HaConnector Connector(string machine, LaunchOptions? launches, params string[] agents) => new(new HaConnectorOptions
    {
        MachineName = machine,
        Version = "0.1.0",
        Dev = true,
        Tick = TimeSpan.FromMilliseconds(250),
        Reconcile = TimeSpan.FromSeconds(2),
        RetireGrace = TimeSpan.FromSeconds(2),
        AgentUserIds = agents,
        Notify = notify,
    }, ha, sessions, () => null, log: m => { lock (log) { log.Add(m); } }, answer: (r, _) =>
    {
        lock (answered) { answered.Add(r); }
        return Task.FromResult(new AnswerResult(AnswerOutcome.Delivered));
    }, reply: (r, _) =>
    {
        lock (replied) { replied.Add(r); }
        return Task.FromResult(new ReplyResult(AnswerOutcome.Delivered));
    }, stop: (r, _) =>
    {
        // The host's rule: a session that is not idle asks first, and its token confirms.
        lock (stopped) { stopped.Add(r); }
        if (r.Confirm != "live-token")
        {
            return Task.FromResult(new StopResult(StopOutcome.Confirm, "this session is still working") { Confirm = "live-token", ConfirmSeconds = 30 });
        }
        sessions.End(r.Id);
        return Task.FromResult(new StopResult(StopOutcome.Ended));
    }, launch: launches is null ? null : new HaLauncher(() => true, _ => Task.FromResult(launches), (r, _) =>
    {
        lock (launched) { launched.Add(r); }
        return Task.FromResult(new LaunchResult(LaunchOutcome.Started)
        {
            Session = Guid.NewGuid().ToString(),
            Launched = new LaunchedWith { Agent = r.Agent ?? "copilot", Workspace = r.Workspace ?? "agent-bridge", Permissions = r.Permissions ?? LaunchProtocol.Ask },
        });
    }), updates: hostUpdates);

    static string Session() => Guid.NewGuid().ToString();

    static readonly Field Release = new()
    {
        Name = "release", Title = "Release", Type = FieldType.Choice, Required = true, AllowOther = true,
        Options = [new Option("cut_now", "Cut the release now"), new Option("wait", "Wait for the fix")],
    };

    void Ask(string id, Question? question, int turn = 1) =>
        sessions.Set(FakeSessions.Copilot(id, status: SessionStatus.Waiting) with
        {
            Question = question,
            Activity = new Activity { Response = "Working on it", TurnCount = turn, UserMessage = $"turn {turn}" },
        });

    static Question Form(string id, params Field[] fields) => new()
    {
        Id = id, Kind = QuestionKind.Form, Message = "Cut the release?", AskedAt = DateTimeOffset.UtcNow, Fields = fields,
    };

    async Task<HaState> Until(string entityId, Func<HaState, bool> condition, int seconds = 90)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        HaState? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await ha.GetStateAsync(entityId);
            if (last is not null && condition(last)) { return last; }
            await Task.Delay(250);
        }
        string history;
        lock (log) { history = string.Join("\n", log.TakeLast(20)); }
        Assert.Fail($"{entityId} never got there; last {last?.State ?? "(none)"} {(last is null ? "" : last.Attributes.ToString())}\n{history}");
        return null!;
    }

    async Task Until(Func<bool> condition, string what, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, what);
            await Task.Delay(250);
        }
    }

    [HomeAssistantFact]
    public async Task Home_assistant_builds_the_card_on_the_ids_the_dashboard_uses_and_arms_its_question()
    {
        var id = Session();
        var node = HaContract.NodeId(id);
        Ask(id, Form("q1", Release));
        await using var c = Connector("CI-CARD");
        c.Start();

        // The attributes come last, after the selector is Awaiting, as the daemon publishes them.
        await Until(SessionEntities.Decision(node), s => s.State == HaDecision.Awaiting
            && s.Attributes.TryGetProperty("decision_id", out var d) && d.GetString() == "q1");
        var field = await Until(SessionEntities.Field(node, 1), s => s.State == HaDecision.Choose);
        Assert.Contains("Cut the release now", field.Attributes.GetProperty("options").EnumerateArray().Select(o => o.GetString()));
        foreach (var entity in new[] { SessionEntities.Status(node), SessionEntities.Activity(node), SessionEntities.Submit(node), SessionEntities.Reply(node), SessionEntities.ReplyPayload(node), SessionEntities.Stop(node) })
        {
            Assert.NotNull(await ha.GetStateAsync(entity));
        }
        var slug = HaContract.MachineSlug("CI-CARD");
        // The beat's time lands on the sensor's attributes (agent-ha-bridge #202).
        await Until(HaContract.MachineEntityId("binary_sensor", "online", slug), s => s.State == "on" && s.Attributes.TryGetProperty("at", out _));
        await Until(HaContract.MachineEntityId("sensor", "sessions", slug), s => s.State == "1");
    }

    [HomeAssistantFact]
    public async Task End_session_pressed_twice_on_a_working_session_reaches_the_hosts_stop_with_its_token()
    {
        var id = Session();
        var node = HaContract.NodeId(id);
        sessions.Set(FakeSessions.Copilot(id, status: SessionStatus.Working) with
        {
            Activity = new Activity { Response = "Working on it", TurnCount = 1, UserMessage = "go" },
        });
        await using var c = Connector("CI-STOP");
        c.Start();
        await Until(SessionEntities.Stop(node), _ => true);
        await Until(SessionEntities.Status(node), s => s.State == "working");
        // The connector reads what the button holds before taking a press from it.
        await Task.Delay(TimeSpan.FromSeconds(2));

        await ha.CallServiceAsync("button", "press", new JsonObject { ["entity_id"] = SessionEntities.Stop(node) });
        var asking = await Until(SessionEntities.Activity(node), s => s.State == HaStop.ConfirmNote);
        Assert.Equal("this session is still working - the confirmation lapses in 30s", asking.Attributes.GetProperty("hint").GetString());

        await ha.CallServiceAsync("button", "press", new JsonObject { ["entity_id"] = SessionEntities.Stop(node) });
        await Until(() => { lock (stopped) { return stopped.Count == 2; } }, "the second press never reached the host");
        Assert.Equal([null, "live-token"], stopped.Select(r => r.Confirm));
        // Ended, and then retired soon after with no dashboard drawing it: either is the end.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        string? last = "working";
        while (last is not ("ended" or null))
        {
            Assert.True(DateTime.UtcNow < deadline, $"the card never said the session ended; last {last}");
            await Task.Delay(100);
            last = (await ha.GetStateAsync(SessionEntities.Status(node)))?.State;
        }
    }

    [HomeAssistantFact]
    public async Task A_press_on_the_card_reaches_the_hosts_answer_and_says_who_pressed_it()
    {
        var me = await CurrentUserAsync();
        var id = Session();
        var node = HaContract.NodeId(id);
        Ask(id, Form("q1", Release));
        await using var c = Connector("CI-PRESS", me);
        c.Start();
        await Until(SessionEntities.Field(node, 1), s => s.State == HaDecision.Choose);

        // What the card does: choose, then Send. This token's account is configured as an agent.
        await ha.CallServiceAsync("select", "select_option", new JsonObject { ["entity_id"] = SessionEntities.Field(node, 1), ["option"] = "Wait for the fix" });
        await ha.CallServiceAsync("button", "press", new JsonObject { ["entity_id"] = SessionEntities.Submit(node) });
        await Until(() => { lock (answered) { return answered.Count == 1; } }, "the answer never arrived");
        Assert.Equal("wait", answered[0].Fields!["release"].GetString());
        await Until(SessionEntities.Activity(node), s => s.State == "Answer sent");

        Ask(id, null, turn: 2);
        await Until(SessionEntities.Activity(node), s => s.Attributes.TryGetProperty("driver", out var d) && d.GetString() == "agent");
        await Until(SessionEntities.Decision(node), s => s.State == HaDecision.Idle);
    }

    [HomeAssistantFact]
    public async Task Words_from_the_reply_card_answer_a_freeform_question_and_the_card_is_told()
    {
        var id = Session();
        var node = HaContract.NodeId(id);
        Ask(id, Form("q1"));
        await using var c = Connector("CI-WORDS");
        c.Start();
        await Until(SessionEntities.Decision(node), s => s.State == HaDecision.Awaiting);

        var at = DateTimeOffset.UtcNow.ToString("o");
        await ha.PublishAsync(HaContract.Topics(node).ReplyPayload, HaContract.Serialize(new JsonObject { ["at"] = at, ["text"] = "Call it Aurora", ["decision_id"] = "q1" }), true);
        await Until(() => { lock (answered) { return answered.Count == 1; } }, "the answer never arrived");
        Assert.Equal("Call it Aurora", answered[0].Text);
        await Until(SessionEntities.Activity(node), s => s.Attributes.TryGetProperty("answer_consumed_at", out var a) && a.GetString() == at);
    }

    [HomeAssistantFact]
    public async Task An_ended_session_is_retired_when_no_dashboard_draws_it()
    {
        // A fresh Home Assistant has no agent-decisions dashboard: Home Assistant's answer for that
        // must read as "nothing draws it", not as a dashboard that cannot be read.
        Assert.False((await ha.ReadDashboardAsync(HaSettings.DefaultDashboard)).Found);
        var id = Session();
        var node = HaContract.NodeId(id);
        Ask(id, null);
        await using var c = Connector("CI-RETIRE");
        c.Start();
        await Until(SessionEntities.Status(node), _ => true);
        sessions.End(id);
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (await ha.GetStateAsync(SessionEntities.Status(node)) is not null)
        {
            Assert.True(DateTime.UtcNow < deadline, "the ended session's entities were never retired");
            await Task.Delay(500);
        }
        Assert.Null(await ha.GetStateAsync(SessionEntities.Decision(node)));
        // The entities go with their configs; the retained values follow, availability last.
        string[] filters = [$"{HaContract.TopicRoot}/{node}/#", $"{HaContract.DiscoveryPrefix}/+/{node}/#"];
        IReadOnlyList<HaMessage> left;
        while ((left = await ha.ReadTopicsAsync(filters, TimeSpan.FromSeconds(2))).Count > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, $"still retained: {string.Join(", ", left.Select(m => m.Topic))}");
            await Task.Delay(500);
        }
    }

    [HomeAssistantFact]
    public async Task The_shadow_reads_what_the_broker_holds_for_the_daemons_machine_and_writes_nothing()
    {
        // What agent-ha-bridge's daemon published, from its own recorded publications.
        foreach (var p in Contract.Publications("sessionPublish").Concat(Contract.Publications("globalStatus")))
        {
            await ha.PublishAsync(p.Topic, p.Payload, true);
        }
        const string machine = "DSWETT-DEV-VM1";
        var node = HaContract.NodeId("6fcbab0c-1d2e-4f50-9a61-7b8c9d0e1f20");
        string[] filters = [$"{HaContract.TopicRoot}/{node}/#", $"{HaContract.DiscoveryPrefix}/+/{node}/#", $"{HaContract.MachineTopicRoot(HaContract.MachineSlug(machine))}/#"];
        var before = await ha.ReadTopicsAsync(filters, TimeSpan.FromSeconds(3));
        Assert.Contains(before, m => m.Retain && m.Topic == HaContract.DiscoveryTopic("button", node, "stop"));
        sessions.Set(FakeSessions.Copilot("6fcbab0c-1d2e-4f50-9a61-7b8c9d0e1f20"));

        var report = await HaShadow.CompareAsync(ha, sessions, new HaConnectorOptions { MachineName = machine, Version = "0.1.0", Dev = true }, true, TimeSpan.FromSeconds(3));

        Assert.DoesNotContain(report.Differences, d => d.Topic == HaContract.DiscoveryTopic("select", node, "decision"));
        // End session too, now that the host serves it.
        Assert.DoesNotContain(report.Differences, d => d.Topic == HaContract.DiscoveryTopic("button", node, "stop"));
        var after = await ha.ReadTopicsAsync(filters, TimeSpan.FromSeconds(3));
        Assert.Equal(before.Select(m => (m.Topic, m.Payload)).Order(), after.Select(m => (m.Topic, m.Payload)).Order());
    }

    [HomeAssistantFact]
    public async Task The_reply_box_and_send_reach_the_hosts_reply_and_the_box_is_cleared()
    {
        var id = Session();
        var node = HaContract.NodeId(id);
        Ask(id, null);
        await using var c = Connector("CI-REPLY");
        c.Start();
        await Until(SessionEntities.Submit(node), _ => true);
        await Until(SessionEntities.Reply(node), _ => true);
        // The connector reads what the card holds before taking anything from it.
        await Task.Delay(TimeSpan.FromSeconds(2));

        await ha.CallServiceAsync("text", "set_value", new JsonObject { ["entity_id"] = SessionEntities.Reply(node), ["value"] = "Ship it after the hotfix" });
        await ha.CallServiceAsync("button", "press", new JsonObject { ["entity_id"] = SessionEntities.Submit(node) });
        await Until(() => { lock (replied) { return replied.Count == 1; } }, "the reply never arrived");
        Assert.Equal("Ship it after the hotfix", replied[0].Text);
        Assert.Equal(id, replied[0].SessionId);
        await Until(SessionEntities.Activity(node), s => s.State == "Reply sent"
            && s.Attributes.TryGetProperty("sent", out var sent) && sent.GetString() == "Ship it after the hotfix");
        await Until(SessionEntities.Reply(node), s => s.State == HaDecision.ReplyBlank);
    }

    [HomeAssistantFact]
    public async Task A_reply_card_publish_with_an_image_and_a_file_reaches_the_hosts_reply_and_the_image_leaves_the_store()
    {
        var id = Session();
        var node = HaContract.NodeId(id);
        Ask(id, null);
        await using var c = Connector("CI-IMAGE");
        c.Start();
        await Until(SessionEntities.ReplyPayload(node), _ => true);
        await Task.Delay(TimeSpan.FromSeconds(2));

        // What the card does: the image into Home Assistant's own store, then one publish naming
        // it, not retained, with the file inline.
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        var image = await UploadImageAsync(png);
        Assert.Equal(png, await ha.DownloadImageAsync(image, 1024 * 1024));
        var notes = "line one\nline two\n"u8.ToArray();
        await ha.PublishAsync(HaContract.Topics(node).ReplyPayload, HaContract.Serialize(new JsonObject
        {
            ["at"] = DateTimeOffset.UtcNow.ToString("o"),
            ["text"] = "What is this?",
            ["images"] = new JsonArray(new JsonObject { ["id"] = image, ["name"] = "dot.png", ["content_type"] = "image/png" }),
            ["files"] = new JsonArray(new JsonObject { ["name"] = "notes.txt", ["content_type"] = "text/plain", ["b64"] = Convert.ToBase64String(notes) }),
        }), false);

        await Until(() => { lock (replied) { return replied.Count == 1; } }, "the reply never arrived");
        Assert.Equal("What is this?", replied[0].Text);
        Assert.Equal(["dot.png", "notes.txt"], replied[0].Files.Select(f => f.Name));
        Assert.Equal(png, replied[0].Files[0].Bytes);
        Assert.Equal(notes, replied[0].Files[1].Bytes);
        await Until(SessionEntities.Activity(node), s => s.State == "Reply sent");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (await ha.DownloadImageAsync(image, 1024 * 1024) is not null)
        {
            Assert.True(DateTime.UtcNow < deadline, "the image was never deleted from Home Assistant's store");
            await Task.Delay(250);
        }
    }

    [HomeAssistantFact]
    public async Task Launch_on_the_card_and_an_agents_request_both_reach_the_hosts_launcher_and_the_request_hears_how_it_went()
    {
        var offered = new LaunchOptions
        {
            Agents =
            [
                new LaunchAgent { Id = "copilot", Label = "Copilot", Kind = AgentKind.Copilot, Models = ["gpt-5.5"] },
                new LaunchAgent { Id = "claude", Label = "Claude", Kind = AgentKind.Claude },
            ],
            Workspaces = [new LaunchWorkspace { Label = "agent-bridge", Path = "/repos/agent-bridge" }, new LaunchWorkspace { Label = "scratch", Path = "/scratch" }],
            Defaults = new LaunchDefaults { Agent = "copilot", Workspace = "agent-bridge", Permissions = LaunchProtocol.Ask },
        };
        await using var c = Connector("CI-LAUNCH", offered);
        c.Start();
        var slug = HaContract.MachineSlug("CI-LAUNCH");
        string Entity(string domain, string key) => HaContract.MachineEntityId(domain, key, slug);
        // The selectors are driven to the machine's defaults once Home Assistant has made them.
        await Until(Entity("select", "new_agent"), s => s.State == "Copilot");
        await Until(Entity("select", "new_workspace"), s => s.State == "agent-bridge");
        await Until(Entity("text", "new_prompt"), s => s.State == HaDecision.ReplyBlank);
        await Task.Delay(TimeSpan.FromSeconds(2));

        // A person's launch: what the card shows.
        await ha.CallServiceAsync("select", "select_option", new JsonObject { ["entity_id"] = Entity("select", "new_workspace"), ["option"] = "scratch" });
        await ha.CallServiceAsync("text", "set_value", new JsonObject { ["entity_id"] = Entity("text", "new_prompt"), ["value"] = "Hello from CI" });
        await ha.CallServiceAsync("button", "press", new JsonObject { ["entity_id"] = Entity("button", "new_session") });
        await Until(() => { lock (launched) { return launched.Count == 1; } }, "the card's launch never reached the host");
        Assert.Equal(("copilot", "scratch", "Hello from CI"), (launched[0].Agent, launched[0].Workspace, launched[0].Prompt));
        await Until(Entity("text", "new_prompt"), s => s.State == HaDecision.ReplyBlank);

        // An agent's: its request and settings in the prompt payload, then the press.
        await ha.PublishAsync(HaContract.PromptPayloadTopic(slug), HaContract.Serialize(new JsonObject
        {
            ["at"] = DateTimeOffset.UtcNow.ToString("o"),
            ["text"] = "Fix the build",
            ["request"] = "ci-launch-0001",
            ["settings"] = new JsonObject { ["agent"] = "Claude", ["workspace"] = "agent-bridge" },
        }), true);
        await Until(Entity("sensor", "new_prompt_payload"), s => s.Attributes.TryGetProperty("request", out var r) && r.GetString() == "ci-launch-0001");
        await ha.CallServiceAsync("button", "press", new JsonObject { ["entity_id"] = Entity("button", "new_session") });
        await Until(() => { lock (launched) { return launched.Count == 2; } }, "the agent's launch never reached the host");
        Assert.Equal(("claude", "agent-bridge", "Fix the build"), (launched[1].Agent, launched[1].Workspace, launched[1].Prompt));
        await Until(Entity("sensor", "new_session_result"), s => s.Attributes.TryGetProperty("recent", out var recent)
            && recent.GetArrayLength() > 0 && recent[0].GetProperty("request").GetString() == "ci-launch-0001"
            && recent[0].GetProperty("outcome").GetString() == "started");
    }

    [HomeAssistantFact]
    public async Task A_question_goes_to_the_phones_with_its_buttons_a_tap_answers_it_and_the_hooks_pushes_follow()
    {
        // persistent_notification stands in for a phone: Home Assistant's own notify service, which
        // takes the same title, message and data and keeps what it was sent where it can be read.
        var services = await ServicesAsync();
        Assert.True(services.Contains("notify.persistent_notification"), "this Home Assistant has no notify.persistent_notification to stand in for a phone");
        const string Service = "notify.persistent_notification";
        notify = new HaNotifySettings { Enabled = true, Service = Service, AnswerButtonServices = [Service] };
        var me = await CurrentUserAsync();
        var id = Session();
        var node = HaContract.NodeId(id);
        var before = (await NotificationsAsync()).Count;
        sessions.Set(FakeSessions.Copilot(id, status: SessionStatus.Working) with { Activity = new Activity { TurnCount = 1, UserMessage = "go" } });
        await using var c = Connector("CI-NOTIFY", me);
        c.Start();
        await Until(SessionEntities.Status(node), s => s.State == "working");
        Ask(id, Form("q1", Release));
        await Until(SessionEntities.Decision(node), s => s.State == HaDecision.Awaiting);

        // The buttons' notification, and the hook's push.
        var shown = await UntilNotified(n => n.Title == "Copilot: Fixture session" && n.Message == "Cut the release?", before);
        await UntilNotified(n => n.Title == "Copilot: Copilot: Fixture session" && n.Message.Contains("1. Cut the release now", StringComparison.Ordinal), before);

        // A tap, as the companion app fires it.
        await PostEventAsync(HaNotify.ActionEvent, new JsonObject
        {
            ["action"] = "BRIDGE_q1_1",
            ["action_data"] = new JsonObject { ["bridge"] = "decision", ["session"] = id, ["decision"] = "q1" },
        });
        await Until(() => { lock (answered) { return answered.Count == 1; } }, "the tap never answered");
        Assert.Equal("wait", answered[0].Fields!["release"].GetString());
        await Until(SessionEntities.Activity(node), s => s.State == "Answer sent");
        await UntilNotified(n => n.Message == HaNotify.ClearMessage, before);

        // The turn the tap started finishes: its reply is pushed.
        sessions.Set(FakeSessions.Copilot(id, status: SessionStatus.Idle) with { Activity = new Activity { Response = "Waiting for the fix, then cutting.", TurnCount = 2, UserMessage = "wait" } });
        await UntilNotified(n => n.Title == "Copilot response: Copilot: Fixture session" && n.Message == "Waiting for the fix, then cutting.", before);
        await Until(SessionEntities.Activity(node), s => s.Attributes.TryGetProperty("driver", out var d) && d.GetString() == "agent");
        Assert.NotNull(shown);
    }

    [HomeAssistantFact]
    public async Task The_update_entity_shows_the_release_found_and_Install_downloads_it_and_hands_it_to_the_updater()
    {
        var updater = new FakeUpdates
        {
            Newer = new UpdateRelease("0.2.0", "## What changed\nA fix.", "https://github.com/danswett/agent-bridge/releases"),
            DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        hostUpdates = updater;
        var before = (await NotificationsAsync()).Count;
        await using var c = Connector("CI-UPDATE");
        c.Start();
        var slug = HaContract.MachineSlug("CI-UPDATE");
        var entity = HaContract.MachineEntityId("update", "update", slug);
        string Button(string key) => HaContract.MachineEntityId("button", key, slug);

        // Home Assistant took the state (its schema refuses the whole payload for one wrong key):
        // a release is on offer, and the buttons are on the dashboard's ids.
        await Until(entity, s => s.State == "on" && s.Attributes.TryGetProperty("latest_version", out var l) && l.GetString() == "0.2.0"
            && s.Attributes.GetProperty("installed_version").GetString() == "0.1.0");
        await Until(Button("check_updates"), _ => true);
        await Until(Button("install_update"), _ => true);
        await Task.Delay(TimeSpan.FromSeconds(1));

        var checks = updater.Checks;
        await ha.CallServiceAsync("button", "press", new JsonObject { ["entity_id"] = Button("check_updates") });
        await Until(() => updater.Checks > checks, "a press of Check never asked the feed");

        await ha.CallServiceAsync("button", "press", new JsonObject { ["entity_id"] = Button("install_update") });
        await Until(entity, s => s.Attributes.TryGetProperty("in_progress", out var p) && p.ValueKind == JsonValueKind.True
            && s.Attributes.TryGetProperty("update_percentage", out var pc) && pc.ValueKind == JsonValueKind.Number && pc.GetDouble() == 42
            && s.Attributes.TryGetProperty("stage", out var st) && st.GetString() == "downloading");

        // Pressed again while it downloads: the daemon's notice, and nothing more started.
        await ha.CallServiceAsync("button", "press", new JsonObject { ["entity_id"] = Button("install_update") });
        await UntilNotified(n => n.Title == "Bridge update already running on CI-UPDATE", before);
        Assert.Single(updater.Downloaded);

        updater.DownloadGate.SetResult();
        await Until(() => { lock (updater.Applied) { return updater.Applied.Count == 1; } }, "the release never reached the updater");
        await Until(entity, s => s.Attributes.TryGetProperty("stage", out var st) && st.GetString() == "restarting");
    }

    [HomeAssistantFact]
    public async Task The_detailed_activity_switch_is_made_once_named_for_its_machine_and_a_second_start_keeps_its_value()
    {
        var slug = HaContract.MachineSlug("CI-UPKEEP");
        var toggle = HaContract.MachineEntityId("input_boolean", "detailed_activity", slug);
        await using (var c = Connector("CI-UPKEEP"))
        {
            c.Start();
            await Until(toggle, s => s.State == "off");
            await Until(HaContract.MachineEntityId("sensor", "sessions", slug), s => s.Attributes.GetProperty("capabilities").GetProperty("detailed").GetBoolean());
        }
        var registry = (await ha.ListEntityRegistryAsync()).Single(e => e.EntityId == toggle);
        Assert.Equal(HaContract.DetailedHelperDisplayName("CI-UPKEEP"), registry.Name);

        // Switched on from the dashboard, then the host starts again: the same switch, still on.
        await ha.CallServiceAsync("input_boolean", "turn_on", new JsonObject { ["entity_id"] = toggle });
        await Until(toggle, s => s.State == "on");
        await using (var again = Connector("CI-UPKEEP"))
        {
            again.Start();
            await Until(HaContract.MachineEntityId("binary_sensor", "online", slug), s => s.State == "on", seconds: 30);
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        Assert.Equal("on", (await ha.GetStateAsync(toggle))!.State);
        Assert.Equal([HaContract.DetailedHelperId(slug)], (await ha.ListInputBooleansAsync()).Where(id => id.StartsWith(HaContract.DetailedHelperId(slug), StringComparison.Ordinal)));
    }

    [HomeAssistantFact]
    public async Task A_machine_forgotten_from_the_dashboard_while_its_host_runs_is_published_again_whole()
    {
        var id = Session();
        var node = HaContract.NodeId(id);
        sessions.Set(FakeSessions.Copilot(id, status: SessionStatus.Working, response: "Halfway there"));
        var slug = HaContract.MachineSlug("CI-FORGET");
        // The daemon's timings, shortened: a beat every two seconds, a stall after three of them.
        await using var c = new HaConnector(new HaConnectorOptions
        {
            MachineName = "CI-FORGET", Version = "0.1.0", Dev = true,
            Tick = TimeSpan.FromMilliseconds(250), Reconcile = TimeSpan.FromSeconds(2), RetireGrace = TimeSpan.FromSeconds(2),
            Heartbeat = TimeSpan.FromSeconds(2), OnlineStall = TimeSpan.FromSeconds(6), EntityRestore = TimeSpan.FromSeconds(4),
        }, ha, sessions, () => null, log: m => { lock (log) { log.Add(m); } });
        c.Start();
        var wanted = new[]
        {
            HaContract.MachineEntityId("binary_sensor", "online", slug), HaContract.MachineEntityId("sensor", "sessions", slug),
            HaContract.MachineEntityId("update", "update", slug), $"sensor.{node}_status", $"sensor.{node}_activity",
        };
        foreach (var entity in wanted) { await Until(entity, _ => true); }
        await Until(HaContract.MachineEntityId("binary_sensor", "online", slug), s => s.State == "on");

        // The machine row's X: an empty retained message on every topic the writer lists for it.
        foreach (var topic in HaContract.ForgetTopics(slug, [node])) { await ha.PublishAsync(topic, "", true); }
        await Until(() => ha.GetStateAsync(wanted[0]).GetAwaiter().GetResult() is null, "the forget did not remove the machine", 30);

        foreach (var entity in wanted) { await Until(entity, _ => true, seconds: 120); }
        await Until($"sensor.{node}_status", s => s.State == "working");
        await Until(HaContract.MachineEntityId("binary_sensor", "online", slug), s => s.State == "on");
        Assert.Contains(log.ToArray(), l => l.Contains("publishing the machine again", StringComparison.Ordinal));
    }

    // This restarts the one Home Assistant every live test shares. The live tests are one class, so
    // none runs beside it, and it finishes only once Home Assistant has this machine's MQTT entities
    // back, so a test after it still finds a working one.
    [HomeAssistantFact]
    public async Task After_Home_Assistant_restarts_the_card_is_put_back_on_the_same_ids_and_its_question_still_answers()
    {
        var id = Session();
        var node = HaContract.NodeId(id);
        Ask(id, Form("q1", Release));
        var slug = HaContract.MachineSlug("CI-RESTART");
        var online = HaContract.MachineEntityId("binary_sensor", "online", slug);
        await using var c = new HaConnector(new HaConnectorOptions
        {
            MachineName = "CI-RESTART", Version = "0.1.0", Dev = true,
            Tick = TimeSpan.FromMilliseconds(250), Reconcile = TimeSpan.FromSeconds(2), RetireGrace = TimeSpan.FromSeconds(2),
            EntityRestore = TimeSpan.FromSeconds(10),
        }, ha, sessions, () => null, log: m => { lock (log) { log.Add(m); } }, answer: (r, _) =>
        {
            lock (answered) { answered.Add(r); }
            return Task.FromResult(new AnswerResult(AnswerOutcome.Delivered));
        });
        c.Start();
        await Until(SessionEntities.Field(node, 1), s => s.State == HaDecision.Choose);
        await Until(SessionEntities.Reply(node), _ => true);
        await Until(online, s => s.State == "on");
        var before = (await ha.ListEntityRegistryAsync()).Select(e => e.EntityId).Where(e => e.Contains(node, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();

        // Restarted as Settings > Restart does it. The call itself may not survive the restart.
        try { await ha.CallServiceAsync("homeassistant", "restart", new JsonObject()); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
        await Until(() =>
        {
            try { return ha.GetStateAsync(online).GetAwaiter().GetResult() is null; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { return true; }
        }, "Home Assistant never went down", 120);

        // Back, with the reply box holding a value again and the question on the card again.
        async Task<HaState> Back(string entityId, Func<HaState, bool> condition, int seconds = 240)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (true)
            {
                try { return await Until(entityId, condition, 15); }
                catch (Exception e) when (DateTime.UtcNow < deadline && e is HttpRequestException or TaskCanceledException or Xunit.Sdk.FailException) { }
            }
        }
        await Back(online, s => s.State == "on");
        await Back(SessionEntities.Reply(node), s => s.State != "unknown" && s.State != "unavailable");
        await Back(SessionEntities.Decision(node), s => s.State == HaDecision.Awaiting
            && s.Attributes.TryGetProperty("decision_id", out var d) && d.GetString() == "q1");
        await Back(SessionEntities.Field(node, 1), s => s.State == HaDecision.Choose);

        // And it still answers, through the same ids the dashboard draws.
        await ha.CallServiceAsync("select", "select_option", new JsonObject { ["entity_id"] = SessionEntities.Field(node, 1), ["option"] = "Wait for the fix" });
        await ha.CallServiceAsync("button", "press", new JsonObject { ["entity_id"] = SessionEntities.Submit(node) });
        try { await Until(() => { lock (answered) { return answered.Count == 1; } }, "the answer never arrived"); }
        catch (Xunit.Sdk.XunitException)
        {
            string history;
            lock (log) { history = string.Join("\n", log.TakeLast(30)); }
            Assert.Fail($"the answer never arrived\n{history}");
        }
        Assert.Equal("wait", answered[0].Fields!["release"].GetString());
        var after = (await ha.ListEntityRegistryAsync()).Select(e => e.EntityId).Where(e => e.Contains(node, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(before, after);
    }

    [HomeAssistantFact]
    public async Task A_choice_on_a_card_that_stays_while_only_its_machines_liveness_sensor_goes_is_still_answered()
    {
        var id = Session();
        var node = HaContract.NodeId(id);
        Ask(id, Form("q1", Release));
        var slug = HaContract.MachineSlug("CI-SENSOR");
        var online = HaContract.MachineEntityId("binary_sensor", "online", slug);
        // The daemon's timings, shortened: a beat every two seconds, a stall after three of them.
        await using var c = new HaConnector(new HaConnectorOptions
        {
            MachineName = "CI-SENSOR", Version = "0.1.0", Dev = true,
            Tick = TimeSpan.FromMilliseconds(250), Reconcile = TimeSpan.FromSeconds(2), RetireGrace = TimeSpan.FromSeconds(2),
            Heartbeat = TimeSpan.FromSeconds(2), OnlineStall = TimeSpan.FromSeconds(6), EntityRestore = TimeSpan.FromSeconds(4),
        }, ha, sessions, () => null, log: m => { lock (log) { log.Add(m); } }, answer: (r, _) =>
        {
            lock (answered) { answered.Add(r); }
            return Task.FromResult(new AnswerResult(AnswerOutcome.Delivered));
        });
        c.Start();
        await Until(SessionEntities.Field(node, 1), s => s.State == HaDecision.Choose);
        await Until(online, s => s.State == "on");

        // A choice made, then only the liveness sensor's config goes: the machine is published again
        // around the card.
        await ha.CallServiceAsync("select", "select_option", new JsonObject { ["entity_id"] = SessionEntities.Field(node, 1), ["option"] = "Wait for the fix" });
        await ha.PublishAsync(HaContract.OnlineConfig(slug, "CI-SENSOR").Topic, "", true);
        await Until(() => { lock (log) { return log.Any(l => l.Contains("publishing the machine again", StringComparison.Ordinal)); } }, "the machine was never published again", 60);
        await Until(online, s => s.State == "on", seconds: 60);
        Assert.Equal("Wait for the fix", (await ha.GetStateAsync(SessionEntities.Field(node, 1)))!.State);

        await ha.CallServiceAsync("button", "press", new JsonObject { ["entity_id"] = SessionEntities.Submit(node) });
        try { await Until(() => { lock (answered) { return answered.Count == 1; } }, "the answer never arrived"); }
        catch (Xunit.Sdk.XunitException)
        {
            string history;
            lock (log) { history = string.Join("\n", log.TakeLast(30)); }
            Assert.Fail($"the answer never arrived\n{history}");
        }
        Assert.Equal("wait", answered[0].Fields!["release"].GetString());
    }

    sealed record Notification(string Title, string Message);

    async Task<Notification> UntilNotified(Func<Notification, bool> match, int skip, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        IReadOnlyList<Notification> seen = [];
        while (DateTime.UtcNow < deadline)
        {
            seen = await NotificationsAsync();
            if (seen.Skip(skip).FirstOrDefault(match) is { } found) { return found; }
            await Task.Delay(250);
        }
        string history;
        lock (log) { history = string.Join("\n", log.TakeLast(20)); }
        Assert.Fail($"never notified; saw {string.Join(" | ", seen.Skip(skip).Select(n => $"{n.Title}: {n.Message}"))}\n{history}");
        return null!;
    }

    // persistent_notification/get: what notify.persistent_notification has been sent, oldest first.
    static async Task<IReadOnlyList<Notification>> NotificationsAsync()
    {
        var result = await WebSocketAsync(new JsonObject { ["type"] = "persistent_notification/get" });
        return [.. result!.AsArray().Select(n => n!.AsObject())
            .OrderBy(n => n["created_at"]?.GetValue<string>(), StringComparer.Ordinal)
            .Select(n => new Notification(n["title"]?.GetValue<string>() ?? "", n["message"]?.GetValue<string>() ?? ""))];
    }

    async Task<HashSet<string>> ServicesAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(LiveHomeAssistant.Url!), "/api/services"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", LiveHomeAssistant.Token);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var all = new HashSet<string>(StringComparer.Ordinal);
        foreach (var domain in JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray())
        {
            foreach (var (service, _) in domain!["services"]!.AsObject()) { all.Add($"{domain["domain"]!.GetValue<string>()}.{service}"); }
        }
        return all;
    }

    // POST /api/events/<type>: an event fired as the companion app fires it, by this token's account.
    async Task PostEventAsync(string eventType, JsonObject data)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(LiveHomeAssistant.Url!), "/api/events/" + eventType))
        {
            Content = new StringContent(data.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", LiveHomeAssistant.Token);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    // What the reply card does with an image: POST /api/image/upload, which answers with its id.
    async Task<string> UploadImageAsync(byte[] png)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(png);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        content.Add(file, "file", "dot.png");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(LiveHomeAssistant.Url!), "/api/image/upload")) { Content = content };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", LiveHomeAssistant.Token);
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
    }

    // auth/current_user: the account behind the test's token, as Home Assistant records presses.
    static async Task<string> CurrentUserAsync() =>
        (await WebSocketAsync(new JsonObject { ["type"] = "auth/current_user" }))!["id"]!.GetValue<string>();

    // One command over Home Assistant's WebSocket API, and its result.
    static async Task<JsonNode?> WebSocketAsync(JsonObject command)
    {
        var uri = new UriBuilder(new Uri(new Uri(LiveHomeAssistant.Url!), "/api/websocket")) { Scheme = "ws" }.Uri;
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(uri, CancellationToken.None);
        await ReceiveAsync(ws);
        await SendAsync(ws, new JsonObject { ["type"] = "auth", ["access_token"] = LiveHomeAssistant.Token });
        await ReceiveAsync(ws);
        command["id"] = 1;
        await SendAsync(ws, command);
        while (true)
        {
            var message = await ReceiveAsync(ws);
            if (message["type"]?.GetValue<string>() == "result")
            {
                Assert.True(message["success"]?.GetValue<bool>() == true, $"{command["type"]} failed: {message.ToJsonString()}");
                return message["result"]?.DeepClone();
            }
        }
    }
    static Task SendAsync(ClientWebSocket ws, JsonObject message) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

    static async Task<JsonObject> ReceiveAsync(ClientWebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, CancellationToken.None);
            stream.Write(buffer, 0, r.Count);
        } while (!r.EndOfMessage);
        return JsonNode.Parse(stream.ToArray())!.AsObject();
    }
}
