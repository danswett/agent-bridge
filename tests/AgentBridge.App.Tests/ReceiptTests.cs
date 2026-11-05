using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// The receipt's timing (ui.md §6.5, decided on #617, "quiet unless something's wrong"), on a fake
// clock: the quiet window, the dwell, an outcome at once, each stall, the waits, an older host,
// and the announcements, coalesced. The normal path's states have no words (Send's ring says
// them), so they are told apart by key and by what is said. pwa/test/unit/receipt.test.mjs holds
// the PWA's to the same.
public sealed class ReceiptTests
{
    readonly FakeTimeProvider clock = new(Make.T0);
    readonly List<string> drawn = [];
    readonly List<string> keys = [];
    readonly List<string> said = [];
    Delivery entry = new() { Key = "reply|s1", Request = Guid.NewGuid().ToString(), State = DeliveryState.Sending, Attempts = 1 };

    Receipt NewReceipt(bool answer = false) => new(clock, InlineDispatcher.Instance,
        (e, stalled) => e.State == DeliveryState.Done
            ? new ReceiptLine("delivered", "", null, Tone.None, ReceiptMark.Check, $"{(answer ? "Answer" : "Reply")} delivered")
            : ReceiptWords.InFlight(answer, e, "ULTRA", stalled),
        line => { drawn.Add(line.Whole); keys.Add(line.Key); }, (speech, _) => said.Add(speech));

    void Show(Receipt r, Func<Delivery, Delivery>? change = null)
    {
        if (change is not null) { entry = change(entry); }
        r.Show(entry);
    }

    void Tick(int ms) => clock.Advance(TimeSpan.FromMilliseconds(ms));
    string Shown => drawn[^1];
    string Key => keys[^1];

    // #625, Copilot 4478422027: the oldest of 50 histories went whatever showed it, so a card
    // refreshed after 50 other requests said "Answer delivered" again.
    [Fact]
    public void A_history_a_receipt_still_shows_is_kept_however_many_requests_come_after_it()
    {
        using var r = NewReceipt(answer: true);
        Show(r, e => e with { State = DeliveryState.Done, Outcome = "delivered" });
        Assert.Equal(["Answer delivered"], said);
        for (var i = 0; i < 120; i++)
        {
            using var other = NewReceipt();
            other.Show(new Delivery { Key = $"reply|other{i}", Request = Guid.NewGuid().ToString(), State = DeliveryState.Done, Outcome = "delivered", Attempts = 1 });
        }
        said.Clear();

        // The card made afresh shows the same delivery: nothing new to say.
        Show(r);

        Assert.Empty(said);
    }

    // #625, Copilot 4478422096: a failure or a refusal needs the person to act, so it is said
    // assertively (ui.md §9.1 A6); the stages before it and a delivery stay polite.
    [Theory]
    [InlineData(DeliveryState.Failed, null, true)]
    [InlineData(DeliveryState.Done, "refused", true)]
    [InlineData(DeliveryState.Done, "delivered", false)]
    public void Only_a_failure_or_a_refusal_is_announced_assertively(DeliveryState state, string? outcome, bool urgent)
    {
        var heard = new List<(string Speech, bool Urgent)>();
        using var r = new Receipt(clock, InlineDispatcher.Instance, (e, stalled) => QuestionViewModel.Describe(e, "ULTRA", stalled), _ => { }, (s, u) => heard.Add((s, u)));
        Show(r);
        Tick(1000);
        Show(r, e => e with { Stage = DeliveryProgress.Received });
        Tick(1000);

        Show(r, e => e with { State = state, Outcome = outcome, Reason = state == DeliveryState.Failed ? "the pipe broke" : null });
        Tick(1000);

        Assert.True(heard.Count >= 2, string.Join(" | ", heard));
        Assert.All(heard.SkipLast(1), h => Assert.False(h.Urgent, h.Speech));
        Assert.Equal(urgent, heard[^1].Urgent);
    }

    [Fact]
    public void Nothing_is_said_for_the_first_400_ms_then_the_furthest_stage_reached_so_a_fast_delivery_never_says_Sent()
    {
        using var r = NewReceipt();
        Show(r);
        Assert.Equal("quiet", Key);
        Tick(100);
        Show(r, e => e with { Stage = DeliveryProgress.Received });
        Tick(200);
        Show(r, e => e with { Stage = DeliveryProgress.Typed });
        Assert.Equal("quiet", Key);
        Assert.Empty(said);
        Tick(100);
        Assert.Equal("typed", Key);
        Assert.Equal(["Reply passed to the agent"], said);
        Assert.DoesNotContain("received", keys);
    }

