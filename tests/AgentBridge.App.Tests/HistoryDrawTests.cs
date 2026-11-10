using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Xunit;

// What the conversation pane draws from the stored turns (#292 W2a, ui.md 6.4 History).
public sealed class HistoryDrawTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 9, 8, 31, 0, TimeSpan.Zero);

    static HistoryTurn Turn(int n, string? response = null, bool truncated = false, bool unreadable = false, UserMessageFrom? from = null, DateTimeOffset? replyAt = null) => new()
    {
        Id = $"t{n}",
        At = T0.AddMinutes(n),
        Prompt = unreadable ? null : new HistoryPrompt { Text = $"prompt {n}", From = from },
        Parts = response is null ? [] : [new HistoryPart { Id = $"p{n}", Kind = HistoryPartKind.Response, At = replyAt ?? T0.AddMinutes(n), Text = response }],
        Truncated = truncated ? true : null,
        Unreadable = unreadable ? true : null,
    };

    [Fact]
    public void Turns_are_drawn_oldest_to_newest()
    {
        var rows = HistoryDraw.Rows([Turn(1, "one"), Turn(2, "two"), Turn(3, "three")], null);

        Assert.Equal(["t1", "t2", "t3"], rows.Select(r => r.Id));
        Assert.Equal("prompt 1", rows[0].Prompt);
        Assert.Single(rows[0].Blocks);
    }

    [Fact]
    public void The_latest_exchange_is_not_drawn_twice()
    {
        var rows = HistoryDraw.Rows([Turn(1, "one"), Turn(2, "two")], "prompt 2");

        Assert.Equal(["t1"], rows.Select(r => r.Id));
    }

    static HistoryPart Asked(string id, QuestionOutcome? outcome = null) => new()
    {
        Id = id, Kind = HistoryPartKind.Question, At = T0,
        Question = Make.Choice() with { Id = id }, Outcome = outcome,
    };

    [Theory]
    [InlineData(QuestionOutcome.Answered)]
    [InlineData(QuestionOutcome.Approved)]
    [InlineData(QuestionOutcome.Denied)]
    [InlineData(QuestionOutcome.Cancelled)]
    [InlineData(QuestionOutcome.Unknown)]
    [InlineData(null)]
    public void Reloaded_questions_stay_between_the_responses_where_asked_without_claiming_who_answered(QuestionOutcome? outcome)
    {
        var turn = Turn(1, "before");
        turn = turn with { Parts = [turn.Parts[0], Asked("q1", outcome) with { Answer = "PostgreSQL" }, turn.Parts[0] with { Id = "after", Text = "after" }] };
        var kept = new Dictionary<string, ClosedQuestion>();

        var row = Assert.Single(HistoryDraw.Rows([turn], "prompt 1", closed: kept));

        Assert.Equal("before", row.Items[0].Response!.Text);
        Assert.Equal("Question closed", row.Items[1].Question!.Summary);
        Assert.Equal("after", row.Items[2].Response!.Text);
        Assert.Equal("q1", row.Items[1].Question!.Question.Id);
    }

    [Fact]
    public void The_current_question_is_not_also_a_closed_record_and_closure_keeps_its_disclosure_open_on_redraw()
    {
        var turn = Turn(1) with { Parts = [Asked("q1"), Asked("q2")] };
        var kept = new Dictionary<string, ClosedQuestion>();
        var first = Assert.Single(HistoryDraw.Rows([turn], null, closed: kept, activeQuestion: "q2"));
        var closed = Assert.Single(first.Items).Question!;
        closed.Open = true;

        var next = Assert.Single(HistoryDraw.Rows([turn], null, closed: kept));

        Assert.Same(closed, next.Items[0].Question);
        Assert.True(next.Items[0].Question!.Open);
        Assert.False(next.Items[1].Question!.Open);
    }

    [Fact]
    public void Only_the_latest_twenty_questions_are_kept_across_turns_in_record_order_not_clock_order()
    {
        var turns = Enumerable.Range(1, 25).Select(n => Turn(n, "reply") with
        {
            Parts = [Asked($"q{n}") with { At = T0.AddMinutes(-n) }],
        }).ToArray();
        var kept = new Dictionary<string, ClosedQuestion>();

        var rows = HistoryDraw.Rows(turns, null, closed: kept);

        Assert.Equal(Enumerable.Range(6, 20).Select(n => $"q{n}"), rows.SelectMany(r => r.Items).Select(i => i.Question!.Question.Id));
        Assert.Equal(20, kept.Count);
    }

    [Fact]
    public void A_question_part_without_a_question_is_skipped()
    {
        var turn = Turn(1) with { Parts = [Asked("missing") with { Question = null }, Asked("q1")] };

        var row = Assert.Single(HistoryDraw.Rows([turn], null, closed: new Dictionary<string, ClosedQuestion>()));

        Assert.Equal("q1", Assert.Single(row.Items).Question!.Question.Id);
    }

    [Fact]
    public void Only_the_newest_five_hundred_turns_are_drawn()
    {
        var turns = Enumerable.Range(1, HistoryLimits.Kept + 30).Select(n => Turn(n, "x")).ToList();

        var rows = HistoryDraw.Rows(turns, null);

        Assert.Equal(HistoryLimits.Kept, rows.Count);
        Assert.Equal("t31", rows[0].Id);
        Assert.Equal($"t{HistoryLimits.Kept + 30}", rows[^1].Id);
    }

    [Fact]
    public void A_turn_that_could_not_be_read_says_so()
    {
        var row = Assert.Single(HistoryDraw.Rows([Turn(1, unreadable: true)], null));

        Assert.Equal("A turn here couldn't be read", row.Note);
        Assert.Null(row.Prompt);
    }

    [Fact]
    public void A_cut_short_turn_ends_with_the_machine_note()
    {
        var row = Assert.Single(HistoryDraw.Rows([Turn(1, "text", truncated: true)], null));

        Assert.Equal("Cut short · see the machine", row.Note);
    }

    [Fact]
    public void A_prompt_the_person_did_not_type_is_collapsed_as_the_live_one_is()
    {
        var row = Assert.Single(HistoryDraw.Rows([Turn(1, "x", from: UserMessageFrom.Agent)], null));

        var d = Assert.IsType<HistoryDisclosure>(row.Disclosure);
        Assert.Equal("Sent by an agent", d.Origin.Label);
        Assert.Equal("prompt 1", d.Origin.Preview);
        Assert.False(d.Open);
        Assert.Equal("Prompt sent by an agent, Show prompt", d.ToggleName);
        Assert.Equal("Show prompt", d.ToggleText);

        d.Open = true;

        Assert.Equal("Prompt sent by an agent, Hide prompt", d.ToggleName);
        Assert.Equal("Hide prompt", d.ToggleText);
    }

    [Theory]
    [InlineData(UserMessageFrom.Schedule, "Scheduled prompt")]
    [InlineData(UserMessageFrom.Other, "Automatic prompt")]
    public void A_schedules_or_other_prompt_is_collapsed_too(UserMessageFrom from, string label)
    {
        var row = Assert.Single(HistoryDraw.Rows([Turn(1, "x", from: from)], null));

        Assert.Equal(label, row.Disclosure!.Origin.Label);
    }

    [Fact]
    public void The_persons_prompt_has_no_disclosure_and_is_drawn_as_text()
    {
        var row = Assert.Single(HistoryDraw.Rows([Turn(1, "x"), Turn(2, "y", from: UserMessageFrom.Person)], null).Take(1));

        Assert.Null(row.Disclosure);
        Assert.Equal("prompt 1", row.Prompt);
        Assert.Null(HistoryDraw.Rows([Turn(2, "y", from: UserMessageFrom.Person)], null)[0].Disclosure);
    }

    [Fact]
    public void A_turn_drawn_again_keeps_its_prompt_open()
    {
        var turns = new[] { Turn(1, "x", from: UserMessageFrom.Agent) };
        var kept = new Dictionary<string, HistoryDisclosure>();
        HistoryDraw.Rows(turns, null, kept)[0].Disclosure!.Open = true;

        Assert.True(HistoryDraw.Rows(turns, null, kept)[0].Disclosure!.Open);
    }
    [Theory]
    [InlineData(HistoryLoad.Loading, 0, "Loading conversation…")]
    [InlineData(HistoryLoad.Loading, 3, null)]
    [InlineData(HistoryLoad.Failed, 3, "Couldn't load earlier messages")]
    [InlineData(HistoryLoad.NoHistory, 3, "Earlier messages aren't available from this machine")]
    public void The_line_above_the_turns_uses_the_designed_words(HistoryLoad state, int drawn, string? expected)
    {
        Assert.Equal(expected, HistoryDraw.Line(state, drawn, hasLivePrompt: true, start: null));
    }

    [Fact]
    public void A_session_with_nothing_yet_says_no_messages()
    {
        Assert.Equal("No messages yet", HistoryDraw.Line(HistoryLoad.Idle, 0, hasLivePrompt: false, start: null));
        Assert.Null(HistoryDraw.Line(HistoryLoad.Idle, 0, hasLivePrompt: true, start: null));
    }

    [Fact]
    public void The_start_of_the_history_reads_as_designed()
    {
        Assert.Equal("Start of available history", HistoryDraw.Line(HistoryLoad.StartReached, 5, true, new HistoryStart { Limited = true }));
        var line = HistoryDraw.Line(HistoryLoad.StartReached, 5, true, new HistoryStart { At = T0 });
        Assert.StartsWith("Start of session · ", line);
    }

    [Fact]
    public void A_turns_prompt_and_reply_carry_their_own_sent_times()
    {
        var clock = new MessageClock(T0.AddMinutes(30), TimeZoneInfo.Utc, System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        var turn = Turn(1, "one", replyAt: T0.AddMinutes(5));

        var row = Assert.Single(HistoryDraw.Rows([turn], null, clock: clock));

        Assert.Equal("29 min ago", row.Times!.Prompt!.Text);
        Assert.Equal("25 min ago", row.Times.Reply!.Text);
        Assert.StartsWith("Mon Nov 9, 2026, ", row.Times.Prompt.Exact);
    }

    [Fact]
    public void A_person_prompt_has_its_time_under_it_and_any_other_beside_its_label()
    {
        var clock = new MessageClock(T0.AddMinutes(30), TimeZoneInfo.Utc, System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        var mine = Assert.Single(HistoryDraw.Rows([Turn(1, "x")], null, clock: clock));
        var scheduled = Assert.Single(HistoryDraw.Rows([Turn(1, "x", from: UserMessageFrom.Schedule)], null, clock: clock));

        Assert.NotNull(mine.PromptUnder);
        Assert.Null(mine.PromptBeside);
        Assert.NotNull(scheduled.PromptBeside);
        Assert.Null(scheduled.PromptUnder);
    }

    [Fact]
    public void An_unreadable_turn_and_a_turn_with_no_reply_show_no_guessed_time()
    {
        var clock = new MessageClock(T0.AddMinutes(30), TimeZoneInfo.Utc, System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        var rows = HistoryDraw.Rows([Turn(1, unreadable: true), Turn(2)], null, clock: clock);

        Assert.Null(rows[0].Times);
        Assert.Null(rows[0].PromptUnder);
        Assert.Null(rows[0].Reply);
        Assert.NotNull(rows[1].Times!.Prompt);
        Assert.Null(rows[1].Times!.Reply);
    }

    [Fact]
    public void Time_never_changes_the_order_of_the_turns()
    {
        var late = Turn(1, "a", replyAt: T0.AddDays(3));
        var rows = HistoryDraw.Rows([late, Turn(2, "b")], null);

        Assert.Equal(["t1", "t2"], rows.Select(r => r.Id));
    }

    [Fact]
    public void Turns_held_are_still_drawn_when_a_page_failed_or_the_machine_is_away()
    {
        var rows = HistoryDraw.Rows([Turn(1, "one")], null);

        Assert.Single(rows);
        Assert.Equal("Couldn't load earlier messages", HistoryDraw.Line(HistoryLoad.Failed, rows.Count, false, null));
    }
}
