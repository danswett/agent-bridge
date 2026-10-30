using System.Text.Json;
using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Answering a session's question from its card, against an in-process Home Assistant: the card
// armed as the daemon arms it, what a person or an agent does on it, and what reaches the host's
// answer delivery.
public sealed class AnswerTests : IDisposable
{
    const string Machine = "DSWETT-DEV-VM1";
    const string A = "6fcbab0c-1d2e-4f50-9a61-7b8c9d0e1f20";
    static readonly string Node = HaContract.NodeId(A);

    readonly string folder = Path.Combine(Path.GetTempPath(), "abha-" + Guid.NewGuid().ToString("N"));
    readonly FakeHomeAssistant ha = new();
    readonly FakeSessions sessions = new();
    readonly FakeTimeProvider time = new(FakeSessions.T0);
    readonly List<string> log = [];
    readonly List<AnswerRequest> answered = [];
    Func<AnswerRequest, AnswerResult> outcome = _ => new AnswerResult(AnswerOutcome.Delivered);

    public void Dispose()
    {
        if (Directory.Exists(folder)) { Directory.Delete(folder, true); }
    }

    HaConnector Connector(params string[] agents) => new(new HaConnectorOptions
    {
        MachineName = Machine,
        Version = "0.1.0",
        StateFile = Path.Combine(folder, "ha-connector.json"),
        AgentUserIds = agents,
        FieldPinDelay = TimeSpan.Zero,
        SelectDelay = TimeSpan.Zero,
    }, ha, sessions, () => null, log: m => { lock (log) { log.Add(m); } }, time: time, answer: (r, _) =>
    {
        answered.Add(r);
        return Task.FromResult(outcome(r));
    });

    async Task Pass(HaConnector c, double seconds = 1)
    {
        time.Advance(TimeSpan.FromSeconds(seconds));
        await c.TickAsync();
    }