    [Fact]
    public void The_normal_path_puts_no_words_on_the_line_and_a_screen_reader_still_hears_each_stage()
    {
        using var r = NewReceipt();
        Show(r);
        Tick(400);
        Show(r, e => e with { Stage = DeliveryProgress.Received });
        Tick(1000);
        Show(r, e => e with { Stage = Deliveries.Queued });
        Tick(1000);
        Show(r, e => e with { State = DeliveryState.Done, Outcome = "delivered" });
        Tick(1000);
        Assert.Equal(["quiet", "sending", "received", "queued", "delivered"], keys);
        Assert.All(drawn, d => Assert.Equal("", d));
        Assert.Equal(["Sending reply", "Reply sent", "Reply up next, after the agent's current step", "Reply delivered"], said);
    }

    [Fact]
    public void A_stage_reached_stays_at_least_400_ms_before_the_next_replaces_it()
    {
        using var r = NewReceipt();
        Show(r);
        Tick(100);
        Show(r, e => e with { Stage = DeliveryProgress.Received });
        Tick(300);
        Assert.Equal("received", Key);
        Tick(100);
        Show(r, e => e with { Stage = DeliveryProgress.Typed });
        Assert.Equal("received", Key);
        Tick(299);
        Assert.Equal("received", Key);
        Tick(1);
        Assert.Equal("typed", Key);
    }

    [Fact]
    public void An_outcome_shows_at_once_past_the_quiet_window_and_any_stage_not_yet_shown()
    {
        using var r = NewReceipt();
        Show(r);
        Tick(50);
        Show(r, e => e with { Stage = DeliveryProgress.Received });
        Tick(50);
        Show(r, e => e with { State = DeliveryState.Done, Outcome = "delivered" });
        Assert.Equal(["quiet", "delivered"], keys);
        Assert.Equal(["Reply delivered"], said);
    }

    [Fact]
    public void Nothing_heard_for_3_s_says_Still_sending_words_only()
    {
        using var r = NewReceipt();
        Show(r);
        Tick(400);
        Assert.Equal(("sending", ""), (Key, Shown));
        Tick(2599);
        Assert.Equal("", Shown);
        Tick(1);
        Assert.Equal("Still sending…", Shown);
    }

    [Fact]
    public void Sent_with_nothing_after_it_for_5_s_says_it_is_still_waiting_on_the_machine()
    {
        using var r = NewReceipt();
        Show(r, e => e with { Stage = DeliveryProgress.Received });
        Tick(400);
        Tick(4599);
        Assert.Equal(("received", ""), (Key, Shown));
        Tick(1);
        Assert.Equal("Sent · still waiting on ULTRA", Shown);
    }

    [Fact]
    public void Passed_to_an_idle_agent_for_10_s_says_not_picked_up_yet_and_Up_next_never_stalls()
    {
        using var r = NewReceipt();
        Show(r, e => e with { Stage = DeliveryProgress.Typed });
        Tick(400);
        Tick(9600);
        Assert.Equal("Passed to the agent · not picked up yet", Shown);

        entry = entry with { Request = Guid.NewGuid().ToString(), Stage = Deliveries.Queued };
        using var busy = NewReceipt();
        Show(busy);
        Tick(400);
        Tick(100_000);
        Assert.Equal(("queued", ""), (Key, Shown));
    }

    [Fact]
    public void A_wait_says_why_the_first_copy_still_being_delivered_no_answer_yet_or_no_machine_to_send_to()
    {
        using var r = NewReceipt();
        Show(r, e => e with { Stage = DeliveryProgress.Typed });
        Tick(400);
        Tick(400);
        Show(r, e => e with { State = DeliveryState.Waiting, Reason = Deliveries.StillBeingDelivered });
        Assert.Equal("Passed to the agent · still being delivered", Shown);
        Tick(400);
        Show(r, e => e with { Reason = "timed out" });
        Assert.Equal("Still sending…", Shown);
        Tick(400);
        Show(r, e => e with { Reason = Deliveries.NotConnected });
        Assert.Equal("Waiting for ULTRA · it goes when ULTRA is back", Shown);
        Assert.Equal(ReceiptMark.Still, ReceiptWords.InFlight(false, entry, "ULTRA", false).Mark);
    }

