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
    public void A_permission_prompt_offers_approve_once_and_deny_and_one_held_at_the_machine_offers_open()
    {
        var approval = Xml(new ToastNotice("t", ToastXml.Question(Make.Session(question: Make.Approval()), Caps, "PC"), false));
        Assert.Equal(["Approve once", "Deny"], approval.Descendants("action").Select(a => (string)a.Attribute("content")!));
        Assert.Contains("PC · waiting for your approval", approval.Descendants("text").Select(t => t.Value));

        var held = Xml(new ToastNotice("t", ToastXml.Question(Make.Session(question: Make.Approval() with { AnswerAtMachine = true }), Caps, "PC"), false));
        Assert.Equal(["Open"], held.Descendants("action").Select(a => (string)a.Attribute("content")!));
        Assert.Contains("PC · waiting at the machine", held.Descendants("text").Select(t => t.Value));
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
    public async Task Answering_on_a_toast_sends_through_the_host_and_replaces_the_toast_with_the_outcome()
    {
        await using var host = new FakeHost();
        host.On(Ops.SessionAnswer, _ => new AnswerResult(AnswerOutcome.Stale));
        host.Start();
        await using var connection = host.Connect();
        var deliveries = new Deliveries(connection, new LockedDispatcher()) { RetryDelay = TimeSpan.FromMilliseconds(50) };
        string? opened = null;
        var responder = new ToastResponder(deliveries, sink, s => opened = s);

        await responder.HandleAsync(ToastXml.Args(("action", "answer"), ("session", "s1"), ("question", "q1"), ("kind", "option"), ("field", "answer"), ("value", "pg")), new Dictionary<string, string>());

        var outcome = Assert.Single(sink.Shown);
        Assert.Equal("s1~a", outcome.Tag);
        Assert.Contains("Already answered or gone: the session is no longer asking this.", Xml(outcome).Descendants("text").Select(t => t.Value));
        Assert.Equal("pg", host.Sent(Ops.SessionAnswer).Single().GetProperty("fields").GetProperty("answer").GetString());

        await responder.HandleAsync("action=open&session=s7", new Dictionary<string, string>());
        Assert.Equal("s7", opened);
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
