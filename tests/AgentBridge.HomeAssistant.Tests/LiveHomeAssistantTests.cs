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
    readonly List<string> log = [];

    public LiveHomeAssistantTests()
    {
        ha = new HomeAssistantClient(new Uri(LiveHomeAssistant.Url ?? "http://127.0.0.1:1"), LiveHomeAssistant.Token ?? "", http);
    }

    public void Dispose() => http.Dispose();

    // Each test is its own machine, so none sees another's entities.
    HaConnector Connector(string machine, params string[] agents) => new(new HaConnectorOptions
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
    });

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
        foreach (var entity in new[] { SessionEntities.Status(node), SessionEntities.Activity(node), SessionEntities.Submit(node), SessionEntities.Reply(node), SessionEntities.ReplyPayload(node) })
        {
            Assert.NotNull(await ha.GetStateAsync(entity));
        }
        Assert.Null(await ha.GetStateAsync(SessionEntities.Stop(node)));
        var slug = HaContract.MachineSlug("CI-CARD");
        await Until(HaContract.MachineEntityId("binary_sensor", "online", slug), s => s.State == "on");
        await Until(HaContract.MachineEntityId("sensor", "sessions", slug), s => s.State == "1");
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
        Assert.Equal(HaShadow.DaemonOnly, report.Differences.Single(d => d.Topic == HaContract.DiscoveryTopic("button", node, "stop")).Verdict);
        var after = await ha.ReadTopicsAsync(filters, TimeSpan.FromSeconds(3));
        Assert.Equal(before.Select(m => (m.Topic, m.Payload)).Order(), after.Select(m => (m.Topic, m.Payload)).Order());
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