    [Fact]
    public void A_resend_that_meets_the_first_copy_in_flight_keeps_saying_still_being_delivered_and_says_so_once()
    {
        using var r = NewReceipt();
        Show(r, e => e with { Stage = DeliveryProgress.Received });
        Tick(400);
        Tick(5000);
        Show(r, e => e with { State = DeliveryState.Waiting, Reason = Deliveries.StillBeingDelivered });
        Assert.Equal("Sent · still being delivered", Shown);
        for (var i = 0; i < 3; i++)
        {
            Tick(2000);
            Show(r, e => e with { State = DeliveryState.Sending, Reason = null, Attempts = 2 + i });
            Tick(100);
            Show(r, e => e with { State = DeliveryState.Waiting, Reason = Deliveries.StillBeingDelivered });
        }
        Tick(1000);
        Assert.Equal("Sent · still being delivered", Shown);
        Assert.Single(drawn, d => d == "Sent · still waiting on ULTRA");
        Assert.Single(said, s => s == "Reply still being delivered");
        Assert.Equal("Reply still being delivered", said[^1]);
    }

    [Fact]
    public void A_machine_without_progress_goes_quietly_then_says_Still_sending_and_a_resend_never_says_Sending_again()
    {
        using var r = NewReceipt();
        Show(r);
        Tick(400);
        Assert.Equal(("sending", ""), (Key, Shown));
        Show(r, e => e with { State = DeliveryState.Waiting, Reason = "timed out" });
        Tick(400);
        Show(r, e => e with { State = DeliveryState.Sending, Reason = null, Attempts = 2 });
        Assert.Equal("Still sending…", Shown);
        Show(r, e => e with { State = DeliveryState.Done, Outcome = "delivered" });
        Assert.Equal(("delivered", ""), (Key, Shown));
        Assert.DoesNotContain(drawn, d => d.Contains("again", StringComparison.Ordinal) || d.StartsWith("Sent", StringComparison.Ordinal));
    }

    [Fact]
    public void Each_change_is_announced_once_prefixed_at_most_one_a_second_the_later_winning_the_outcome_always()
    {
        using var r = NewReceipt();
        Show(r);
        Tick(100);
        Show(r, e => e with { Stage = DeliveryProgress.Received });
        Assert.Empty(said);
        Tick(300);
        Assert.Equal(["Reply sent"], said);
        Tick(400);
        Show(r, e => e with { Stage = Deliveries.Queued });
        Show(r, e => e with { State = DeliveryState.Done, Outcome = "delivered" });
        // The Up next inside the second is passed over for the outcome after it.
        Assert.Equal(["Reply sent"], said);
        Tick(1000);
        Assert.Equal(["Reply sent", "Reply delivered"], said);
        // Drawn again (a view model made afresh), nothing is said twice.
        using var again = NewReceipt();
        Show(again);
        Tick(1000);
        Assert.Equal(["Reply sent", "Reply delivered"], said);
    }

    [Fact]
    public void An_outcome_still_waiting_out_the_one_second_limit_when_its_view_model_is_made_afresh_is_said_by_the_new_one_after_the_same_second()
    {
        var first = NewReceipt(answer: true);
        Show(first, e => e with { Stage = DeliveryProgress.Received });
        Tick(400);
        Show(first, e => e with { State = DeliveryState.Done, Outcome = "delivered" });
        Assert.Equal(["Answer sent"], said);
        first.Dispose();
        using var again = NewReceipt(answer: true);
        Show(again);
        Assert.Equal(["Answer sent"], said);
        Tick(1000);
        Assert.Equal(["Answer sent", "Answer delivered"], said);
        Show(again);
        Tick(1000);
        Assert.Equal(["Answer sent", "Answer delivered"], said);
    }

    [Fact]
    public void A_reply_box_moved_on_to_the_next_reply_never_says_the_last_ones_outcome_on_its_line()
    {
        using var r = NewReceipt();
        Show(r, e => e with { Stage = DeliveryProgress.Received });
        Tick(400);
        Assert.Equal(["Reply sent"], said);
        // Delivered inside the second, and the next reply sent before it is said: the line now
        // shows the next one, and naming it "Reply delivered" would say it had got there.
        Show(r, e => e with { State = DeliveryState.Done, Outcome = "delivered" });
        entry = new Delivery { Key = "reply|s1", Request = Guid.NewGuid().ToString(), State = DeliveryState.Sending, Attempts = 1 };
        Show(r);
        Tick(1000);
        Assert.Equal(["Reply sent", "Sending reply"], said);
    }

