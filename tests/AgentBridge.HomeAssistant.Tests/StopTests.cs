using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// End session from a card, against an in-process Home Assistant and a stand-in for the host's
// stop delivery that keeps the host's two-press rule (session.stop): what the card says at each
// press, as the daemon says it (Fixtures/ha-stops.json, recorded from its own functions), and
// what reaches the host.
public sealed class StopTests : IDisposable
{
    const string Machine = "DSWETT-DEV-VM1";
    const string A = "6fcbab0c-1d2e-4f50-9a61-7b8c9d0e1f20";
    static readonly string Node = HaContract.NodeId(A);
    static readonly string[] TransientKeys = ["error", "hint", "waiting_on", "sent", "unsent", "recorded", "answer", "at"];

    static readonly Lazy<JsonObject> Loaded = new(() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ha-stops.json")))!);

    readonly string folder = Path.Combine(Path.GetTempPath(), "abhs-" + Guid.NewGuid().ToString("N"));
    readonly FakeHomeAssistant ha = new();
    readonly FakeSessions sessions = new();
    readonly FakeTimeProvider time = new(FakeSessions.T0);
    readonly List<string> log = [];
    readonly List<StopRequest> stopped = [];
    // The host's rule: an idle session, or a press carrying the token a first press was given,
    // ends; any other asks for a second press.
    Func<StopRequest, Task<StopResult>>? outcome;
    // What ending comes to once the host has decided to end.
    StopResult ending = new(StopOutcome.Ended);

    public void Dispose()
    {
        if (Directory.Exists(folder)) { Directory.Delete(folder, true); }
    }

    Task<StopResult> Host(StopRequest r)
    {
        if (outcome is not null) { return outcome(r); }
        var s = sessions.Get(r.Id);
        if (s is null) { return Task.FromResult(new StopResult(StopOutcome.Stale, "the session has ended")); }
        var idle = s.Status == SessionStatus.Idle && s.Activity.Background is null;
        if (!idle && r.Confirm != "token-1")
        {
            return Task.FromResult(new StopResult(StopOutcome.Confirm, "this session is still working; the confirmation lapses in 10 s") { Confirm = "token-1", ConfirmSeconds = 10 });
        }
        if (ending.Outcome == StopOutcome.Ended) { sessions.End(r.Id); }
        return Task.FromResult(ending);
    }

    HaConnector Connector(bool stops = true) => new(new HaConnectorOptions
    {
        MachineName = Machine,
        Version = "0.1.0",
        StateFile = Path.Combine(folder, "ha-connector.json"),
        FieldPinDelay = TimeSpan.Zero,
        SelectDelay = TimeSpan.Zero,
    }, ha, sessions, () => null, log: m => { lock (log) { log.Add(m); } }, time: time,
        answer: (_, _) => Task.FromResult(new AnswerResult(AnswerOutcome.Delivered)),
        stop: stops
            ? (r, _) =>
            {
                lock (stopped) { stopped.Add(r); }
                return Host(r);
            }
            : null);

    async Task Pass(HaConnector c, double seconds = 1)
    {
        time.Advance(TimeSpan.FromSeconds(seconds));
        await c.TickAsync();
    }

    async Task Press(HaConnector c, double after = 1)
    {
        c.Observe(ha.Press(SessionEntities.Stop(Node)));
        await Pass(c, after);
        await c.DeliveredAsync();
        await Pass(c, 0.01);
    }

    void Status(SessionStatus status, int agents = 0, int shells = 0, int turn = 1) =>
        sessions.Set(FakeSessions.Copilot(A, status: status) with
        {
            Activity = new Activity { Response = "Reading files", TurnCount = turn, UserMessage = "go", Background = Background.Of(agents, shells) },
        });

    async Task<HaConnector> Ready(SessionStatus status, int agents = 0, int shells = 0)
    {
        Status(status, agents, shells);
        var c = Connector();
        await Pass(c);
        await Pass(c);
        return c;
    }

    string Activity => ha.Retained[HaContract.Topics(Node).ActivityState];
    JsonElement Detail => JsonDocument.Parse(ha.Retained[HaContract.Topics(Node).ActivityAttributes]).RootElement;

    List<(string Summary, JsonElement Attributes)> NotesSince(int mark)
    {
        var t = HaContract.Topics(Node);
        var notes = new List<(string, JsonElement)>();
        var published = ha.Log.Skip(mark).ToList();
        for (var i = 0; i < published.Count; i++)
        {
            if (published[i].Topic != t.ActivityState) { continue; }
            var attributes = published.Skip(i + 1).First(p => p.Topic == t.ActivityAttributes);
            notes.Add((published[i].Payload, JsonDocument.Parse(attributes.Payload).RootElement));
        }
        return notes;
    }

    List<string> StatusesSince(int mark) => [.. ha.Log.Skip(mark).Where(p => p.Topic == HaContract.Topics(Node).StatusState).Select(p => p.Payload)];

    static JsonObject Case(string name) =>
        Loaded.Value["cases"]!.AsArray().Select(c => c!.AsObject()).Single(c => c["name"]!.GetValue<string>() == name);

    // What the card was told, and what the status showed, as the daemon did in the same case.
    void AssertCase(string name, int mark, bool statuses = true)
    {
        var expected = Case(name)["notes"]!.AsArray().Select(n => n!.AsObject()).ToList();
        var actual = NotesSince(mark);
        Assert.Equal(expected.Select(n => n["summary"]!.GetValue<string>()), actual.Select(n => n.Summary));
        for (var i = 0; i < expected.Count; i++)
        {
            var extra = expected[i]["extra"]!.AsObject();
            foreach (var (key, value) in extra) { Assert.Equal(value!.GetValue<string>(), actual[i].Attributes.GetProperty(key).GetString()); }
            foreach (var key in TransientKeys.Where(k => !extra.ContainsKey(k))) { Assert.False(actual[i].Attributes.TryGetProperty(key, out _), $"{name}: {key}"); }
        }
        if (statuses) { Assert.Equal(Case(name)["statuses"]!.AsArray().Select(s => s!.GetValue<string>()), StatusesSince(mark)); }
    }

    [Fact]
    public void The_words_and_hints_are_the_daemons()
    {
        var root = Loaded.Value;
        Assert.Equal(A, root["sessionId"]!.GetValue<string>());
        var constants = root["constants"]!.AsObject();
        Assert.Equal(Stops.ConfirmSeconds, constants["confirmSeconds"]!.GetValue<int>());
        Assert.Equal(HaStop.ConfirmNote, constants["confirmNote"]!.GetValue<string>());
        Assert.Equal(HaStop.LapsedNote, constants["lapsedNote"]!.GetValue<string>());
        Assert.Equal((int)new HaConnectorOptions { MachineName = Machine, Version = "0" }.StopLapseRetry.TotalSeconds, constants["lapseRetrySeconds"]!.GetValue<int>());
        var hints = root["hints"]!.AsObject();
        foreach (var (status, key) in new[] { ("working", "working"), ("waiting", "waiting"), ("agents", "agents"), ("shell", "shell"), ("", "unclassified"), ("compacting", "other") })
        {
            Assert.Equal(hints[key]!.GetValue<string>(), HaStop.Hint(status, 10));
        }
    }

    [Fact]
    public async Task The_card_offers_end_session_on_the_id_the_dashboard_uses()
    {
        await using var c = await Ready(SessionStatus.Idle);
        var recorded = Contract.Publications("sessionPublish");
        var sent = ha.Log.Where(p => recorded.Any(e => e.Topic == p.Topic)).Take(recorded.Count).ToList();
        Contract.Equal(recorded, sent);
        Assert.True(ha.Exists(SessionEntities.Stop(Node)));
    }

    [Fact]
    public async Task An_idle_session_ends_on_one_press()
    {
        await using var c = await Ready(SessionStatus.Idle);
        var mark = ha.Log.Count;

        await Press(c);

        var request = Assert.Single(stopped);
        Assert.Equal(A, request.Id);
        Assert.Null(request.Confirm);
        Assert.True(Guid.TryParse(request.Request, out _));
        AssertCase("idleOnePress", mark);
    }

    [Theory]
    [InlineData("workingFirstPress", SessionStatus.Working, 0, 0)]
    [InlineData("waitingFirstPress", SessionStatus.Waiting, 0, 0)]
    [InlineData("agentsFirstPress", SessionStatus.Idle, 2, 0)]
    [InlineData("shellFirstPress", SessionStatus.Idle, 0, 1)]
    public async Task A_first_press_of_a_session_that_is_not_idle_asks_for_a_second(string name, SessionStatus status, int agents, int shells)
    {
        await using var c = await Ready(status, agents, shells);
        var mark = ha.Log.Count;

        await Press(c);

        Assert.Null(Assert.Single(stopped).Confirm);
        AssertCase(name, mark);
        Assert.True(sessions.Get(A) is not null);
    }

    [Fact]
    public async Task A_second_press_within_the_window_sends_the_first_ones_token_and_ends_it()
    {
        await using var c = await Ready(SessionStatus.Working);
        await Press(c);
        var mark = ha.Log.Count;

        await Press(c, after: 5);

        Assert.Equal([null, "token-1"], stopped.Select(r => r.Confirm));
        Assert.NotEqual(stopped[0].Request, stopped[1].Request);
        AssertCase("workingConfirmed", mark);
    }

    [Fact]
    public async Task The_question_stays_on_the_card_while_the_session_carries_on()
    {
        await using var c = await Ready(SessionStatus.Waiting);
        await Press(c);

        // The session writes something new: the card goes on asking, its own reply live beneath.
        sessions.Set(sessions.Get(A)! with { Activity = sessions.Get(A)!.Activity with { Response = "Still going" } });
        await Pass(c);
        var pinned = Loaded.Value["pinned"]!.AsObject();
        Assert.Equal(pinned["summary"]!.GetValue<string>(), Activity);
        Assert.Equal(pinned["hint"]!.GetValue<string>(), Detail.GetProperty("hint").GetString());
        Assert.Equal("Still going", Detail.GetProperty("response").GetString());
    }

    [Fact]
    public async Task A_first_press_nobody_confirms_lapses_and_says_so_and_the_next_press_asks_again()
    {
        await using var c = await Ready(SessionStatus.Working);
        await Press(c);
        var mark = ha.Log.Count;

        await Pass(c, 9);
        Assert.Empty(NotesSince(mark));
        await Pass(c, 2);
        AssertCase("lapsed", mark);

        // The card shows the session again, not the question.
        sessions.Set(sessions.Get(A)! with { Activity = sessions.Get(A)!.Activity with { Response = "More" } });
        await Pass(c);
        Assert.NotEqual(HaStop.ConfirmNote, Activity);

        await Press(c);
        Assert.Equal([null, null], stopped.Select(r => r.Confirm));
        Assert.Equal(HaStop.ConfirmNote, Activity);
    }

    [Fact]
    public async Task A_lapse_that_cannot_be_shown_is_tried_again_rather_than_dropped()
    {
        await using var c = await Ready(SessionStatus.Working);
        await Press(c);
        ha.PublishFailsFor = (topic, payload) => topic == HaContract.Topics(Node).ActivityState && payload == HaStop.LapsedNote;
        await Pass(c, 11);
        await Pass(c, 1);
        Assert.Equal(HaStop.ConfirmNote, Activity);

        ha.PublishFailsFor = null;
        await Pass(c, 3);
        Assert.Equal(HaStop.LapsedNote, Activity);
    }

    [Fact]
    public async Task A_question_the_card_could_not_show_is_not_confirmed_by_the_next_press()
    {
        await using var c = await Ready(SessionStatus.Working);
        ha.PublishFailsFor = (topic, payload) => topic == HaContract.Topics(Node).ActivityState && payload == HaStop.ConfirmNote;
        await Press(c);
        Assert.NotEqual(HaStop.ConfirmNote, Activity);

        // Pressed again because the first looked dead: asked again, never taken as the confirmation.
        ha.PublishFailsFor = null;
        await Press(c);
        Assert.Equal([null, null], stopped.Select(r => r.Confirm));
        Assert.Equal(HaStop.ConfirmNote, Activity);
        Assert.NotNull(sessions.Get(A));
    }

    [Fact]
    public async Task An_end_that_fails_says_so_and_shows_the_error()
    {
        ending = new StopResult(StopOutcome.Failed, "the session did not exit");
        await using var c = await Ready(SessionStatus.Idle);
        var mark = ha.Log.Count;

        await Press(c);

        AssertCase("endFails", mark);
    }

    [Fact]
    public async Task A_press_the_host_refuses_is_said_and_the_card_shows_the_session_again()
    {
        outcome = _ => Task.FromResult(new StopResult(StopOutcome.Refused, "the session's process is not known; end it at the machine"));
        await using var c = await Ready(SessionStatus.Idle);
        var mark = ha.Log.Count;

        await Press(c);
        Assert.Equal(HaStop.Failed, Activity);
        Assert.Equal("the session's process is not known; end it at the machine", Detail.GetProperty("error").GetString());

        // "ending" was shown ahead of the answer: the real status comes back.
        await Pass(c);
        Assert.Equal("idle", StatusesSince(mark).Last());
    }

    [Fact]
    public async Task A_press_read_again_and_a_press_already_on_the_button_at_start_end_nothing()
    {
        Status(SessionStatus.Idle);
        // The button carries a press from before (agent-ha-bridge's, or this host's before a
        // restart); it is not one for this connector.
        ha.States[SessionEntities.Stop(Node)] = new HaState(SessionEntities.Stop(Node), "2026-10-29T19:59:00.000000+00:00", default);
        await using var c = Connector();
        await Pass(c);
        await Pass(c, 20);
        await c.DeliveredAsync();
        Assert.Empty(stopped);
        ha.States.Remove(SessionEntities.Stop(Node));

        // A press, reported again, is one press.
        Status(SessionStatus.Working);
        await Pass(c);
        var press = ha.Press(SessionEntities.Stop(Node));
        c.Observe(press);
        await Pass(c);
        await c.DeliveredAsync();
        await Pass(c, 0.01);
        Assert.Single(stopped);
        var mark = ha.Log.Count;
        c.Observe(press);
        await Pass(c);
        await c.DeliveredAsync();
        Assert.Single(stopped);
        AssertCase("repeatPress", mark);
    }

    [Fact]
    public async Task A_session_asking_a_question_can_still_be_ended_from_its_card()
    {
        Status(SessionStatus.Waiting);
        sessions.Set(sessions.Get(A)! with { Question = new Question { Id = "q1", Kind = QuestionKind.Form, Message = "Go on?", AskedAt = FakeSessions.T0 } });
        await using var c = Connector();
        await Pass(c);
        await Pass(c);

        await Press(c);
        await Press(c, after: 2);

        Assert.Equal([null, "token-1"], stopped.Select(r => r.Confirm));
        Assert.Null(sessions.Get(A));
    }

    [Fact]
    public async Task A_slow_end_holds_up_nothing_else()
    {
        var release = new TaskCompletionSource<StopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        outcome = _ =>
        {
            reached.TrySetResult();
            return release.Task;
        };
        await using var c = await Ready(SessionStatus.Idle);
        c.Observe(ha.Press(SessionEntities.Stop(Node)));
        await Pass(c);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(HaStop.Ending, Activity);

        // A second press while it is ending is not a second end, then or once the first is done.
        c.Observe(ha.Press(SessionEntities.Stop(Node)));
        await Pass(c).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(stopped);

        release.SetResult(new StopResult(StopOutcome.Failed, "the process would not exit"));
        await c.DeliveredAsync();
        await Pass(c);
        await Pass(c, 20);
        await Pass(c, 20);
        Assert.Single(stopped);
        Assert.Equal(HaStop.Failed, Activity);
    }

    [Fact]
    public async Task A_press_made_while_the_host_was_still_deciding_does_not_confirm_the_question_it_then_asks()
    {
        var release = new TaskCompletionSource<StopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        outcome = _ =>
        {
            reached.TrySetResult();
            return release.Task;
        };
        await using var c = await Ready(SessionStatus.Working);
        c.Observe(ha.Press(SessionEntities.Stop(Node)));
        await Pass(c);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Pressed again before the card asked, and reported only once it has.
        var early = ha.Press(SessionEntities.Stop(Node));
        release.SetResult(new StopResult(StopOutcome.Confirm, "still working") { Confirm = "token-1", ConfirmSeconds = 10 });
        await c.DeliveredAsync();
        c.Observe(early);
        await Pass(c, 0.01);
        await Pass(c);
        Assert.Single(stopped);
        Assert.Equal(HaStop.ConfirmNote, Activity);

        outcome = null;
        await Press(c);
        Assert.Equal([null, "token-1"], stopped.Select(r => r.Confirm));
    }

    [Fact]
    public async Task A_second_press_made_the_moment_the_card_asks_is_taken()
    {
        await using var c = await Ready(SessionStatus.Working);
        // Pressed as soon as the question is on the card, before the connector has finished the pass.
        var t = HaContract.Topics(Node);
        var pressed = false;
        ha.BeforePublish = topic =>
        {
            if (!pressed && topic == t.ActivityAttributes && ha.Retained.GetValueOrDefault(t.ActivityState) == HaStop.ConfirmNote)
            {
                pressed = true;
                c.Observe(ha.Press(SessionEntities.Stop(Node)));
            }
        };
        await Press(c);
        Assert.True(pressed);
        await Pass(c);
        await c.DeliveredAsync();
        Assert.Equal([null, "token-1"], stopped.Select(r => r.Confirm));
    }

    [Fact]
    public async Task A_double_press_whose_second_lands_just_before_the_card_asks_does_not_end_the_session()
    {
        await using var c = await Ready(SessionStatus.Working);
        // After the connector has read the button, before the question reaches the card.
        var pressed = false;
        ha.BeforePublish = topic =>
        {
            if (!pressed && topic == HaContract.Topics(Node).ActivityState && stopped.Count == 1 && ha.Log.Count(l => l.Payload == HaStop.ConfirmNote) == 0)
            {
                pressed = true;
                c.Observe(ha.Press(SessionEntities.Stop(Node)));
            }
        };
        await Press(c);
        Assert.True(pressed);
        await Pass(c);
        await c.DeliveredAsync();
        Assert.Single(stopped);
        Assert.Equal(HaStop.ConfirmNote, Activity);
        Assert.Contains(log, m => m.Contains("came before its card asked", StringComparison.Ordinal));

        // A press made once it asks is the confirmation.
        ha.BeforePublish = null;
        await Press(c);
        Assert.Equal([null, "token-1"], stopped.Select(r => r.Confirm));
    }

    [Fact]
    public async Task A_question_the_host_answers_after_its_window_has_closed_is_never_put()
    {
        var release = new TaskCompletionSource<StopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        outcome = _ =>
        {
            reached.TrySetResult();
            return release.Task;
        };
        await using var c = await Ready(SessionStatus.Working);
        c.Observe(ha.Press(SessionEntities.Stop(Node)));
        await Pass(c);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Pass(c, 11);
        var mark = ha.Log.Count;

        release.SetResult(new StopResult(StopOutcome.Confirm, "still working") { Confirm = "token-1", ConfirmSeconds = 10 });
        await c.DeliveredAsync();
        await Pass(c, 0.01);
        Assert.DoesNotContain(NotesSince(mark), n => n.Summary == HaStop.ConfirmNote);

        outcome = null;
        await Press(c);
        Assert.Equal([null, null], stopped.Select(r => r.Confirm));
    }

    [Fact]
    public async Task A_window_that_closes_during_the_pass_is_not_offered()
    {
        var release = new TaskCompletionSource<StopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        outcome = _ =>
        {
            reached.TrySetResult();
            return release.Task;
        };
        await using var c = await Ready(SessionStatus.Working);
        c.Observe(ha.Press(SessionEntities.Stop(Node)));
        await Pass(c);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await Pass(c, 9);
        var mark = ha.Log.Count;

        // The pass starts inside the window; publishing the session's news takes it past the end.
        release.SetResult(new StopResult(StopOutcome.Confirm, "still working") { Confirm = "token-1", ConfirmSeconds = 10 });
        await c.DeliveredAsync();
        sessions.Set(sessions.Get(A)! with { Activity = sessions.Get(A)!.Activity with { Response = "Slow news" } });
        ha.PublishFailsFor = (topic, payload) =>
        {
            if (topic == HaContract.Topics(Node).ActivityState && payload == "Slow news") { time.Advance(TimeSpan.FromSeconds(2)); }
            return false;
        };
        await Pass(c, 0.5);
        Assert.DoesNotContain(NotesSince(mark), n => n.Summary == HaStop.ConfirmNote);
    }

    [Fact]
    public async Task A_slow_pass_before_the_press_goes_to_the_host_does_not_use_up_the_window()
    {
        await using var c = await Ready(SessionStatus.Working);
        // The pass that reads the press first publishes the session's news, slowly.
        sessions.Set(sessions.Get(A)! with { Activity = sessions.Get(A)!.Activity with { Response = "Slow news" } });
        ha.PublishFailsFor = (topic, payload) =>
        {
            if (topic == HaContract.Topics(Node).ActivityState && payload == "Slow news") { time.Advance(TimeSpan.FromSeconds(11)); }
            return false;
        };
        await Press(c);
        Assert.Equal(HaStop.ConfirmNote, Activity);
    }

    [Fact]
    public async Task A_notice_that_replaces_the_question_on_the_card_disarms_it()
    {
        Status(SessionStatus.Waiting);
        var go = new Field { Name = "go", Title = "Go on?", Type = FieldType.Choice, Required = true, Options = [new Option("yes", "Yes"), new Option("no", "No")] };
        sessions.Set(sessions.Get(A)! with { Question = new Question { Id = "q1", Kind = QuestionKind.Form, Message = "Go on?", AskedAt = FakeSessions.T0, Fields = [go] } });
        await using var c = Connector();
        await Pass(c);
        await Pass(c);
        await Press(c);
        Assert.Equal(HaStop.ConfirmNote, Activity);

        c.Observe(ha.Choose(SessionEntities.Field(Node, 1), "Yes"));
        await Pass(c);
        c.Observe(ha.Press(SessionEntities.Submit(Node)));
        await Pass(c);
        await c.DeliveredAsync();
        await Pass(c, 0.01);
        Assert.Equal("Answer sent", Activity);

        // The card shows the answer, not the question: the next press asks afresh.
        await Press(c);
        Assert.Equal([null, null], stopped.Select(r => r.Confirm));
        Assert.Equal(HaStop.ConfirmNote, Activity);
    }

    [Fact]
    public async Task A_notice_only_half_published_over_the_question_still_disarms_it()
    {
        Status(SessionStatus.Waiting);
        var go = new Field { Name = "go", Title = "Go on?", Type = FieldType.Choice, Required = true, Options = [new Option("yes", "Yes"), new Option("no", "No")] };
        sessions.Set(sessions.Get(A)! with { Question = new Question { Id = "q1", Kind = QuestionKind.Form, Message = "Go on?", AskedAt = FakeSessions.T0, Fields = [go] } });
        await using var c = Connector();
        await Pass(c);
        await Pass(c);
        await Press(c);

        // The answer's notice reaches the card; its attributes do not.
        ha.PublishFailsFor = (topic, payload) => topic == HaContract.Topics(Node).ActivityAttributes && payload.Contains("\"answer\"", StringComparison.Ordinal);
        c.Observe(ha.Choose(SessionEntities.Field(Node, 1), "Yes"));
        await Pass(c);
        c.Observe(ha.Press(SessionEntities.Submit(Node)));
        await Pass(c);
        await c.DeliveredAsync();
        await Pass(c, 0.01);
        Assert.NotEqual(HaStop.ConfirmNote, Activity);

        ha.PublishFailsFor = null;
        await Press(c);
        Assert.Equal([null, null], stopped.Select(r => r.Confirm));
    }

    [Fact]
    public async Task A_second_press_is_read_within_the_window_even_with_no_report_of_it()
    {
        await using var c = await Ready(SessionStatus.Working);
        await Press(c);

        // The watch is down: nothing reports the press, and the sweep comes every 15 s.
        ha.Press(SessionEntities.Stop(Node));
        await Pass(c, 2);
        await c.DeliveredAsync();
        Assert.Equal([null, "token-1"], stopped.Select(r => r.Confirm));
    }

    [Fact]
    public async Task A_confirmed_end_that_outlasts_the_window_is_never_said_to_have_lapsed()
    {
        await using var c = await Ready(SessionStatus.Working);
        await Press(c);
        var release = new TaskCompletionSource<StopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        outcome = _ =>
        {
            reached.TrySetResult();
            return release.Task;
        };
        var mark = ha.Log.Count;
        c.Observe(ha.Press(SessionEntities.Stop(Node)));
        await Pass(c);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("token-1", stopped[1].Confirm);

        // The host gives the session time to exit, past the confirmation's 10 s.
        await Pass(c, 6);
        sessions.Set(sessions.Get(A)! with { Activity = sessions.Get(A)!.Activity with { Response = "Still exiting" } });
        await Pass(c, 6);
        Assert.DoesNotContain(NotesSince(mark), n => n.Summary is HaStop.LapsedNote or HaStop.ConfirmNote);
        Assert.NotEqual(HaStop.ConfirmNote, Activity);

        sessions.End(A);
        release.SetResult(new StopResult(StopOutcome.Ended));
        await c.DeliveredAsync();
        await Pass(c, 0.01);
        Assert.Contains("ended", StatusesSince(mark));
    }

    [Fact]
    public async Task Without_the_hosts_stop_delivery_end_session_is_withdrawn_not_offered()
    {
        Status(SessionStatus.Idle);
        await using var c = Connector(stops: false);
        await Pass(c);
        Assert.Contains(ha.Log, p => p.Topic == HaContract.DiscoveryTopic("button", Node, "stop") && p.Payload.Length == 0);
        Assert.False(ha.Exists(SessionEntities.Stop(Node)));
    }
}
