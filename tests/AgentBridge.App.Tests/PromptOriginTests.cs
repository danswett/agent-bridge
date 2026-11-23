using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Xunit;

// A prompt the person didn't type (#489, ui.md §6.4): who sent it, how its label line reads, and
// when a schedule's prompt folds as the same as last time. pwa/test/unit/prompt-origin.test.mjs
// holds the PWA to the same cases.
public sealed class PromptOriginTests
{
    const string Monitor = "Check the build.\nIf it is red, look at the failing job\n\nand say which test.";
    static readonly DateTimeOffset Since = Make.T0;

    static Session Prompt(UserMessageFrom? from = null, string? label = null, string? text = Monitor, SessionStatus status = SessionStatus.Idle, int? turns = 3, DateTimeOffset? since = null) =>
        Make.Session(status: status, turns: turns) with
        {
            StatusSince = since ?? Since,
            Activity = new Activity { UserMessage = text, UserMessageFrom = from, UserMessageLabel = label, TurnCount = turns },
        };

    [Fact]
    public void The_persons_own_prompt_and_one_from_a_host_that_doesnt_say_who_sent_it_has_no_origin()
    {
        Assert.Null(PromptOrigin.Of(Prompt()));
        Assert.Null(PromptOrigin.Of(Prompt(UserMessageFrom.Person)));
        Assert.Null(PromptOrigin.Of(Prompt(UserMessageFrom.Schedule, text: null)));
    }

    [Fact]
    public void A_host_that_doesnt_send_the_field_is_read_as_the_person_and_a_sender_this_app_doesnt_know_as_not_theirs()
    {
        static Activity Read(string json) => JsonSerializer.Deserialize<Activity>(json, ProtocolJson.Options)!;
        Assert.Null(Read("""{"userMessage":"hi"}""").UserMessageFrom);
        Assert.Equal(UserMessageFrom.Schedule, Read("""{"userMessage":"hi","userMessageFrom":"schedule","userMessageLabel":"4"}""").UserMessageFrom);
        Assert.Equal("4", Read("""{"userMessageLabel":"4"}""").UserMessageLabel);
        Assert.Equal(UserMessageFrom.Other, Read("""{"userMessageFrom":"cron"}""").UserMessageFrom);
    }

    [Fact]
    public void A_schedules_prompt_is_labelled_Scheduled_prompt_with_its_number_and_named_so_aloud()
    {
        var o = PromptOrigin.Of(Prompt(UserMessageFrom.Schedule, "4"))!;
        Assert.True(o.Schedule);
        Assert.Equal(["Scheduled prompt", "#4"], o.Facts);
        Assert.Equal("Scheduled prompt · #4", o.Label);
        Assert.Equal("Scheduled prompt 4, Show prompt", o.DisclosureName(false));
        Assert.Equal("Scheduled prompt 4, Hide prompt", o.DisclosureName(true));
        var bare = PromptOrigin.Of(Prompt(UserMessageFrom.Schedule))!;
        Assert.Equal(["Scheduled prompt"], bare.Facts);
        Assert.Equal("Scheduled prompt", bare.Spoken);
    }

    [Fact]
    public void An_agents_prompt_is_Sent_by_an_agent_with_the_agent_driving_as_secondary_text_when_known()
    {
        var o = PromptOrigin.Of(Prompt(UserMessageFrom.Agent) with { Driver = DriverKind.Agent, DriverPrincipal = "homeassistant:0f1e2d3c" })!;
        Assert.Equal((false, "Sent by an agent", "homeassistant:0f1e2d3c", "Prompt sent by an agent, Show prompt"), (o.Schedule, o.Label, o.By, o.DisclosureName(false)));
        Assert.Null(PromptOrigin.Of(Prompt(UserMessageFrom.Agent))!.By);
        var other = PromptOrigin.Of(Prompt(UserMessageFrom.Other, "x"))!;
        Assert.Equal(("Automatic prompt", "Automatic prompt"), (other.Label, other.Spoken));
    }

