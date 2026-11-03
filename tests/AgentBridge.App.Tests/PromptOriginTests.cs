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
}