    // The card: what a person (or an agent) does, then the pass that reads it.
    async Task Do(HaConnector c, HaTrigger hit)
    {
        c.Observe(hit);
        await Pass(c);
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

    static readonly Field Push = new() { Name = "push", Title = "Push now", Type = FieldType.Boolean };

    void Ask(Question? q, int turn = 1, string? message = "go") =>
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Waiting) with
        {
            Question = q,
            Activity = new Activity { Response = "Working on it", TurnCount = turn, UserMessage = message },
        });

    string State(string entityId) => ha.GetStateAsync(entityId).Result?.State ?? "(none)";
    JsonElement Attributes(string entityId) => ha.GetStateAsync(entityId).Result!.Attributes;
    string Activity => ha.Retained[HaContract.Topics(Node).ActivityState];
    JsonElement Detail => JsonDocument.Parse(ha.Retained[HaContract.Topics(Node).ActivityAttributes]).RootElement;

    [Fact]
    public async Task A_session_publishes_its_whole_card_except_stop_as_the_daemon_does()
    {
        Ask(null);
        await using var c = Connector();
        await Pass(c);

        var recorded = Contract.Publications("sessionPublish");
        var stop = HaContract.DiscoveryTopic("button", Node, "stop");
        var expected = recorded.Where(p => p.Topic != stop).ToList();
        var sent = ha.Log.Where(p => expected.Any(e => e.Topic == p.Topic)).Take(expected.Count).ToList();
        Contract.Equal(expected, sent);
        // Stop is a later stage: withdrawn, not left as a button nothing acts on.
        Assert.Contains(ha.Log, p => p.Topic == stop && p.Payload.Length == 0);
        Assert.False(ha.Exists(SessionEntities.Stop(Node)));
        // Every control is on the id the dashboard and the card address it by.
        foreach (var id in HaContract.SessionControlIds(Node).Values) { Assert.True(ha.Exists(id), id); }
    }

    [Fact]
    public async Task A_question_arms_the_card_and_answered_at_the_terminal_clears_it()
    {
        Ask(Form("q1", Release));
        await using var c = Connector();
        await Pass(c);
        Assert.Equal(HaDecision.Awaiting, State(SessionEntities.Decision(Node)));
        Assert.Equal("q1", Attributes(SessionEntities.Decision(Node)).GetProperty("decision_id").GetString());
        Assert.Equal(HaDecision.Choose, State(SessionEntities.Field(Node, 1)));
        Assert.Equal(["Choose...", "Cut the release now", "Wait for the fix"],
            Attributes(SessionEntities.Field(Node, 1)).GetProperty("options").EnumerateArray().Select(o => o.GetString()));

        Ask(null);
        await Pass(c);
        Assert.Equal(HaDecision.Idle, State(SessionEntities.Decision(Node)));
        Assert.Equal(HaDecision.Idle, State(SessionEntities.Field(Node, 1)));
        Assert.Equal("{}", ha.Retained[HaContract.Topics(Node).DecisionAttributes]);
        Assert.Empty(answered);
    }

    [Fact]
    public async Task Choosing_answers_nothing_until_send_and_then_the_options_own_value_is_sent()
    {
        Ask(Form("q1", Release));
        await using var c = Connector();
        await Pass(c);
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut the release now"));
        Assert.Empty(answered);

        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        var request = Assert.Single(answered);
        Assert.Equal(A, request.Id);
        Assert.Equal("q1", request.Question);
        Assert.Equal("cut_now", request.Fields!["release"].GetString());
        Assert.Null(request.Text);
        Assert.Equal("Answer sent", Activity);
        Assert.Equal("Cut the release now", Detail.GetProperty("answer").GetString());
        // The reply box goes back to its blank sentinel, as after any answer.
        Assert.Contains(ha.Services, s => s.Service == "text.set_value" && s.EntityId == SessionEntities.Reply(Node) && s.Value == " ");

        // The same press, read again, is not a second answer.
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Wait for the fix"));
        Assert.Single(answered);
    }

    [Fact]
    public async Task Send_on_an_incomplete_form_says_what_is_missing_and_the_tap_that_completes_it_does_not_send()
    {
        Ask(Form("q1", Release, Push));
        await using var c = Connector();
        await Pass(c);
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Wait for the fix"));
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Empty(answered);
        Assert.Equal("Not sent - answer every field", Activity);
        Assert.Equal("Push now", Detail.GetProperty("waiting_on").GetString());

        await Do(c, ha.Choose(SessionEntities.Field(Node, 2), "No"));
        Assert.Empty(answered);

        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        var request = Assert.Single(answered);
        Assert.Equal("wait", request.Fields!["release"].GetString());
        Assert.Equal(JsonValueKind.False, request.Fields["push"].ValueKind);
    }

    [Fact]
    public async Task A_press_from_before_the_question_was_shown_is_never_its_answer()
    {
        Ask(null);
        await using var c = Connector();
        await Pass(c);
        await Do(c, ha.Press(SessionEntities.Submit(Node)));

        Ask(Form("q1", Release));
        await Pass(c);
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut the release now"));
        Assert.Empty(answered);
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Single(answered);
    }

    [Fact]
    public async Task Words_from_the_reply_card_answer_a_freeform_question_and_the_card_learns_they_were_used()
    {
        Ask(Form("q1"));
        await using var c = Connector();
        await Pass(c);
        Assert.Equal("freeform", Attributes(SessionEntities.Decision(Node)).GetProperty("mode").GetString());

        var hit = ha.SendFromCard(Node, "Call it Aurora", decisionId: "q1");
        await Do(c, hit);
        var request = Assert.Single(answered);
        Assert.Equal("Call it Aurora", request.Text);
        Assert.Null(request.Fields);
        Assert.Equal(hit.State.State, Detail.GetProperty("answer_consumed_at").GetString());

        // And it stays on the card's activity after the session moves on.
        Ask(null, turn: 2, message: "next");
        await Pass(c);
        Assert.Equal("Working on it", Activity);
        Assert.Equal(hit.State.State, Detail.GetProperty("answer_consumed_at").GetString());
    }

    [Fact]
    public async Task Words_typed_for_another_question_are_not_this_ones_answer()
    {
        Ask(Form("q1"));
        await using var c = Connector();
        await Pass(c);
        await Do(c, ha.SendFromCard(Node, "meant for the last one", decisionId: "q0"));
        Assert.Empty(answered);
    }

    [Fact]
    public async Task A_lone_choice_takes_words_through_other_and_a_yes_no_does_not()
    {
        Ask(Form("q1", Release));
        await using var c = Connector();
        await Pass(c);
        Assert.True(Attributes(SessionEntities.Decision(Node)).GetProperty("takes_typed_answer").GetBoolean());
        await Do(c, ha.SendFromCard(Node, "Ship it on Friday", decisionId: "q1"));
        var request = Assert.Single(answered);
        Assert.Equal("Ship it on Friday", request.Fields!["release"].GetProperty("other").GetString());

        Ask(Form("q2", Push), turn: 2);
        await Pass(c);
        Assert.False(Attributes(SessionEntities.Decision(Node)).TryGetProperty("takes_typed_answer", out _));
        await Do(c, ha.SendFromCard(Node, "maybe", decisionId: "q2"));
        Assert.Single(answered);
        Assert.Equal("Not sent - this question takes options", Activity);
    }

    [Fact]
    public async Task An_agents_press_drives_the_next_turn_and_a_person_at_the_machine_takes_it_back()
    {
        Ask(Form("q1", Release));
        await using var c = Connector("agent-user");
        await Pass(c);
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut the release now", "agent-user"));
        await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user"));
        Assert.Single(answered);

        Ask(null, turn: 2, message: "the agent's answer");
        await Pass(c);
        Assert.Equal("agent", Detail.GetProperty("driver").GetString());

        // A turn nobody armed is the person's at the machine.
        Ask(null, turn: 3, message: "typed in the terminal");
        await Pass(c);
        Assert.Equal("human", Detail.GetProperty("driver").GetString());
    }

    [Fact]
    public async Task A_persons_press_is_the_persons_even_when_agents_are_configured()
    {
        Ask(Form("q1", Release));
        await using var c = Connector("agent-user");
        await Pass(c);
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut the release now", "person"));
        await Do(c, ha.Press(SessionEntities.Submit(Node), "person"));
        Ask(null, turn: 2, message: "next");
        await Pass(c);
        Assert.Equal("human", Detail.GetProperty("driver").GetString());
    }

    [Fact]
    public async Task An_approval_is_answered_by_its_tap()
    {
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Waiting) with
        {
            Agent = AgentKind.Codex,
            Question = new Question
            {
                Id = "a1", Kind = QuestionKind.Approval, Message = "Codex wants to run git push", AskedAt = FakeSessions.T0,
                Fields = [new Field { Name = "decision", Type = FieldType.Choice, Options = [new Option("approve", "Approve once"), new Option("deny", "Deny")], Required = true }],
            },
        });
        await using var c = Connector();
        await Pass(c);
        Assert.True(Attributes(SessionEntities.Decision(Node)).GetProperty("answer_on_tap").GetBoolean());
        await Do(c, ha.Choose(SessionEntities.Decision(Node), "Deny"));
        var request = Assert.Single(answered);
        Assert.Equal("deny", request.Fields!["decision"].GetString());
    }

    [Fact]
    public async Task An_answer_the_host_refuses_is_said_to_be_not_sent_and_can_be_given_again()
    {
        outcome = _ => new AnswerResult(AnswerOutcome.Busy, "another answer or reply to this session is being delivered");
        Ask(Form("q1", Release));
        await using var c = Connector();
        await Pass(c);
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut the release now"));
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Equal("Answer NOT sent", Activity);
        Assert.Equal("nothing reached the terminal", Detail.GetProperty("hint").GetString());
        // The press that failed is not tried again by itself, however often the card is read.
        await Pass(c, 6);
        await Pass(c, 6);
        Assert.Single(answered);

        outcome = _ => new AnswerResult(AnswerOutcome.Delivered);
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Equal(2, answered.Count);
        // Two presses are two requests; a resend of one would carry its id again.
        Assert.NotEqual(answered[0].Request, answered[1].Request);
        Assert.Equal("Answer sent", Activity);
    }

    [Fact]
    public async Task A_press_the_watch_never_reported_is_still_answered_when_the_card_is_next_read()
    {
        Ask(Form("q1", Release));
        await using var c = Connector();
        await Pass(c);
        // Made while the watch was reconnecting: Home Assistant has it, the connector heard nothing.
        ha.Choose(SessionEntities.Field(Node, 1), "Wait for the fix");
        ha.Press(SessionEntities.Submit(Node));
        await Pass(c, 6);
        Assert.Equal("wait", Assert.Single(answered).Fields!["release"].GetString());
        Assert.Equal("Answer sent", Activity);
        await Pass(c, 6);
        Assert.Single(answered);
    }

    [Fact]
    public async Task An_approval_tap_that_was_refused_can_be_tapped_again_and_is_tried_afresh()
    {
        outcome = _ => new AnswerResult(AnswerOutcome.Busy, "another answer or reply to this session is being delivered");
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Waiting) with
        {
            Agent = AgentKind.Codex,
            Question = new Question
            {
                Id = "a1", Kind = QuestionKind.Approval, Message = "Codex wants to run git push", AskedAt = FakeSessions.T0,
                Fields = [new Field { Name = "decision", Type = FieldType.Choice, Options = [new Option("approve", "Approve once"), new Option("deny", "Deny")], Required = true }],
            },
        });
        await using var c = Connector();
        await Pass(c);
        await Do(c, ha.Choose(SessionEntities.Decision(Node), "Approve"));
        Assert.Equal("Answer NOT sent", Activity);
        // Back to waiting, so the same option can be chosen again; and not retried by itself.
        Assert.Equal(HaDecision.Awaiting, State(SessionEntities.Decision(Node)));
        await Pass(c, 6);
        Assert.Single(answered);
        await Do(c, ha.Choose(SessionEntities.Decision(Node), "Approve"));
        Assert.Equal(2, answered.Count);

        // Tapped again before the card was read waiting: the watch saw it wait, so it counts.
        c.Observe(new HaTrigger(SessionEntities.Decision(Node), (await ha.GetStateAsync(SessionEntities.Decision(Node)))!));
        outcome = _ => new AnswerResult(AnswerOutcome.Delivered);
        await Do(c, ha.Choose(SessionEntities.Decision(Node), "Approve"));
        Assert.Equal(3, answered.Count);
        // Each tap is a new request, not the refusal the host remembers for the one before.
        Assert.Equal(3, answered.Select(a => a.Request).Distinct().Count());
        Assert.Equal("Answer sent", Activity);
    }

    [Fact]
    public async Task A_refused_tap_the_selector_could_not_be_reset_from_is_not_sent_again_by_itself()
    {
        outcome = _ => new AnswerResult(AnswerOutcome.Busy, "another answer or reply to this session is being delivered");
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Waiting) with
        {
            Agent = AgentKind.Codex,
            Question = new Question
            {
                Id = "a1", Kind = QuestionKind.Approval, Message = "Codex wants to run git push", AskedAt = FakeSessions.T0,
                Fields = [new Field { Name = "decision", Type = FieldType.Choice, Options = [new Option("approve", "Approve once"), new Option("deny", "Deny")], Required = true }],
            },
        });
        await using var c = Connector();
        await Pass(c);
        ha.ServiceFailsFor = (service, entity) => service == "select.select_option" && entity == SessionEntities.Decision(Node);
        await Do(c, ha.Choose(SessionEntities.Decision(Node), "Approve"));
        Assert.Equal("Answer NOT sent", Activity);
        // Still showing Approve: read again and again, it is the same refused tap.
        Assert.Equal("Approve", State(SessionEntities.Decision(Node)));
        await Pass(c, 6);
        await Pass(c, 6);
        Assert.Single(answered);
    }

    [Fact]
    public async Task A_new_approval_whose_selector_still_shows_the_last_tap_is_not_answered_by_it()
    {
        static Session Approval(string id) => FakeSessions.Copilot(A, status: SessionStatus.Waiting) with
        {
            Agent = AgentKind.Codex,
            Question = new Question
            {
                Id = id, Kind = QuestionKind.Approval, Message = "Codex wants to run git push", AskedAt = FakeSessions.T0,
                Fields = [new Field { Name = "decision", Type = FieldType.Choice, Options = [new Option("approve", "Approve once"), new Option("deny", "Deny")], Required = true }],
            },
        };
        sessions.Set(Approval("a1"));
        await using var c = Connector();
        await Pass(c);
        await Do(c, ha.Choose(SessionEntities.Decision(Node), "Approve"));
        Assert.Single(answered);

        // The next approval arrives, and the selector cannot be set waiting: it still says Approve.
        ha.ServiceFailsFor = (service, entity) => service == "select.select_option" && entity == SessionEntities.Decision(Node);
        sessions.Set(Approval("a2"));
        await Pass(c);
        Assert.Equal("Approve", State(SessionEntities.Decision(Node)));
        await Pass(c, 6);
        await Pass(c, 6);
        Assert.Single(answered);

        // Once it is seen waiting, a tap answers it.
        ha.ServiceFailsFor = null;
        await Do(c, ha.Choose(SessionEntities.Decision(Node), HaDecision.Awaiting));
        await Do(c, ha.Choose(SessionEntities.Decision(Node), "Approve"));
        Assert.Equal(2, answered.Count);
        Assert.Equal("a2", answered[1].Question);
    }

    [Fact]
    public async Task Cancel_chosen_twice_is_undone_both_times_so_the_form_can_still_be_sent()
    {
        Ask(Form("q1", Release));
        await using var c = Connector();
        await Pass(c);
        await Do(c, ha.Choose(SessionEntities.Decision(Node), HaDecision.Cancel));
        Assert.Equal(HaDecision.Awaiting, State(SessionEntities.Decision(Node)));
        await Do(c, ha.Choose(SessionEntities.Decision(Node), HaDecision.Cancel));
        Assert.Equal(HaDecision.Awaiting, State(SessionEntities.Decision(Node)));

        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Wait for the fix"));
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Equal("wait", Assert.Single(answered).Fields!["release"].GetString());
    }

    [Fact]
    public async Task A_press_whose_sending_note_could_not_be_shown_is_read_again_and_sent()
    {
        Ask(Form("q1", Release));
        await using var c = Connector();
        await Pass(c);
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Wait for the fix"));
        ha.PublishFailsFor = (topic, payload) => topic == HaContract.Topics(Node).ActivityState && payload == "Sending answer...";
        c.Observe(ha.Press(SessionEntities.Submit(Node)));
        await Pass(c);
        Assert.Empty(answered);

        ha.PublishFailsFor = null;
        await Pass(c);
        Assert.Single(answered);
        Assert.Equal("Answer sent", Activity);
    }

    [Fact]
    public async Task Reply_card_words_that_reached_nothing_are_not_reported_as_used()
    {
        outcome = _ => new AnswerResult(AnswerOutcome.Busy, "another answer or reply to this session is being delivered");
        Ask(Form("q1"));
        await using var c = Connector();
        await Pass(c);
        await Do(c, ha.SendFromCard(Node, "Call it Aurora", "q1"));
        Assert.Equal("Answer NOT sent", Activity);

        // The session's own activity moves on; the card's draft must not be told to clear.
        sessions.Set(FakeSessions.Copilot(A, status: SessionStatus.Waiting) with
        {
            Question = Form("q1"),
            Activity = new Activity { Response = "Still waiting", TurnCount = 1, UserMessage = "go" },
        });
        await Pass(c);
        Assert.Equal("Still waiting", Activity);
        Assert.False(Detail.TryGetProperty("answer_consumed_at", out var consumed) && consumed.ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task Words_left_in_the_reply_box_from_before_the_question_do_not_fill_its_text_field()
    {
        Ask(null);
        await using var c = Connector();
        await Pass(c);
        ha.Type(SessionEntities.Reply(Node), "words for the last question");
        Ask(Form("q1", Release, new Field { Name = "note", Title = "Note", Type = FieldType.Text }));
        await Pass(c);
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Wait for the fix"));
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Equal("", Assert.Single(answered).Fields!["note"].GetString());
    }

    [Fact]
    public async Task Words_typed_in_the_reply_box_and_sent_answer_a_choice_that_takes_another_answer()
    {
        Ask(Form("q1", Release));
        await using var c = Connector();
        await Pass(c);
        await Do(c, ha.Type(SessionEntities.Reply(Node), "after the hotfix lands"));
        Assert.Empty(answered);
        await Do(c, ha.Press(SessionEntities.Submit(Node), "agent-user"));
        var request = Assert.Single(answered);
        Assert.Equal("after the hotfix lands", request.Fields!["release"].GetProperty("other").GetString());
    }

    [Fact]
    public async Task Reply_card_words_sent_before_the_form_is_complete_wait_for_a_press_of_send()
    {
        Ask(Form("q1", Release, new Field { Name = "note", Title = "Note", Type = FieldType.Text }));
        await using var c = Connector();
        await Pass(c);
        await Do(c, ha.SendFromCard(Node, "after the hotfix", "q1"));
        Assert.Equal("Not sent - answer every field", Activity);
        // Finishing the form does not send the words by itself.
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Wait for the fix"));
        await Pass(c, 6);
        Assert.Empty(answered);

        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        var request = Assert.Single(answered);
        Assert.Equal("after the hotfix", request.Fields!["note"].GetString());
        Assert.Equal("wait", request.Fields!["release"].GetString());
    }

    [Fact]
    public async Task A_slot_still_showing_the_last_questions_choice_is_not_an_answer_to_the_next()
    {
        Ask(Form("q1", Push));
        await using var c = Connector();
        await Pass(c);
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Yes"));
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Single(answered);

        // The next question, and the slot cannot be reset: it still says Yes.
        ha.ServiceFailsFor = (service, entity) => service == "select.select_option" && entity == SessionEntities.Field(Node, 1);
        Ask(Form("q2", Push), turn: 2);
        await Pass(c);
        Assert.Equal("Yes", State(SessionEntities.Field(Node, 1)));
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Single(answered);
        Assert.Equal("Not sent - answer every field", Activity);

        ha.ServiceFailsFor = null;
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "No"));
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Equal(2, answered.Count);
        Assert.False(answered[1].Fields!["push"].GetBoolean());
    }

    [Fact]
    public async Task A_number_is_sent_exactly_as_typed()
    {
        Ask(Form("q1", Release, new Field { Name = "count", Title = "Count", Type = FieldType.Number }));
        await using var c = Connector();
        await Pass(c);
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Wait for the fix"));
        await Do(c, ha.Type(SessionEntities.Reply(Node), "9007199254740993"));
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Equal("9007199254740993", Assert.Single(answered).Fields!["count"].GetRawText());
    }

    [Fact]
    public async Task Refused_words_go_again_on_a_send_the_watch_never_reported()
    {
        outcome = _ => new AnswerResult(AnswerOutcome.Busy, "another answer or reply to this session is being delivered");
        Ask(Form("q1"));
        await using var c = Connector();
        await Pass(c);
        await Do(c, ha.Type(SessionEntities.Reply(Node), "Call it Aurora"));
        Assert.Equal("Answer NOT sent", Activity);

        outcome = _ => new AnswerResult(AnswerOutcome.Delivered);
        // Pressed while the watch was reconnecting: only the next read of the card sees it.
        ha.Press(SessionEntities.Submit(Node));
        await Pass(c, 6);
        Assert.Equal(2, answered.Count);
        Assert.Equal("Answer sent", Activity);
    }

    [Fact]
    public async Task Words_sent_to_a_question_that_takes_only_options_do_not_stand_in_the_way_of_choosing()
    {
        Ask(Form("q1", Push));
        await using var c = Connector();
        await Pass(c);
        await Do(c, ha.SendFromCard(Node, "maybe later", "q1"));
        Assert.Equal("Not sent - this question takes options", Activity);
        // Chosen and sent together, in one read of the card.
        c.Observe(ha.Choose(SessionEntities.Field(Node, 1), "Yes"));
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.True(Assert.Single(answered).Fields!["push"].GetBoolean());

        // And on a form of several choices.
        answered.Clear();
        Ask(Form("q2", Release, Push), turn: 2);
        await Pass(c);
        await Do(c, ha.SendFromCard(Node, "whatever you think", "q2"));
        Assert.Equal("Not sent - this question takes options", Activity);
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Wait for the fix"));
        await Do(c, ha.Choose(SessionEntities.Field(Node, 2), "No"));
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Equal("wait", Assert.Single(answered).Fields!["release"].GetString());
    }

    [Fact]
    public async Task A_send_pressed_while_a_refused_form_was_being_delivered_does_not_send_it_again()
    {
        var note = new Field { Name = "note", Title = "Note", Type = FieldType.Text };
        Ask(Form("q1", Release, note));
        await using var c = Connector();
        outcome = _ =>
        {
            ha.Press(SessionEntities.Submit(Node));
            return new AnswerResult(AnswerOutcome.Busy, "another answer or reply to this session is being delivered");
        };
        await Pass(c);
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Wait for the fix"));
        // Submitted from Send, then from the reply card: each time Send is pressed again mid-delivery.
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        await Pass(c, 6);
        await Pass(c, 6);
        Assert.Single(answered);
        await Do(c, ha.SendFromCard(Node, "after the hotfix", "q1"));
        Assert.Equal(2, answered.Count);
        await Pass(c, 6);
        await Pass(c, 6);
        Assert.Equal(2, answered.Count);

        // A press after the refusal does send it.
        outcome = _ => new AnswerResult(AnswerOutcome.Delivered);
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Equal(3, answered.Count);
        Assert.Equal("Answer sent", Activity);
    }

    [Fact]
    public async Task A_send_that_arrived_while_refused_words_were_being_delivered_does_not_send_them_again()
    {
        Ask(Form("q1"));
        await using var c = Connector();
        // Typing then Send: the box's change and the press arrive a moment apart, and the press
        // lands while the box's words are being delivered.
        outcome = _ =>
        {
            c.Observe(ha.Press(SessionEntities.Submit(Node)));
            return new AnswerResult(AnswerOutcome.Busy, "another answer or reply to this session is being delivered");
        };
        await Pass(c);
        await Do(c, ha.Type(SessionEntities.Reply(Node), "Call it Aurora"));
        Assert.Equal("Answer NOT sent", Activity);
        outcome = _ => new AnswerResult(AnswerOutcome.Delivered);
        await Pass(c);
        await Pass(c, 6);
        Assert.Single(answered);

        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Equal(2, answered.Count);
        Assert.Equal("Answer sent", Activity);
    }

    [Fact]
    public async Task A_form_whose_words_came_from_the_reply_card_keeps_them_for_a_send_after_a_refusal()
    {
        var note = new Field { Name = "note", Title = "Note", Type = FieldType.Text };
        outcome = _ => new AnswerResult(AnswerOutcome.Busy, "another answer or reply to this session is being delivered");
        Ask(Form("q1", Release, note));
        await using var c = Connector();
        await Pass(c);
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Wait for the fix"));
        await Do(c, ha.SendFromCard(Node, "after the hotfix", "q1"));
        Assert.Equal("after the hotfix", Assert.Single(answered).Fields!["note"].GetString());
        Assert.Equal("Answer NOT sent", Activity);
        // The card's words do not submit themselves again.
        await Pass(c, 6);
        Assert.Single(answered);

        outcome = _ => new AnswerResult(AnswerOutcome.Delivered);
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Equal(2, answered.Count);
        Assert.Equal("after the hotfix", answered[1].Fields!["note"].GetString());
        Assert.Equal("wait", answered[1].Fields!["release"].GetString());
        // The reply card is told its words were used, so it clears its draft.
        Assert.Equal(State(SessionEntities.ReplyPayload(Node)), Detail.GetProperty("answer_consumed_at").GetString());
    }

    [Fact]
    public async Task Refused_words_from_the_reply_box_go_again_on_a_press_of_send_and_not_by_themselves()
    {
        outcome = _ => new AnswerResult(AnswerOutcome.Busy, "another answer or reply to this session is being delivered");
        Ask(Form("q1"));
        await using var c = Connector();
        await Pass(c);
        await Do(c, ha.Type(SessionEntities.Reply(Node), "Call it Aurora"));
        Assert.Equal("Answer NOT sent", Activity);
        await Pass(c, 6);
        Assert.Single(answered);

        outcome = _ => new AnswerResult(AnswerOutcome.Delivered);
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Equal(2, answered.Count);
        Assert.Equal("Call it Aurora", answered[1].Text);
        Assert.NotEqual(answered[0].Request, answered[1].Request);
        Assert.Equal("Answer sent", Activity);
    }

    [Fact]
    public async Task An_answer_that_may_have_reached_the_terminal_is_never_typed_again()
    {
        outcome = _ => new AnswerResult(AnswerOutcome.Unconfirmed, "the prompt did not change");
        Ask(Form("q1", Release));
        await using var c = Connector();
        await Pass(c);
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Cut the release now"));
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Equal("Answer unconfirmed - check the terminal", Activity);
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Single(answered);
    }

    [Fact]
    public async Task A_question_only_the_machine_can_answer_is_shown_and_never_read()
    {
        Ask(Form("q1", Release) with { AnswerAtMachine = true });
        await using var c = Connector();
        await Pass(c);
        Assert.Contains("**This one has to be answered in the terminal**", Attributes(SessionEntities.Decision(Node)).GetProperty("question").GetString(), StringComparison.Ordinal);
        await Do(c, ha.SendFromCard(Node, "yes", decisionId: "q1"));
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Empty(answered);
    }

    [Fact]
    public async Task Words_sent_with_no_question_waiting_are_said_to_be_unserved_rather_than_dropped()
    {
        Ask(null);
        await using var c = Connector();
        await Pass(c);
        await Do(c, ha.SendFromCard(Node, "carry on"));
        Assert.Empty(answered);
        Assert.Equal("Reply NOT sent", Activity);
        Assert.Equal("reply at the machine", Detail.GetProperty("hint").GetString());
        Assert.False(Detail.TryGetProperty("answer_consumed_at", out _));
    }

    [Fact]
    public async Task A_cancel_is_refused_and_the_question_stays_answerable()
    {
        Ask(Form("q1", Release));
        await using var c = Connector();
        await Pass(c);
        await Do(c, ha.Choose(SessionEntities.Decision(Node), HaDecision.Cancel));
        Assert.Equal("Answer NOT sent", Activity);
        Assert.Equal(HaDecision.Awaiting, State(SessionEntities.Decision(Node)));
        await Do(c, ha.Choose(SessionEntities.Field(Node, 1), "Wait for the fix"));
        await Do(c, ha.Press(SessionEntities.Submit(Node)));
        Assert.Single(answered);
    }

    [Fact]
    public async Task A_card_taken_over_with_a_question_nobody_is_asking_any_more_is_cleared()
    {
        // agent-ha-bridge armed a question, which was then answered while no bridge ran.
        foreach (var p in Contract.Publications("sessionPublish")) { ha.Seed(p.Topic, p.Payload); }
        ha.Seed(HaContract.Topics(Node).DecisionAttributes, "{\"decision_id\":\"old\",\"field_count\":1}");
        Ask(null);
        await using var c = Connector();
        await Pass(c);
        Assert.Equal("{}", ha.Retained[HaContract.Topics(Node).DecisionAttributes]);
        Assert.Equal(HaDecision.Idle, State(SessionEntities.Decision(Node)));

        // A card with nothing armed is left as it is: no clear on every start.
        var c2Log = ha.Log.Count;
        await using var again = Connector();
        await Pass(again);
        Assert.DoesNotContain(ha.Log.Skip(c2Log), p => p.Topic == HaContract.Topics(Node).DecisionAttributes);
    }

    [Fact]
    public async Task The_watch_covers_every_sessions_controls_and_its_hits_reach_the_card()
    {
        Ask(Form("q1", Release));
        await using var c = new HaConnector(new HaConnectorOptions
        {
            MachineName = Machine, Version = "0.1.0", Tick = TimeSpan.FromMilliseconds(20),
            FieldPinDelay = TimeSpan.Zero, SelectDelay = TimeSpan.Zero,
        }, ha, sessions, () => null, answer: (r, _) => { lock (answered) { answered.Add(r); } return Task.FromResult(new AnswerResult(AnswerOutcome.Delivered)); });
        c.Start();
        await Until(() => ha.Watches.Count > 0 && ha.Watches[^1].Contains(SessionEntities.Submit(Node)));
        Assert.Equal(HaContract.MaxFields + 4, ha.Watches[^1].Count);
        ha.Trigger(ha.Choose(SessionEntities.Field(Node, 1), "Cut the release now"));
        ha.Trigger(ha.Press(SessionEntities.Submit(Node)));
        await Until(() => { lock (answered) { return answered.Count == 1; } });
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