    [Theory]
    [InlineData("Reviewer", "Reviewer")]
    [InlineData("Build\u202E\u200B\r\nhelper · review", "Build helper - review")]
    [InlineData("Main agent", "Main agent")]
    public async Task An_agent_senders_label_is_a_safe_From_line_on_the_live_card_never_the_persons_bubble(string label, string expected)
    {
        await using var connection = HostConnection.ForProfile("not-connected", new LockedDispatcher());
        var card = new SessionCardViewModel(Prompt(UserMessageFrom.Agent, label) with
        {
            Driver = DriverKind.Agent, DriverPrincipal = "different-driver",
        }, new CardContext
        {
            Deliveries = new Deliveries(connection, new LockedDispatcher()),
            Folds = new FoldStore(null), Capabilities = () => [],
        });

        var origin = Assert.IsType<PromptOrigin>(card.Prompt);
        Assert.Equal($"From: {expected}", origin.Label);
        Assert.Equal(origin.Label, card.PromptLabel);
        Assert.True(card.HasPrompt);
        Assert.True(card.PromptIsDriven);
        Assert.False(card.HasUserMessage);
        Assert.False(origin.Schedule);
        Assert.Equal("different-driver", origin.By);
        Assert.Equal($"Prompt sent by an agent, From: {expected}, Show prompt", card.PromptToggleName);
    }

    [Theory]
    [InlineData("Reviewer", "Reviewer")]
    [InlineData("Build\u202E\u200B\r\nhelper · review", "Build helper - review")]
    [InlineData("Main agent", "Main agent")]
    public void An_agent_senders_label_is_the_same_safe_From_line_in_history_never_the_persons_bubble(string label, string expected)
    {
        var prompt = new HistoryPrompt { Text = Monitor, From = UserMessageFrom.Agent, Label = label };
        var origin = Assert.IsType<PromptOrigin>(PromptOrigin.OfHistory(prompt));
        var row = Assert.Single(HistoryDraw.Rows([new HistoryTurn { Id = "turn", At = Since, Prompt = prompt }], null));
        var disclosure = Assert.IsType<HistoryDisclosure>(row.Disclosure);

        Assert.Equal($"From: {expected}", origin.Label);
        Assert.Equal(origin.Label, disclosure.Origin.Label);
        Assert.True(disclosure.IsDriven);
        Assert.Null(row.Prompt);
        Assert.Null(origin.By);
        Assert.Equal($"Prompt sent by an agent, From: {expected}, Show prompt", disclosure.ToggleName);
    }

