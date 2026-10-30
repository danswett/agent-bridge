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
    }));

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
    static async Task<string> CurrentUserAsync()
    {
        var uri = new UriBuilder(new Uri(new Uri(LiveHomeAssistant.Url!), "/api/websocket")) { Scheme = "ws" }.Uri;
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(uri, CancellationToken.None);
        await ReceiveAsync(ws);
        await SendAsync(ws, new JsonObject { ["type"] = "auth", ["access_token"] = LiveHomeAssistant.Token });
        await ReceiveAsync(ws);
        await SendAsync(ws, new JsonObject { ["id"] = 1, ["type"] = "auth/current_user" });
        while (true)
        {
            var message = await ReceiveAsync(ws);
            if (message["type"]?.GetValue<string>() == "result") { return message["result"]!["id"]!.GetValue<string>(); }
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
