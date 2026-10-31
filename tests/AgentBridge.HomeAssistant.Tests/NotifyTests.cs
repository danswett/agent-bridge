using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Phone notifications: a question's answer buttons and the hooks' pushes, as the daemon's own
// functions produced them (Fixtures/ha-notify.json), against an in-process Home Assistant, and
// what a tap delivers to the host.
public sealed class NotifyTests : IDisposable
{
    const string Machine = "DSWETT-DEV-VM1";
    const string A = "6fcbab0c-1d2e-4f50-9a61-7b8c9d0e1f20";
    const string B = "7a000000-0000-4000-8000-0000000000bb";
    const string Phone = "notify.mobile_app_phone";
    const string Tablet = "notify.mobile_app_tablet";
    static readonly string Node = HaContract.NodeId(A);
    static readonly string Tag = "bridge_decision_" + Node;

    static readonly Lazy<JsonObject> Loaded = new(() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ha-notify.json")))!);
    static JsonObject Fixture => Loaded.Value;

    readonly string folder = Path.Combine(Path.GetTempPath(), "abhn-" + Guid.NewGuid().ToString("N"));
    readonly FakeHomeAssistant ha = new();
    readonly FakeSessions sessions = new();
    readonly FakeTimeProvider time = new(FakeSessions.T0);
    readonly List<string> log = [];
    readonly List<AnswerRequest> answered = [];
    Func<AnswerRequest, AnswerResult> outcome = _ => new AnswerResult(AnswerOutcome.Delivered);
    string? daemon;

    public void Dispose()
    {
        if (Directory.Exists(folder)) { Directory.Delete(folder, true); }
    }

    static readonly HaNotifySettings Phones = new() { AnswerButtonServices = [Phone, Tablet] };

    HaConnector Connector(HaNotifySettings? notify = null, bool answers = true, string[]? agents = null) => new(new HaConnectorOptions
    {
        MachineName = Machine,
        Version = "0.1.0",
        StateFile = Path.Combine(folder, "ha-connector.json"),
        AgentUserIds = agents ?? [],
        FieldPinDelay = TimeSpan.Zero,
        SelectDelay = TimeSpan.Zero,
        Notify = notify ?? Phones,
    }, ha, sessions, () => daemon, log: m => { lock (log) { log.Add(m); } }, time: time, answer: answers ? (r, _) =>
    {
        answered.Add(r);
        return Task.FromResult(outcome(r));
    } : null);

    async Task Pass(HaConnector c, double seconds = 1)
    {
        time.Advance(TimeSpan.FromSeconds(seconds));
        await c.TickAsync();
    }

    static Question Form(string id, params Field[] fields) => new()
    {
        Id = id, Kind = QuestionKind.Form, Message = "Cut the release?", AskedAt = FakeSessions.T0, Fields = fields,
    };

    static readonly Field Release = new()
    {
        Name = "release", Title = "Release", Type = FieldType.Choice, Required = true, AllowOther = true,
        Options = [new Option("cut_now", "Cut the release now"), new Option("wait", "Wait for the fix")],
    };

    static readonly Field Fixed = Release with { AllowOther = false };
    static readonly Field Push = new() { Name = "push", Title = "Push now", Type = FieldType.Boolean };
    static readonly Field Count = new() { Name = "count", Title = "How many", Type = FieldType.Number };
    static readonly Field Pick = new()
    {
        Name = "pick", Title = "Pick", Type = FieldType.Multi,
        Options = [new Option("a", "Alpha"), new Option("b", "Beta")],
    };

    void Ask(Question? q, AgentKind agent = AgentKind.Copilot, string id = A, string? response = "Working on it", SessionStatus? status = null, int turn = 1) =>
        sessions.Set(FakeSessions.Copilot(id, title: "Fix the relay", status: status ?? (q is null ? SessionStatus.Working : SessionStatus.Waiting)) with
        {
            Agent = agent,
            Cwd = @"C:\work\scratch",
            Question = q,
            Activity = new Activity { Response = response, TurnCount = turn, UserMessage = "go" },
        });

    List<(string Service, JsonObject Data)> Sent(string? service = null) => [.. ha.NotifiedNow().Where(n => service is null || n.Service == service)];
    List<JsonObject> Buttons(string service = Phone) => [.. Sent(service).Where(n => n.Data["message"]?.GetValue<string>() != HaNotify.ClearMessage).Select(n => n.Data)];
    int Clears(string service = Phone) => Sent(service).Count(n => n.Data["message"]?.GetValue<string>() == HaNotify.ClearMessage);
    List<JsonObject> Pushes(string service = "notify.notify") => [.. Sent(service).Select(n => n.Data)];

    static string ActionOf(JsonObject buttons, int i) => buttons["data"]!["actions"]![i]!["action"]!.GetValue<string>();

    // A tap, as Home Assistant fires it: each a new event unless one is given.
    void Tap(HaConnector c, string action, string? reply = null, string decision = "q1", string session = A, string? user = "person", string? id = null)
    {
        var data = new JsonObject
        {
            ["action"] = action,
            ["action_data"] = new JsonObject { ["bridge"] = "decision", ["session"] = session, ["decision"] = decision },
        };
        if (reply is not null) { data["reply_text"] = reply; }
        c.Tapped(new HaEvent(HaNotify.ActionEvent, JsonSerializer.SerializeToElement(data), user, id ?? Guid.NewGuid().ToString("N")));
    }

    static NotifyMarker MarkerOf(JsonNode m) => new(
        m["decisionId"]!.GetValue<string>(), m["question"]!.GetValue<string>(), m["mode"]!.GetValue<string>(),
        [.. m["choices"]!.AsArray().Select(x => x!.GetValue<string>())],
        [.. m["fields"]!.AsArray().Select(f => new NotifyField(f!["Label"]!.GetValue<string>(), [.. f["Options"]!.AsArray().Select(o => o!.GetValue<string>())], f["IsText"]!.GetValue<bool>(), f["MultiSelect"]!.GetValue<bool>()))],
        m["terminalOnly"]!.GetValue<bool>());

    static void Same(JsonNode? expected, JsonNode? actual, string because) =>
        Assert.True(JsonNode.DeepEquals(expected, actual), $"{because}\nexpected {expected?.ToJsonString()}\nactual   {actual?.ToJsonString()}");

    [Fact]
    public void Answer_buttons_are_the_daemons_for_every_kind_of_question()
    {
        foreach (var (name, node) in Fixture["buttons"]!.AsObject())
        {
            var marker = MarkerOf(node!["marker"]!);
            var options = HaNotify.Options(marker);
            Assert.Equal(node["options"]!.AsArray().Select(o => o!.GetValue<string>()), options);
            var actions = HaNotify.Actions(marker, marker.DecisionId, options, 6);
            Same(node["actions"], actions, name);
            Same(node["payload"], HaNotify.Payload(A, "Copilot: Fix the relay", marker, actions, "agent-decisions", Machine), name);
        }
        var choice = MarkerOf(Fixture["buttons"]!["choice"]!["marker"]!);
        Same(Fixture["plain"], HaNotify.Payload(A, "Claude: scratch", choice, [], "my-dash", "", icon: "", interruptionLevel: ""), "plain");
        Assert.Equal(Fixture["tag"]!.GetValue<string>(), HaNotify.Tag(A));
        foreach (var (text, title) in Fixture["titles"]!.AsObject()) { Assert.Equal(title!.GetValue<string>(), HaNotify.Title(text)); }
        // Withdrawn by tag, at every phone it went to; the entry that is no service is skipped.
        Same(Fixture["cleared"]![0]!["data"], HaNotify.Clear(A), "cleared");
        var services = Fixture["services"]!.AsArray().Select(s => s!.GetValue<string>()).Where(s => HaNotify.Split(s) is not null);
        Assert.Equal(Fixture["sent"]!.AsArray().Select(s => s!["service"]!.GetValue<string>()), services);
        Assert.Equal(Fixture["cleared"]!.AsArray().Select(s => s!["service"]!.GetValue<string>()), services);
    }

    [Fact]
    public void Taps_titles_pushes_and_defaults_are_the_daemons()
    {
        foreach (var (name, node) in Fixture["choices"]!.AsObject())
        {
            var choice = HaNotify.Choice(JsonSerializer.SerializeToElement(node!["event"]));
            if (node["choice"] is null) { Assert.Null(choice); continue; }
            var c = node["choice"]!;
            Assert.Equal(new TapChoice(c["SessionId"]!.GetValue<string>(), c["DecisionId"]!.GetValue<string>(), c["Index"]!.GetValue<int>(), c["ReplyText"]!.GetValue<string>()), choice);
        }
        Assert.Equal(Fixture["longTitle"]!.GetValue<string>(), HaNotify.NotificationTitle(new string('x', 200)));

        var pushes = Fixture["pushes"]!;
        Same(pushes["notify"]![0]!["data"], HaNotify.Notification("notify.notify", "Copilot: Copilot: Fix the relay", "Hello", "agents", "agent-decisions"), "notify");
        Same(pushes["ticker"]![0]!["data"], HaNotify.Notification("ticker.notify", "Approval needed: Codex: api", "rm -rf build", "agents", "agent-decisions"), "ticker");
        var (title, message) = HaNotify.Response("Claude: scratch", "  Done.  ", copilot: false)!.Value;
        Same(pushes["responseShort"]![0]!["data"], new JsonObject { ["title"] = title, ["message"] = message }, "short");
        (title, message) = HaNotify.Response("Copilot: Fix the relay", Fixture["longResponse"]!.GetValue<string>(), copilot: true)!.Value;
        Same(pushes["responseLong"]![0]!["data"], new JsonObject { ["title"] = title, ["message"] = message }, "long");
        Assert.Empty(pushes["responseEmpty"]!.AsArray());
        Assert.Null(HaNotify.Response("Codex: api", "   ", copilot: false));

        var d = Fixture["defaults"]!;
        var mine = HaNotifySettings.Default;
        Assert.Equal(d["notifications.enabled"]!.GetValue<bool>(), mine.Enabled);
        Assert.Equal(d["notifications.service"]!.GetValue<string>(), mine.Service);
        Assert.Equal(d["notifications.tickerCategory"]!.GetValue<string>(), mine.TickerCategory);
        Assert.Equal(d["answerButtons.enabled"]!.GetValue<bool>(), mine.AnswerButtons);
        Assert.Empty(d["answerButtons.services"]!.AsArray());
        Assert.Empty(mine.AnswerButtonServices);
        Assert.Equal(d["answerButtons.maxButtons"]!.GetValue<int>(), mine.MaxButtons);
        Assert.Equal(d["answerButtons.icon"]!.GetValue<string>(), mine.Icon);
        Assert.Equal(d["answerButtons.iconColor"]!.GetValue<string>(), mine.IconColor);
        Assert.Equal(d["answerButtons.color"]!.GetValue<string>(), mine.Color);
        Assert.Equal(d["answerButtons.interruptionLevel"]!.GetValue<string>(), mine.InterruptionLevel);
    }

    [Fact]
    public void The_hooks_pushes_are_worded_as_each_hook_words_them()
    {
        Assert.Equal(("Copilot: Copilot: Fix the relay",
            "Session: Copilot: Fix the relay\nMachine: M\n\nCut it?\n\n1. Now\n2. Later\n\nAnswer in the terminal or on the Agent Sessions dashboard."),
            HaNotify.CopilotQuestion("Copilot: Fix the relay", "M", "Cut it?", ["Now", "Later"]));
        Assert.Equal("Session: Copilot: X\nMachine: M\n\nWhat?\n\nAnswer in the terminal or in the Reply box on the Agent Sessions dashboard.",
            HaNotify.CopilotQuestion("Copilot: X", "M", "What?", []).Message);
        Assert.Equal(("Claude: scratch", "Session: Claude: scratch\nMachine: M\n\nCut it?\n\n1. Now\n2. Later\n\nAnswer in the terminal or on the dashboard."),
            HaNotify.ClaudeQuestion("Claude: scratch", "M", "Cut it?", ["Now", "Later"], terminalOnly: false));
        Assert.Equal("Session: Claude: scratch\nMachine: M\n\nWhat?\n\n\nAnswer this one in the terminal.",
            HaNotify.ClaudeQuestion("Claude: scratch", "M", "What?", [], terminalOnly: true).Message);
        Assert.Equal(("Waiting: Claude: scratch", "Session: Claude: scratch\nMachine: M\n\nClaude is waiting for you.\n\nAnswer in the terminal, or try the Reply box on the dashboard."),
            HaNotify.ClaudeWaiting("Claude: scratch", "M", null));
        Assert.Equal(("Copilot permission needed", "Copilot CLI is waiting for your input."), HaNotify.CopilotPermission(" "));
        Assert.Equal(("Approval needed: Codex: api", "rm -rf build"), HaNotify.CodexApproval("Codex: api", "rm -rf build"));
        // Capped where the hooks cap them.
        Assert.Equal(950, HaNotify.CopilotQuestion("n", "M", new string('q', 2000), []).Message.Length);
        Assert.EndsWith("...", HaNotify.CopilotQuestion("n", "M", new string('q', 2000), []).Message, StringComparison.Ordinal);
        Assert.Contains(new string('w', 597) + "...\n", HaNotify.ClaudeWaiting("n", "M", new string('w', 700)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_question_goes_to_every_named_phone_once_as_the_daemon_sends_it()
    {
        Ask(Form("q1", Release));
        await using var c = Connector(Phones with { AnswerButtonServices = [Phone, "not-a-service", Tablet] });
        await Pass(c);
        var sent = Assert.Single(Buttons());
        Assert.Single(Buttons(Tablet));
        Assert.Equal("Copilot: Fix the relay", sent["title"]!.GetValue<string>());
        Assert.Equal("Cut the release?", sent["message"]!.GetValue<string>());
        Assert.Equal(Tag, sent["data"]!["tag"]!.GetValue<string>());
        Assert.Equal("/agent-decisions/decision", sent["data"]!["url"]!.GetValue<string>());
        Assert.Equal($"{Machine} - waiting on you", sent["data"]!["subtitle"]!.GetValue<string>());
        Same(new JsonObject { ["bridge"] = "decision", ["session"] = A, ["decision"] = "q1" }, sent["data"]!["action_data"], "action_data");
        Assert.Equal(["Cut the release now", "Wait for the fix", "Type an answer"],
            sent["data"]!["actions"]!.AsArray().Select(a => a!["title"]!.GetValue<string>()));

        for (var i = 0; i < 3; i++) { await Pass(c, 20); }
        Assert.Single(Buttons());
    }

    [Fact]
    public async Task A_phone_that_fails_does_not_stop_the_others_and_a_question_none_took_is_tried_again()
    {
        ha.NotifyFailsFor = s => s == Tablet;
        Ask(Form("q1", Release));
        await using var c = Connector();
        await Pass(c);
        Assert.Single(Buttons());
        Assert.Contains(log, l => l.Contains($"answer buttons via {Tablet} failed", StringComparison.Ordinal));
        await Pass(c, 20);
        Assert.Single(Buttons());

        ha.NotifyFailsFor = _ => true;
        Ask(Form("q2", Release));
        await Pass(c);
        await Pass(c, 5);
        Assert.Single(Buttons());
        ha.NotifyFailsFor = null;
        await Pass(c, 5);
        Assert.Single(Buttons());
        await Pass(c, 10);
        Assert.Equal(2, Buttons().Count);
        Assert.Single(Buttons(Tablet));
    }

    [Fact]
    public async Task Tapping_an_option_answers_as_the_card_would_and_withdraws_the_notification()
    {
        Ask(Form("q1", Release));
        await using var c = Connector(agents: ["agent-user"]);
        await Pass(c);
        Tap(c, ActionOf(Buttons()[0], 1));
        await Pass(c);
        var request = Assert.Single(answered);
        Assert.Equal("q1", request.Question);
        Assert.Equal("wait", request.Fields!["release"].GetString());
        Assert.Equal("Answer sent", ha.Retained[HaContract.Topics(Node).ActivityState]);
        Assert.Equal(1, Clears());
        Assert.Equal(1, Clears(Tablet));

        // The same tap, again, and a second button after the answer, deliver nothing more.
        Tap(c, ActionOf(Buttons()[0], 1));
        Tap(c, ActionOf(Buttons()[0], 0), user: "agent-user");
        await Pass(c);
        Assert.Single(answered);
    }

    [Fact]
    public async Task A_tap_made_by_an_agent_is_the_agents_answer()
    {
        Ask(Form("q1", Push));
        await using var c = Connector(agents: ["agent-user"]);
        await Pass(c);
        // Yes or No, and no words: the host has nothing to deliver typed words to a checkbox with.
        Assert.Equal(["Yes", "No"], Buttons()[0]["data"]!["actions"]!.AsArray().Select(a => a!["title"]!.GetValue<string>()));
        Tap(c, ActionOf(Buttons()[0], 1), user: "agent-user");
        await Pass(c);
        var request = Assert.Single(answered);
        Assert.False(request.Fields!["push"].GetBoolean());
        // The turn the answer starts is the agent's, as after its press on the card.
        Ask(null, turn: 2);
        await Pass(c);
        Assert.Equal("agent", JsonDocument.Parse(ha.Retained[HaContract.Topics(Node).ActivityAttributes]).RootElement.GetProperty("driver").GetString());
    }

    [Fact]
    public async Task Typed_words_answer_through_other_a_text_field_or_as_the_text_of_a_freeform_question()
    {
        Ask(Form("q1", Release));
        await using var c = Connector();
        await Pass(c);
        Tap(c, ActionOf(Buttons()[0], 2), reply: "  after the hotfix  ");
        await Pass(c);
        Assert.Equal("after the hotfix", Assert.Single(answered).Fields!["release"].GetProperty("other").GetString());

        Ask(Form("q2", Count));
        await Pass(c);
        Assert.Equal(["Type an answer"], Buttons()[^1]["data"]!["actions"]!.AsArray().Select(a => a!["title"]!.GetValue<string>()));
        Tap(c, "BRIDGE_q2_text", reply: "three", decision: "q2");
        await Pass(c);
        Assert.Single(answered);
        Tap(c, "BRIDGE_q2_text", reply: "3", decision: "q2");
        await Pass(c);
        Assert.Equal(3, answered[^1].Fields!["count"].GetInt32());

        Ask(new Question { Id = "q3", Kind = QuestionKind.Form, Message = "What should the notes say?", AskedAt = FakeSessions.T0, Fields = [] });
        await Pass(c);
        Tap(c, "BRIDGE_q3_text", reply: "Fixes the relay", decision: "q3");
        await Pass(c);
        Assert.Equal("Fixes the relay", answered[^1].Text);
        Assert.Equal(3, answered.Count);
    }

    [Fact]
    public async Task Words_are_not_offered_where_the_host_could_not_deliver_them_and_a_choose_any_tap_is_one_choice()
    {
        Ask(Form("q1", Fixed));
        await using var c = Connector();
        await Pass(c);
        Assert.DoesNotContain(Buttons()[0]["data"]!["actions"]!.AsArray(), a => a!["title"]!.GetValue<string>() == "Type an answer");
        Tap(c, "BRIDGE_q1_text", reply: "something else");
        await Pass(c);
        Assert.Empty(answered);

        Ask(Form("q2", Pick));
        await Pass(c);
        Tap(c, ActionOf(Buttons()[^1], 1), decision: "q2");
        await Pass(c);
        Assert.Equal(["b"], Assert.Single(answered).Fields!["pick"].EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task A_tap_for_an_earlier_question_another_session_or_an_unknown_button_answers_nothing()
    {
        Ask(Form("q1", Release));
        await using var c = Connector();
        await Pass(c);
        var first = Buttons()[0];
        Ask(Form("q2", Release));
        await Pass(c);
        Tap(c, ActionOf(first, 0), decision: "q1");
        Tap(c, "BRIDGE_q2_0", decision: "q2", session: B);
        Tap(c, "BRIDGE_q2_9", decision: "q2");
        Tap(c, "SOMETHING", decision: "q2");
        await Pass(c);
        Assert.Empty(answered);
        Assert.Contains(log, l => l.Contains("is for an earlier question", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_notification_is_withdrawn_when_answered_elsewhere_replaced_or_the_session_ends()
    {
        Ask(Form("q1", Release));
        await using var c = Connector();
        await Pass(c);
        Ask(null);
        await Pass(c);
        Assert.Equal(1, Clears());

        // Answered at the terminal, then asked again: a new question is notified again.
        Ask(Form("q2", Release));
        await Pass(c);
        Assert.Equal(2, Buttons().Count);
        Ask(Form("q3", Release));
        await Pass(c);
        Assert.Equal(2, Clears());
        Assert.Equal(3, Buttons().Count);

        sessions.End(A);
        await Pass(c);
        Assert.Equal(3, Clears());
        // Nothing to withdraw for a question never notified.
        await Pass(c, 20);
        Assert.Equal(3, Clears());
    }

    [Fact]
    public async Task An_approval_gets_no_buttons_a_terminal_only_question_is_a_notice_and_without_phones_nothing_is_sent()
    {
        Ask(new Question { Id = "q1", Kind = QuestionKind.Approval, Message = "rm -rf build", AskedAt = FakeSessions.T0 }, AgentKind.Codex);
        await using var c = Connector();
        await Pass(c);
        Assert.Empty(Buttons());

        Ask(new Question { Id = "q2", Kind = QuestionKind.Form, Message = "Answer it here", AskedAt = FakeSessions.T0, AnswerAtMachine = true, Fields = [Release] });
        await Pass(c);
        var notice = Assert.Single(Buttons());
        Assert.Empty(notice["data"]!["actions"]!.AsArray());

        sessions.End(A);
        await using var none = Connector(HaNotifySettings.Default);
        Ask(Form("q3", Release), id: B);
        await Pass(none);
        Assert.Single(Buttons());
        await using var off = Connector(Phones with { AnswerButtons = false });
        await Pass(off);
        Assert.Single(Buttons());
    }

    [Fact]
    public async Task Taking_the_machine_back_from_agent_ha_bridge_sends_a_waiting_question_again_once()
    {
        Ask(Form("q1", Release));
        await using var c = Connector();
        await Pass(c);
        daemon = "agent-ha-bridge's daemon (pid 4242)";
        await Pass(c);
        // A tap agent-ha-bridge's own listener took while it had the machine is its to answer.
        Tap(c, ActionOf(Buttons()[0], 0));
        await Pass(c);
        Assert.Single(Buttons());
        daemon = null;
        await Pass(c);
        await Pass(c, 20);
        Assert.Equal(2, Buttons().Count);
        Assert.Empty(answered);
    }

    [Fact]
    public async Task Without_answer_delivery_no_buttons_go_out()
    {
        Ask(Form("q1", Release));
        await using var c = Connector(answers: false);
        await Pass(c);
        Assert.Empty(Buttons());
    }

    [Fact]
    public async Task A_question_and_a_finished_turn_are_pushed_once_each_as_the_hooks_push_them()
    {
        var notify = new HaNotifySettings { Enabled = true };
        Ask(null, response: null);
        await using var c = Connector(notify);
        await Pass(c);
        Ask(Form("q1", Release), response: null);
        await Pass(c);
        var asked = Assert.Single(Pushes());
        Assert.Equal("Copilot: Copilot: Fix the relay", asked["title"]!.GetValue<string>());
        Assert.Equal($"Session: Copilot: Fix the relay\nMachine: {Machine}\n\nCut the release?\n\n1. Cut the release now\n2. Wait for the fix\n\nAnswer in the terminal or on the Agent Sessions dashboard.",
            asked["message"]!.GetValue<string>());
        await Pass(c, 20);
        Assert.Single(Pushes());

        // Answered at the machine: idle for a moment on the way back to work is not a finished turn.
        Ask(null, response: "Cutting it.", status: SessionStatus.Idle);
        await Pass(c);
        Ask(null, response: "Cutting it.", status: SessionStatus.Working);
        await Pass(c, 5);
        Assert.Single(Pushes());

        Ask(null, response: "Done: the release is cut.", status: SessionStatus.Idle);
        await Pass(c);
        Assert.Single(Pushes());
        await Pass(c, 5);
        var done = Pushes()[^1];
        Assert.Equal("Copilot response: Copilot: Fix the relay", done["title"]!.GetValue<string>());
        Assert.Equal("Done: the release is cut.", done["message"]!.GetValue<string>());
        await Pass(c, 20);
        Assert.Equal(2, Pushes().Count);

        // A background agent's message while the session rests is not a finished turn.
        sessions.Set(sessions.Get(A)! with { Activity = new Activity { Response = "An agent reports in.", TurnCount = 1, UserMessage = "go", Background = Background.Of(1, 0) } });
        await Pass(c);
        await Pass(c, 5);
        Assert.Equal(2, Pushes().Count);

        // The next turn ending in the same words is a finished turn of its own.
        Ask(null, response: "Done: the release is cut.", status: SessionStatus.Working, turn: 2);
        await Pass(c);
        Ask(null, response: "Done: the release is cut.", status: SessionStatus.Idle, turn: 2);
        await Pass(c);
        await Pass(c, 5);
        Assert.Equal(3, Pushes().Count);
        Assert.Equal("Done: the release is cut.", Pushes()[^1]["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Claude_and_codex_are_pushed_as_their_hooks_push_them_and_a_ticker_gets_its_own_fields()
    {
        var notify = new HaNotifySettings { Enabled = true, Service = "ticker.notify", TickerCategory = "agents" };
        Ask(null, AgentKind.Claude, response: null);
        Ask(null, AgentKind.Codex, id: B, response: null);
        await using var c = Connector(notify);
        await Pass(c);

        Ask(null, AgentKind.Claude, response: null, status: SessionStatus.Waiting);
        await Pass(c);
        // Still blocked, whatever else changes: told once.
        Ask(null, AgentKind.Claude, response: "Still thinking", status: SessionStatus.Waiting);
        await Pass(c);
        var waiting = Assert.Single(Pushes("ticker.notify"));
        Assert.Equal("Waiting: Claude: scratch", waiting["title"]!.GetValue<string>());
        Assert.Equal("agents", waiting["category"]!.GetValue<string>());
        Assert.Equal("/agent-decisions/decision", waiting["navigate_to"]!.GetValue<string>());

        Ask(new Question { Id = "x1", Kind = QuestionKind.Approval, Message = "rm -rf build", AskedAt = FakeSessions.T0 }, AgentKind.Codex, id: B, response: null);
        await Pass(c);
        var approval = Pushes("ticker.notify")[^1];
        Assert.Equal("Approval needed: Codex: scratch", approval["title"]!.GetValue<string>());
        Assert.Equal("rm -rf build", approval["message"]!.GetValue<string>());

        Ask(null, AgentKind.Claude, response: "All done.", status: SessionStatus.Idle);
        await Pass(c);
        await Pass(c, 5);
        Assert.Equal("Response: Claude: scratch", Pushes("ticker.notify")[^1]["title"]!.GetValue<string>());
        Assert.Equal(3, Pushes("ticker.notify").Count);
    }

    [Fact]
    public async Task What_sessions_already_show_when_the_connector_starts_is_not_pushed_again_and_off_sends_nothing()
    {
        Ask(Form("q1", Release), response: "Earlier reply", status: SessionStatus.Waiting);
        Ask(null, AgentKind.Claude, id: B, response: "Earlier reply", status: SessionStatus.Idle);
        await using (var c = Connector(new HaNotifySettings { Enabled = true }))
        {
            await Pass(c);
            await Pass(c, 20);
            Assert.Empty(Pushes());
            // A session that appears later is new news.
            Ask(Form("q9", Release), id: "8b000000-0000-4000-8000-0000000000cc");
            await Pass(c);
            Assert.Single(Pushes());
            // But not a reply it brought with it: that turn was over before it was seen here.
            Ask(null, id: "9c000000-0000-4000-8000-0000000000dd", response: "Resumed with this", status: SessionStatus.Idle);
            await Pass(c);
            await Pass(c, 5);
            Assert.Single(Pushes());
        }

        await using var off = Connector(HaNotifySettings.Default);
        Ask(Form("q2", Release));
        await Pass(off);
        Ask(null, response: "New reply", status: SessionStatus.Idle);
        await Pass(off);
        await Pass(off, 5);
        Assert.Single(Pushes());
    }

    [Fact]
    public async Task A_session_whose_first_publication_failed_is_still_not_news_when_it_is_published()
    {
        Ask(Form("q1", Release), response: "Earlier reply");
        ha.PublishFailsFor = (topic, _) => topic == HaContract.Topics(Node).ActivityState;
        await using var c = Connector(new HaNotifySettings { Enabled = true });
        await Pass(c);
        ha.PublishFailsFor = null;
        await Pass(c);
        await Pass(c, 5);
        Assert.Empty(Pushes());
    }

    [Fact]
    public async Task Two_sessions_asking_with_the_same_question_id_are_each_notified()
    {
        Ask(Form("q1", Release));
        await using var c = Connector();
        await Pass(c);
        Ask(Form("q1", Release), id: B);
        await Pass(c);
        Assert.Equal([A, B], Buttons().Select(b => b["data"]!["action_data"]!["session"]!.GetValue<string>()).Order());
    }

    [Fact]
    public async Task An_approval_shown_for_the_terminal_is_still_an_approval_and_goes_to_no_phone()
    {
        Ask(new Question { Id = "q1", Kind = QuestionKind.Approval, Message = "Allow the edit?", AskedAt = FakeSessions.T0, AnswerAtMachine = true }, AgentKind.Claude);
        await using var c = Connector();
        await Pass(c);
        await Pass(c, 20);
        Assert.Empty(Buttons());
    }

    [Fact]
    public async Task A_withdrawal_that_failed_is_tried_again_and_one_left_by_a_stopped_host_is_made_on_start()
    {
        Ask(Form("q1", Release));
        await using (var c = Connector())
        {
            await Pass(c);
            ha.NotifyFailsFor = s => s == Phone;
            Ask(null);
            await Pass(c);
            Assert.Equal((0, 1), (Clears(), Clears(Tablet)));
            await Pass(c, 5);
            Assert.Equal(0, Clears());
            ha.NotifyFailsFor = null;
            await Pass(c, 20);
            Assert.Equal((1, 1), (Clears(), Clears(Tablet)));

            // Asked again, and the host stops before the answer.
            Ask(Form("q2", Release));
            await Pass(c);
            Assert.Equal(2, Buttons().Count);
        }
        Ask(null);
        await using var again = Connector();
        await Pass(again);
        Assert.Equal((2, 2), (Clears(), Clears(Tablet)));
        await Pass(again, 20);
        Assert.Equal((2, 2), (Clears(), Clears(Tablet)));
    }

    [Fact]
    public async Task Only_an_action_the_notification_offers_answers_it()
    {
        Ask(Form("q1", Release));
        await using var c = Connector(Phones with { MaxButtons = 1 });
        await Pass(c);
        Assert.Equal(["BRIDGE_q1_0", "BRIDGE_q1_text"], Buttons()[0]["data"]!["actions"]!.AsArray().Select(a => a!["action"]!.GetValue<string>()));
        Tap(c, "SOMETHING_ELSE_0");
        Tap(c, "BRIDGE_q1_1");
        Tap(c, "SOMETHING_ELSE", reply: "typed elsewhere");
        await Pass(c);
        Assert.Empty(answered);
        Tap(c, "BRIDGE_q1_0");
        await Pass(c);
        Assert.Equal("cut_now", Assert.Single(answered).Fields!["release"].GetString());
    }

    [Fact]
    public async Task A_phone_that_missed_both_the_withdrawal_and_the_next_question_is_withdrawn_alone()
    {
        Ask(Form("q1", Release));
        await using var c = Connector();
        await Pass(c);
        ha.NotifyFailsFor = s => s == Phone;
        Ask(Form("q2", Release));
        await Pass(c);
        Assert.Equal((0, 1), (Clears(), Clears(Tablet)));
        Assert.Equal((1, 2), (Buttons().Count, Buttons(Tablet).Count));
        ha.NotifyFailsFor = null;
        await Pass(c, 20);
        // The phone still showing q1 is cleared; the tablet keeps q2.
        Assert.Equal((1, 1), (Clears(), Clears(Tablet)));
        await Pass(c, 20);
        Assert.Equal((1, 1), (Clears(), Clears(Tablet)));
    }

    [Fact]
    public async Task A_phone_that_times_out_does_not_stop_the_others_or_the_pass()
    {
        ha.NotifyThrows = s => s == Phone ? new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout") : null;
        Ask(Form("q1", Release));
        await using var c = Connector(Phones with { Enabled = true, Service = Phone });
        await Pass(c);
        Assert.Single(Buttons(Tablet));
        // The push to the phone timed out too, and the session's card was still published.
        Assert.Equal(HaDecision.Awaiting, (await ha.GetStateAsync(SessionEntities.Decision(Node)))!.State);
        ha.NotifyThrows = null;
        await Pass(c, 20);
        Assert.Single(Buttons(Tablet));
    }

    [Fact]
    public async Task A_refused_tap_read_again_after_a_failure_to_say_so_is_not_delivered_again()
    {
        outcome = _ => answered.Count == 1 ? new AnswerResult(AnswerOutcome.Refused, "busy") : new AnswerResult(AnswerOutcome.Delivered);
        Ask(Form("q1", Release));
        await using var c = Connector();
        await Pass(c);
        ha.PublishFailsFor = (topic, payload) => topic == HaContract.Topics(Node).ActivityState && payload == "Answer NOT sent";
        Tap(c, "BRIDGE_q1_0");
        await Pass(c);
        Assert.Single(answered);
        ha.PublishFailsFor = null;
        await Pass(c);
        await Pass(c);
        Assert.Single(answered);
    }

    [Fact]
    public async Task A_reply_that_changes_while_a_finished_turn_waits_is_pushed_as_it_ends()
    {
        Ask(null, response: null);
        await using var c = Connector(new HaNotifySettings { Enabled = true });
        await Pass(c);
        Ask(null, response: "Almost.", status: SessionStatus.Idle);
        await Pass(c);
        Ask(null, response: "Done.", status: SessionStatus.Idle);
        await Pass(c);
        await Pass(c, 5);
        Assert.Equal("Done.", Assert.Single(Pushes())["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_reply_that_changes_between_reading_the_session_and_pushing_it_is_still_pushed()
    {
        Ask(null, response: null);
        await using var c = Connector(new HaNotifySettings { Enabled = true });
        await Pass(c);
        Ask(null, response: "Almost.", status: SessionStatus.Idle);
        await Pass(c);
        // The session moves on while this pass is publishing it: the finished step reads the newer reply.
        var once = true;
        ha.PublishFailsFor = (topic, _) =>
        {
            if (once && topic == HaContract.Topics(Node).ActivityState)
            {
                once = false;
                sessions.Set(sessions.Get(A)! with { Activity = sessions.Get(A)!.Activity with { Response = "Done." } });
            }
            return false;
        };
        sessions.Set(sessions.Get(A)! with { Activity = sessions.Get(A)!.Activity with { Reasoning = "checking" } });
        await Pass(c, 5);
        ha.PublishFailsFor = null;
        await Pass(c);
        await Pass(c, 5);
        Assert.Equal("Done.", Assert.Single(Pushes())["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task Other_changes_while_a_finished_turn_waits_do_not_hold_it_back()
    {
        Ask(null, response: null);
        await using var c = Connector(new HaNotifySettings { Enabled = true });
        await Pass(c);
        Ask(null, response: "Done.", status: SessionStatus.Idle);
        await Pass(c);
        for (var i = 0; i < 3; i++)
        {
            sessions.Set(sessions.Get(A)! with { Activity = sessions.Get(A)!.Activity with { Reasoning = $"thought {i}" } });
            await Pass(c, 1.5);
        }
        Assert.Equal("Done.", Assert.Single(Pushes())["message"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_phone_that_took_the_notification_before_the_pass_stopped_is_still_withdrawn()
    {
        Ask(Form("q1", Release));
        // agent-ha-bridge starts just after the phone takes the notification, before the tablet does.
        ha.NotifyThrows = s => { if (s == Phone) { daemon = "agent-ha-bridge's daemon (pid 4242)"; } return null; };
        await using var c = Connector();
        await Pass(c);
        ha.NotifyThrows = null;
        Assert.Single(Buttons());
        Assert.Empty(Buttons(Tablet));
        Ask(null);
        daemon = null;
        await Pass(c);
        Assert.Equal(1, Clears());
    }

    [Fact]
    public async Task Words_are_not_offered_for_a_list_with_no_options()
    {
        Ask(Form("q1", new Field { Name = "tags", Title = "Tags", Type = FieldType.Multi }));
        await using var c = Connector();
        await Pass(c);
        Assert.Empty(Buttons()[0]["data"]!["actions"]!.AsArray());
        Tap(c, "BRIDGE_q1_text", reply: "a, b");
        await Pass(c);
        Assert.Empty(answered);
    }

    [Fact]
    public async Task A_refused_tap_read_again_after_another_was_refused_is_not_delivered()
    {
        outcome = _ => answered.Count <= 2 ? new AnswerResult(AnswerOutcome.Refused, "busy") : new AnswerResult(AnswerOutcome.Delivered);
        Ask(Form("q1", Release));
        await using var c = Connector();
        await Pass(c);
        var failOnce = true;
        ha.PublishFailsFor = (topic, payload) =>
        {
            if (failOnce && topic == HaContract.Topics(Node).ActivityState && payload == "Answer NOT sent") { failOnce = false; return true; }
            return false;
        };
        Tap(c, "BRIDGE_q1_0");
        Tap(c, "BRIDGE_q1_1");
        await Pass(c);
        await Pass(c);
        await Pass(c);
        Assert.Equal(["cut_now", "wait"], answered.Select(a => a.Fields!["release"].GetString()));
    }

    [Fact]
    public async Task A_tapped_button_answers_its_own_option_when_two_share_a_label()
    {
        var twins = new Field
        {
            Name = "pick", Title = "Pick", Type = FieldType.Multi,
            Options = [new Option("a", "Same"), new Option("b", "Same"), new Option("c", "Other one")],
        };
        Ask(Form("q1", twins));
        await using var c = Connector();
        await Pass(c);
        Assert.Equal(["Same", "Same", "Other one"], Buttons()[0]["data"]!["actions"]!.AsArray().Select(a => a!["title"]!.GetValue<string>()).Take(3));
        Tap(c, "BRIDGE_q1_1");
        await Pass(c);
        Assert.Equal(["b"], Assert.Single(answered).Fields!["pick"].EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task A_button_refused_before_a_resume_and_tapped_again_after_it_is_delivered()
    {
        // The host remembers what came of each request for a while, refusals included
        // (OutcomeMemory): a new tap must not be answered from what an earlier one was told.
        var remembered = new Dictionary<string, AnswerResult>(StringComparer.Ordinal);
        var refuse = true;
        outcome = r => remembered.TryGetValue(r.Request, out var known) ? known
            : remembered[r.Request] = refuse ? new AnswerResult(AnswerOutcome.Refused, "agent-ha-bridge is running") : new AnswerResult(AnswerOutcome.Delivered);
        Ask(Form("q1", Release));
        await using var c = Connector();
        await Pass(c);
        Tap(c, "BRIDGE_q1_0");
        await Pass(c);
        Assert.Equal("Answer NOT sent", ha.Retained[HaContract.Topics(Node).ActivityState]);

        daemon = "agent-ha-bridge's daemon (pid 4242)";
        await Pass(c);
        daemon = null;
        refuse = false;
        await Pass(c);
        Assert.Equal(2, Buttons().Count);
        Tap(c, "BRIDGE_q1_0");
        await Pass(c);
        Assert.Equal(2, answered.Count);
        Assert.NotEqual(answered[0].Request, answered[1].Request);
        Assert.Equal("Answer sent", ha.Retained[HaContract.Topics(Node).ActivityState]);
    }

    [Fact]
    public async Task The_same_tap_event_delivered_twice_is_answered_once()
    {
        Ask(Form("q1", Release));
        await using var c = Connector();
        await Pass(c);
        Tap(c, "BRIDGE_q1_0", id: "event-1");
        Tap(c, "BRIDGE_q1_0", id: "event-1");
        await Pass(c);
        Assert.Single(answered);

        // Refused, then another tap refused, then the first event again: still only the two.
        Ask(Form("q2", Release));
        await Pass(c);
        outcome = _ => new AnswerResult(AnswerOutcome.Refused, "busy");
        Tap(c, "BRIDGE_q2_0", decision: "q2", id: "event-2");
        await Pass(c);
        Tap(c, "BRIDGE_q2_1", decision: "q2", id: "event-3");
        await Pass(c);
        Tap(c, "BRIDGE_q2_0", decision: "q2", id: "event-2");
        await Pass(c);
        Assert.Equal(3, answered.Count);
    }

    [Fact]
    public async Task A_tap_the_host_refused_can_be_made_again()
    {
        var refusals = 1;
        outcome = _ => refusals-- > 0 ? new AnswerResult(AnswerOutcome.Refused, "busy") : new AnswerResult(AnswerOutcome.Delivered);
        Ask(Form("q1", Release));
        await using var c = Connector();
        await Pass(c);
        Tap(c, "BRIDGE_q1_0");
        await Pass(c);
        Assert.Single(answered);
        Assert.Equal(0, Clears());
        Tap(c, "BRIDGE_q1_0");
        await Pass(c);
        Assert.Equal(2, answered.Count);
        Assert.Equal(1, Clears());
    }

    [Fact]
    public async Task The_tap_listener_takes_taps_while_the_connector_runs_and_lets_go_while_agent_ha_bridge_does()
    {
        string? daemon = null;
        Ask(Form("q1", Release));
        await using var c = new HaConnector(new HaConnectorOptions
        {
            MachineName = Machine, Version = "0.1.0", Tick = TimeSpan.FromMilliseconds(20), StateFile = Path.Combine(folder, "ha-connector.json"),
            FieldPinDelay = TimeSpan.Zero, SelectDelay = TimeSpan.Zero, Notify = Phones,
        }, ha, sessions, () => daemon, answer: (r, _) => { lock (answered) { answered.Add(r); } return Task.FromResult(new AnswerResult(AnswerOutcome.Delivered)); });
        c.Start();
        await Until(() => Volatile.Read(ref ha.EventSubscriptions) == 1 && Buttons().Count == 1);
        ha.Fire(HaNotify.ActionEvent, new JsonObject
        {
            ["action"] = ActionOf(Buttons()[0], 0),
            ["action_data"] = new JsonObject { ["bridge"] = "decision", ["session"] = A, ["decision"] = "q1" },
        });
        await Until(() => { lock (answered) { return answered.Count == 1; } });
        Assert.Equal("cut_now", answered[0].Fields!["release"].GetString());

        daemon = "agent-ha-bridge's daemon (pid 4242)";
        await Until(() => !c.Started);
        await Task.Delay(200);
        var held = Volatile.Read(ref ha.EventSubscriptions);
        daemon = null;
        await Until(() => Volatile.Read(ref ha.EventSubscriptions) == held + 1);
    }

    static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(20);
        }
    }
}