    [Fact]
    public void An_answer_never_says_Up_next()
    {
        var line = ReceiptWords.InFlight(true, entry with { Stage = Deliveries.Queued }, null, false);
        Assert.Equal(("typed", "Answer passed to the agent"), (line.Key, line.Speech));
    }

    [Fact]
    public void A_confirmed_answers_value_is_a_taps_option_label_free_texts_first_line_cut_to_about_40_characters_and_none_for_a_form()
    {
        var tap = Make.Choice();
        Assert.Equal("PostgreSQL (Recommended)", ReceiptWords.AnsweredValue(tap, Answer(tap, "pg")));
        var text = Make.Choice() with { Fields = [] };
        Assert.Equal("Fixes first", ReceiptWords.AnsweredValue(text, new AnswerRequest { Id = "s1", Question = "q1", Request = "r", Text = "Fixes first" }));
        Assert.Equal("MariaDB…", ReceiptWords.AnsweredValue(text, new AnswerRequest { Id = "s1", Question = "q1", Request = "r", Text = "MariaDB\nbecause" }));
        Assert.Equal($"{new string('x', 40)}…", ReceiptWords.AnsweredValue(text, new AnswerRequest { Id = "s1", Question = "q1", Request = "r", Text = new string('x', 60) }));
        var form = Make.Choice() with { Fields = [.. Make.Choice().Fields, new Field { Name = "why", Type = FieldType.Text }] };
        Assert.Null(ReceiptWords.AnsweredValue(form, Answer(form, "pg")));
    }

    [Fact]
    public void A_tapped_options_value_is_its_whole_label_never_cut_as_free_text_is()
    {
        var label = "Keep the session store in SQLite for now, and revisit it later";
        var tap = Make.Choice() with { Fields = [new Field { Name = "answer", Type = FieldType.Choice, Options = [new Option("keep", label), new Option("move", "Move it")] }] };
        Assert.Equal(label, ReceiptWords.AnsweredValue(tap, Answer(tap, "keep")));
    }

    // ui.md §6.5: white space runs as one space, then cut at the last word break within 40
    // characters, or at 40 where the last break comes before the 21st.
    [Theory]
    [InlineData("Use   the\tsession   store", "Use the session store")]
    [InlineData("Keep the session store in SQLite for now and revisit later", "Keep the session store in SQLite for now…")]
    [InlineData("Ok supercalifragilisticexpialidociousandthensome words", "Ok supercalifragilisticexpialidociousand…")]
    [InlineData("abcdefghijklmnopqrst uvwxyzabcdefghijklmnopqrstuvwxyz", "abcdefghijklmnopqrst…")]
    [InlineData("abcdefghijklmnopqrs tuvwxyzabcdefghijklmnopqrstuvwxyz", "abcdefghijklmnopqrs tuvwxyzabcdefghijklm…")]
    public void A_free_text_answers_value_is_cut_at_a_word_break_within_40_characters(string answer, string value)
    {
        var text = Make.Choice() with { Fields = [] };
        Assert.Equal(value, ReceiptWords.AnsweredValue(text, new AnswerRequest { Id = "s1", Question = "q1", Request = "r", Text = answer }));
    }

    [Fact]
    public void A_confirmed_answers_value_is_shown_in_a_direction_of_its_own_but_spoken_without_the_isolates()
    {
        var tap = Make.Choice();
        var line = QuestionViewModel.Describe(new Delivery { Key = "k", Request = "r", State = DeliveryState.Done, Outcome = "delivered", Payload = Answer(tap, "pg") }, null, question: tap);

        Assert.Equal("You answered · \u2068PostgreSQL (Recommended)\u2069", line.Whole);
        Assert.Equal("Answer delivered. You answered PostgreSQL (Recommended)", line.Speech);
    }

    [Fact]
    public void A_confirmed_answers_value_draws_its_hidden_and_bidi_characters_as_escapes()
    {
        var text = Make.Choice() with { Fields = [] };
        Assert.Equal("ok\\u202Egnp.exe", ReceiptWords.AnsweredValue(text, new AnswerRequest { Id = "s1", Question = "q1", Request = "r", Text = "ok\u202Egnp.exe" }));
    }

    static AnswerRequest Answer(Question q, string value) => new()
    {
        Id = "s1", Question = q.Id, Request = "r",
        Fields = new Dictionary<string, JsonElement> { [q.Fields[0].Name] = JsonSerializer.SerializeToElement(value) },
    };
}