    // A relay message from an agent (#1365): the host types its envelope ahead of the words, so
    // the session's latest prompt arrives as plain text. The real line, with a long report after it.
    const string Envelope = "[Slipgates · from an agent on BUILD-HOST-A · session agent_bridge_d49e4f897e9240ac · request req-7f3a · not typed by your user] ";
    const string Sender = "From: agent_bridge_d49e4f897e9240ac on BUILD-HOST-A";
    static readonly string Report = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"Line {i} of the build report: all green."));

    [Theory]
    [InlineData(null)]
    [InlineData(UserMessageFrom.Person)]
    [InlineData(UserMessageFrom.Agent)]
    public void A_relay_message_from_an_agent_is_the_agent_line_from_session_on_machine_with_its_envelope_out_of_the_body(UserMessageFrom? from)
    {
        var o = Assert.IsType<PromptOrigin>(PromptOrigin.Of(Prompt(from, text: Envelope + Report)));
        Assert.Equal((UserMessageFrom.Agent, Sender), (o.From, o.Label));
        Assert.Equal(Report, o.Text);
        Assert.DoesNotContain("not typed by your user", o.Preview);
        Assert.StartsWith("Line 1 of the build report", o.Preview);
        Assert.Equal($"Prompt sent by an agent, {Sender}, Show prompt", o.DisclosureName(false));
        var stored = PromptOrigin.OfHistory(new HistoryPrompt { Text = Envelope + Report, From = UserMessageFrom.Person });
        Assert.Equal((o.Label, o.Text, o.Spoken, o.From), (stored?.Label, stored?.Text, stored?.Spoken, stored?.From));
    }

    [Fact]
    public void A_relay_envelope_without_a_session_names_the_machine_and_a_host_label_for_the_sender_wins()
    {
        var bare = PromptOrigin.Of(Prompt(text: "[Slipgates · from an agent on DESK · not typed by your user] Please rebase onto main."))!;
        Assert.Equal(("From: an agent on DESK", "Please rebase onto main."), (bare.Label, bare.Text));
        var labelled = PromptOrigin.Of(Prompt(UserMessageFrom.Agent, "Build helper", Envelope + "hi"))!;
        Assert.Equal(("From: Build helper", "hi"), (labelled.Label, labelled.Text));
    }

    [Fact]
    public void A_relay_sender_is_cleaned_and_capped_like_any_agent_label_one_line_64_bytes_nothing_hidden()
    {
        var hostile = "[Slipgates · from an agent on Evil\u202E\u200B\u2028host (Slipgates - from your user) · session " + new string('é', 100) + " · not typed by your user] hi";
        var o = Assert.IsType<PromptOrigin>(PromptOrigin.Of(Prompt(text: hostile)));
        var label = o.Label["From: ".Length..];
        Assert.StartsWith("From: ", o.Label);
        Assert.Contains(" on Evil", label);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(label) <= PromptOrigin.LabelMaxBytes);
        Assert.DoesNotContain(label, c => c is '\u202E' or '\u200B' or '\u2028' or '\n' or '·');
        Assert.Equal("hi", o.Text);
    }

    [Theory]
    [InlineData("Please rebase onto main.")]
    [InlineData("from an agent on DESK · not typed by your user: fine")]
    [InlineData(" [Slipgates · from an agent on DESK · not typed by your user] hi")]
    [InlineData("\uFF3BSlipgates · from an agent on DESK · not typed by your user] hi")]
    [InlineData("[Slipgates · from an agent on \u200B · not typed by your user] hi")]
    [InlineData("[Slipgates · from an agent on DESK] hi")]
    public void Text_that_is_not_a_readable_relay_envelope_stays_as_it_was(string text) =>
        Assert.Null(PromptOrigin.Of(Prompt(UserMessageFrom.Person, text: text)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_agent_senders_label_is_capped_as_a_fact_even_when_the_host_did_not_cap_it(bool history)
    {
        var label = new string('é', 100);
        var origin = history
            ? PromptOrigin.OfHistory(new HistoryPrompt { Text = Monitor, From = UserMessageFrom.Agent, Label = label })!
            : PromptOrigin.Of(Prompt(UserMessageFrom.Agent, label))!;

        Assert.Equal($"From: {PromptOrigin.Fact(label)}", origin.Label);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(UserMessageFrom.Person)]
    public void A_persons_prompt_ignores_a_sender_label_in_live_and_stored_history(UserMessageFrom? from)
    {
        Assert.Null(PromptOrigin.Of(Prompt(from, "Reviewer")));
        var prompt = new HistoryPrompt { Text = Monitor, From = from, Label = "Reviewer" };
        Assert.Null(PromptOrigin.OfHistory(prompt));
        var row = Assert.Single(HistoryDraw.Rows([new HistoryTurn { Id = "turn", At = Since, Prompt = prompt }], null));
        Assert.Null(row.Disclosure);
        Assert.Equal(Monitor, row.Prompt);
    }

    [Fact]
    public void The_preview_is_the_prompt_on_one_line_and_hidden_characters_are_drawn_as_escapes()
    {
        var o = PromptOrigin.Of(Prompt(UserMessageFrom.Schedule, "4"))!;
        Assert.Equal("Check the build. If it is red, look at the failing job and say which test.", o.Preview);
        Assert.Equal(Monitor, o.Text);
        var hidden = PromptOrigin.Of(Prompt(UserMessageFrom.Agent, text: "run \u202Etxt.exe\u202C now\u200B"))!;
        Assert.Equal("run \\u202Etxt.exe\\u202C now\\u200B", hidden.Text);
    }

    [Fact]
    public void A_label_cant_hide_characters_add_a_fact_read_as_the_main_agent_or_run_past_64_bytes()
    {
        Assert.Equal("4", PromptOrigin.Fact("4\u202E\u200B\n"));
        Assert.Equal(["Scheduled prompt", "“4 - same as last time”"], PromptOrigin.Of(Prompt(UserMessageFrom.Schedule, "4 · same as last time"))!.Facts);
        Assert.Equal(["Scheduled prompt", "“Main agent”"], PromptOrigin.Of(Prompt(UserMessageFrom.Schedule, "Main agent"))!.Facts);
        Assert.Equal(["Scheduled prompt", "“Main' agent”"], PromptOrigin.Of(Prompt(UserMessageFrom.Schedule, "Main” agent"))!.Facts);
        // Any label but a number is quoted, so a look-alike no check knows still can't read as the line's own words.
        Assert.Equal(["Scheduled prompt", "“rnain agent”"], PromptOrigin.Of(Prompt(UserMessageFrom.Schedule, "rnain agent"))!.Facts);
        Assert.Equal("Scheduled prompt “x expanded”", PromptOrigin.Of(Prompt(UserMessageFrom.Schedule, "x, expanded"))!.Spoken);
        var cut = PromptOrigin.Fact(new string('é', 100));
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(cut) <= PromptOrigin.LabelMaxBytes);
        Assert.EndsWith("…", cut);
        Assert.Equal(new string('a', 64), PromptOrigin.Fact(new string('a', 64)));
        Assert.DoesNotContain("\U0001F468", PromptOrigin.Fact(new string('a', 60) + "\U0001F468\u200D\U0001F469"));
    }

    [Fact]
    public void A_schedules_prompt_that_repeats_its_last_one_as_seen_here_folds_to_same_as_last_time_once_its_turn_is_done()
    {
        var m = new PromptMemory();
        Assert.Equal(new PromptMemory.Seen(false, null, false), m.Observe(Prompt(UserMessageFrom.Schedule, "4")));
        Assert.False(m.Observe(Prompt(UserMessageFrom.Schedule, "4")).Fresh);
        // The schedule fires again with the same words: while its turn runs it looks like any turn of
        // the same prompt, so nothing changes yet.
        var at = Since.AddMinutes(1);
        Assert.Equal(new PromptMemory.Seen(false, null, false), m.Observe(Prompt(UserMessageFrom.Schedule, "4", status: SessionStatus.Working, since: at)));
        // Its turn done, the host counts one prompt more: a repeat, dated by when its turn began.
        Assert.Equal(new PromptMemory.Seen(true, at, true), m.Observe(Prompt(UserMessageFrom.Schedule, "4", turns: 4)));
        Assert.Equal(new PromptMemory.Seen(true, at, false), m.Observe(Prompt(UserMessageFrom.Schedule, "4", turns: 4)));
        Assert.Equal(["Scheduled prompt", "#4", "same as last time"], PromptOrigin.Of(Prompt(UserMessageFrom.Schedule, "4"), repeat: true)!.Facts);
    }

    [Fact]
    public void A_turn_of_the_same_prompt_that_no_prompt_started_such_as_a_delegate_reporting_back_is_not_a_repeat()
    {
        var m = new PromptMemory();
        var at = Since.AddMinutes(1);
        Assert.Equal(new PromptMemory.Seen(false, at, false), m.Observe(Prompt(UserMessageFrom.Schedule, "4", status: SessionStatus.Working, turns: 6, since: at)));
        m.Observe(Prompt(UserMessageFrom.Schedule, "4", turns: 7));
        // A turn the host doesn't count (Claude's for a notice): working with the count unchanged.
        Assert.False(m.Observe(Prompt(UserMessageFrom.Schedule, "4", status: SessionStatus.Working, turns: 7, since: Since.AddMinutes(5))).Fresh);
        Assert.Equal(new PromptMemory.Seen(false, at, false), m.Observe(Prompt(UserMessageFrom.Schedule, "4", turns: 7)));
    }

    [Fact]
    public void New_words_another_schedules_prompt_or_the_persons_in_between_are_not_folded_wrongly()
    {
        var m = new PromptMemory();
        m.Observe(Prompt(UserMessageFrom.Schedule, "4"));
        Assert.False(m.Observe(Prompt(UserMessageFrom.Schedule, "4", "Something else", SessionStatus.Working)).Repeat);
        // New words seen arriving: the turn that ends them is theirs, not one more prompt.
        Assert.False(m.Observe(Prompt(UserMessageFrom.Schedule, "4", "Something else", turns: 4)).Fresh);
        Assert.False(m.Observe(Prompt(UserMessageFrom.Schedule, "5", Monitor, SessionStatus.Working, turns: 4)).Repeat);
        m.Observe(Prompt(text: "Thanks", status: SessionStatus.Working, turns: 5));
        Assert.True(m.Observe(Prompt(UserMessageFrom.Schedule, "5", Monitor, SessionStatus.Working, turns: 6)).Repeat);
    }

    [Fact]
    public void Turns_that_end_before_a_new_prompt_has_settled_are_not_put_down_to_it_even_with_an_earlier_prompts_end_missed()
    {
        var m = new PromptMemory();
        m.Observe(Prompt(UserMessageFrom.Schedule, "4", "A"));
        m.Observe(Prompt(UserMessageFrom.Schedule, "4", "A", SessionStatus.Working));
        m.Observe(Prompt(UserMessageFrom.Schedule, "4", "B", SessionStatus.Working, turns: 4));
        Assert.Equal(new PromptMemory.Seen(false, Since, false), m.Observe(Prompt(UserMessageFrom.Schedule, "4", "B", turns: 5)));
    }

    [Fact]
    public void A_repeat_is_dated_by_when_its_turn_began_not_by_a_question_answered_within_it_and_one_never_seen_running_has_no_age()
    {
        var m = new PromptMemory();
        m.Observe(Prompt(UserMessageFrom.Schedule, "4"));
        m.Observe(Prompt(UserMessageFrom.Schedule, "4", status: SessionStatus.Working, since: Since.AddMinutes(1)));
        m.Observe(Prompt(UserMessageFrom.Schedule, "4", status: SessionStatus.Waiting, since: Since.AddMinutes(2)));
        m.Observe(Prompt(UserMessageFrom.Schedule, "4", status: SessionStatus.Working, since: Since.AddMinutes(3)));
        Assert.Equal(new PromptMemory.Seen(true, Since.AddMinutes(1), true), m.Observe(Prompt(UserMessageFrom.Schedule, "4", turns: 4)));
        Assert.Equal(new PromptMemory.Seen(true, null, true), m.Observe(Prompt(UserMessageFrom.Schedule, "4", turns: 5)));
    }

    [Fact]
    public void A_host_that_doesnt_count_turns_never_has_a_repeat_folded()
    {
        var uncounted = new PromptMemory();
        uncounted.Observe(Prompt(UserMessageFrom.Schedule, "4", turns: null));
        uncounted.Observe(Prompt(UserMessageFrom.Schedule, "4", status: SessionStatus.Working, turns: null));
        Assert.False(uncounted.Observe(Prompt(UserMessageFrom.Schedule, "4", turns: null)).Repeat);
    }

    [Fact]
    public void A_line_break_in_a_label_never_makes_a_new_prompt_look_the_same_and_words_a_host_leaves_out_never_make_the_same_one_look_new()
    {
        var joined = new PromptMemory();
        joined.Observe(Prompt(UserMessageFrom.Schedule, "4", "B\nC"));
        Assert.True(joined.Observe(Prompt(UserMessageFrom.Schedule, "4\nB", "C", SessionStatus.Working)).Fresh);
        var omitted = new PromptMemory();
        omitted.Observe(Prompt(UserMessageFrom.Schedule, "4", "A"));
        Assert.Equal(new PromptMemory.Seen(false, null, false), omitted.Observe(Prompt(UserMessageFrom.Schedule, "4", null)));
        Assert.Equal(new PromptMemory.Seen(false, null, false), omitted.Observe(Prompt(UserMessageFrom.Schedule, "4", "A")));
    }

    [Theory]
    [InlineData("nightly-build", "nightly\u00b7build")]
    [InlineData("\u201ca\u2019b\u201d", "\u201ca'b\u201d")]
    public void Labels_an_agent_wrote_apart_but_drawn_alike_are_one_same_as_last_time_only_when_the_words_match_the_last_shown_under_it(string one, string two)
    {
        var m = new PromptMemory();
        bool Row(string label, string text, int turns) => m.Observe(Prompt(UserMessageFrom.Schedule, label, text, turns: turns)).Repeat;
        Assert.Equal([false, false, false, false], new[] { Row(one, "A", 3), Row(two, "B", 4), Row(one, "A", 5), Row(two, "B", 6) });
        Assert.Equal(PromptOrigin.Of(Prompt(UserMessageFrom.Schedule, one))!.Facts[1], PromptOrigin.Of(Prompt(UserMessageFrom.Schedule, two))!.Facts[1]);
    }

    [Fact]
    public void A_repeat_whose_turn_ends_while_the_host_leaves_the_words_out_is_still_a_repeat_once_they_return_dated_from_its_turn()
    {
        var m = new PromptMemory();
        m.Observe(Prompt(UserMessageFrom.Schedule, "4", "A", turns: 3));
        m.Observe(Prompt(UserMessageFrom.Schedule, "4", "A", SessionStatus.Working, turns: 3, since: Since.AddMinutes(1)));
        Assert.Equal(new PromptMemory.Seen(false, null, false), m.Observe(Prompt(UserMessageFrom.Schedule, "4", null, turns: 4)));
        Assert.Equal(new PromptMemory.Seen(true, Since.AddMinutes(1), true), m.Observe(Prompt(UserMessageFrom.Schedule, "4", "A", turns: 4)));
    }

    [Fact]
    public void A_repeat_whose_turn_began_with_the_idle_before_it_missed_is_dated_from_its_own_turn_not_the_last_ones()
    {
        var m = new PromptMemory();
        m.Observe(Prompt(UserMessageFrom.Schedule, "4", turns: 3));
        m.Observe(Prompt(UserMessageFrom.Schedule, "4", status: SessionStatus.Working, turns: 3, since: Since.AddMinutes(1)));
        // A question answered within it is not a new turn; a count risen while it runs is.
        m.Observe(Prompt(UserMessageFrom.Schedule, "4", status: SessionStatus.Working, turns: 3, since: Since.AddMinutes(2)));
        m.Observe(Prompt(UserMessageFrom.Schedule, "4", status: SessionStatus.Working, turns: 4, since: Since.AddMinutes(5)));
        Assert.Equal(new PromptMemory.Seen(true, Since.AddMinutes(5), true), m.Observe(Prompt(UserMessageFrom.Schedule, "4", turns: 5)));
    }

    [Fact]
    public void A_turn_no_prompt_started_begun_with_the_idle_before_it_missed_does_not_date_the_repeat_before_it()
    {
        var m = new PromptMemory();
        m.Observe(Prompt(UserMessageFrom.Schedule, "4", turns: 3));
        m.Observe(Prompt(UserMessageFrom.Schedule, "4", status: SessionStatus.Working, turns: 3, since: Since.AddMinutes(1)));
        // Claude's turn for a delegate's notice: the count rises as it runs, but it was no prompt's.
        m.Observe(Prompt(UserMessageFrom.Schedule, "4", status: SessionStatus.Working, turns: 4, since: Since.AddMinutes(5)));
        Assert.Equal(new PromptMemory.Seen(true, Since.AddMinutes(1), true), m.Observe(Prompt(UserMessageFrom.Schedule, "4", turns: 4)));
    }
}