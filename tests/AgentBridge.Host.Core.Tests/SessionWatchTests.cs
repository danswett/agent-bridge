using AgentBridge.Host.Core.Watch;
using AgentBridge.Protocol;
using Xunit;

// Host-native session watch, slice S1 (docs/design/session-watch.md, #4262): the pure decision for
// R1 (at a prompt) and R2 (idle with assigned work that never delivered), with the 30 minute dedupe.
public class SessionWatchTests
{
    static readonly DateTimeOffset T = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    const string Boss = "agent_bridge_1111111111111111";

    static Session S(SessionStatus status, DateTimeOffset since, string id = "s1", string? response = null) => new()
    {
        Id = id,
        Agent = AgentKind.Codex,
        Status = status,
        StatusSince = since,
        UpdatedAt = since,
        Activity = new Activity { TurnCount = 1, Response = response },
    };

    static SessionWatch Watch(WatchSettings? s = null, params string[] owned) =>
        new(s ?? new WatchSettings(), id => owned.Contains(id) ? Boss : null);

    [Fact]
    public void A_prompt_shorter_than_five_minutes_raises_no_notice() =>
        Assert.Empty(Watch().Evaluate([S(SessionStatus.Waiting, T.AddMinutes(-4))], T));

    [Fact]
    public void A_prompt_at_five_minutes_raises_one_notice_and_not_again_within_thirty_minutes()
    {
        var w = Watch();
        var since = T.AddMinutes(-5);
        Assert.Equal(WatchRule.Prompt, Assert.Single(w.Evaluate([S(SessionStatus.Waiting, since)], T)).Rule);
        Assert.Empty(w.Evaluate([S(SessionStatus.Waiting, since)], T.AddMinutes(29)));
    }

    [Fact]
    public void A_prompt_still_open_repeats_three_times_then_one_final_notice_then_silence()
    {
        var w = Watch();
        var since = T.AddMinutes(-5);
        var all = new List<WatchNotice>();
        for (var i = 0; i < 8; i++) all.AddRange(w.Evaluate([S(SessionStatus.Waiting, since)], T.AddMinutes(30 * i)));
        Assert.Equal(4, all.Count);
        Assert.Equal([false, false, false, true], all.Select(n => n.Final));
    }

    [Fact]
    public void A_new_prompt_after_an_old_one_is_a_new_notice()
    {
        var w = Watch();
        Assert.Single(w.Evaluate([S(SessionStatus.Waiting, T.AddMinutes(-6))], T));
        var now = T.AddMinutes(10);
        Assert.Single(w.Evaluate([S(SessionStatus.Waiting, now.AddMinutes(-6))], now));
    }

    [Fact]
    public void A_prompt_notice_names_the_owner_only_when_the_host_has_an_owner_record()
    {
        var n = Watch(null, "a").Evaluate([S(SessionStatus.Waiting, T.AddMinutes(-6), "a"), S(SessionStatus.Waiting, T.AddMinutes(-6), "b")], T);
        Assert.Equal(Boss, n.Single(x => x.Session == "a").Owner);
        Assert.Null(n.Single(x => x.Session == "b").Owner);
    }

    [Fact]
    public void Idle_for_twenty_minutes_with_an_owner_that_never_delivered_notifies_the_owner()
    {
        var w = Watch(null, "s1");
        w.Evaluate([S(SessionStatus.Working, T.AddMinutes(-40))], T.AddMinutes(-30));
        var n = Assert.Single(w.Evaluate([S(SessionStatus.Idle, T.AddMinutes(-20))], T));
        Assert.Equal(WatchRule.Idle, n.Rule);
        Assert.Equal(Boss, n.Owner);
    }

    [Fact]
    public void Idle_with_no_owner_record_raises_no_notice()
    {
        var w = Watch();
        w.Evaluate([S(SessionStatus.Working, T.AddHours(-4))], T.AddHours(-3));
        Assert.Empty(w.Evaluate([S(SessionStatus.Idle, T.AddHours(-3))], T));
    }

    [Fact]
    public void Idle_under_twenty_minutes_raises_no_notice()
    {
        var w = Watch(null, "s1");
        w.Evaluate([S(SessionStatus.Working, T.AddMinutes(-40))], T.AddMinutes(-30));
        Assert.Empty(w.Evaluate([S(SessionStatus.Idle, T.AddMinutes(-19))], T));
    }

    [Fact]
    public void A_session_that_delivered_is_never_idle_with_work()
    {
        var w = Watch(null, "s1");
        w.Evaluate([S(SessionStatus.Working, T.AddHours(-3))], T.AddHours(-2));
        w.Evaluate([S(SessionStatus.Idle, T.AddHours(-2), response: "Done, PR is open.")], T.AddHours(-2));
        // Delivered, so it is never R2; after two idle hours it is R4: an offer to end, not idle-with-work.
        Assert.Equal(WatchRule.Finished, Assert.Single(w.Evaluate([S(SessionStatus.Idle, T.AddHours(-2), response: "Done, PR is open.")], T)).Rule);
    }

    [Fact]
    public void A_session_first_seen_idle_after_a_restart_has_unknown_delivery_and_raises_no_notice() =>
        Assert.Empty(Watch(null, "s1").Evaluate([S(SessionStatus.Idle, T.AddHours(-3))], T));

    [Fact]
    public void Working_again_clears_delivered_so_a_later_empty_idle_can_be_noticed()
    {
        var w = Watch(null, "s1");
        w.Evaluate([S(SessionStatus.Working, T.AddHours(-4))], T.AddHours(-3.5));
        w.Evaluate([S(SessionStatus.Idle, T.AddHours(-3), response: "Done.")], T.AddHours(-3));
        w.Evaluate([S(SessionStatus.Working, T.AddHours(-2))], T.AddHours(-2));
        Assert.Single(w.Evaluate([S(SessionStatus.Idle, T.AddMinutes(-25))], T));
    }

    [Fact]
    public void A_disabled_watch_sends_nothing() =>
        Assert.Empty(Watch(new WatchSettings { Enabled = false }).Evaluate([S(SessionStatus.Waiting, T.AddHours(-1))], T));

    [Fact]
    public void A_repeat_is_not_sent_one_minute_early_and_is_sent_one_minute_late()
    {
        var w = Watch();
        var since = T.AddMinutes(-5);
        Assert.Single(w.Evaluate([S(SessionStatus.Waiting, since)], T));
        Assert.Empty(w.Evaluate([S(SessionStatus.Waiting, since)], T.AddMinutes(29)));
        Assert.Single(w.Evaluate([S(SessionStatus.Waiting, since)], T.AddMinutes(31)));
    }

    [Fact]
    public void A_prompt_that_ends_and_returns_with_the_same_start_time_is_a_fresh_episode()
    {
        var w = Watch();
        var since = T.AddMinutes(-5);
        Assert.Single(w.Evaluate([S(SessionStatus.Waiting, since)], T));
        Assert.Empty(w.Evaluate([], T.AddMinutes(1)));
        Assert.Single(w.Evaluate([S(SessionStatus.Waiting, since)], T.AddMinutes(2)));
    }

    [Fact]
    public void A_held_discovery_judges_and_forgets_nothing()
    {
        var w = Watch();
        var since = T.AddMinutes(-5);
        Assert.Single(w.Evaluate([S(SessionStatus.Waiting, since)], T));
        Assert.Empty(w.Evaluate([], T.AddMinutes(1), held: true));
        Assert.Empty(w.Evaluate([S(SessionStatus.Waiting, since)], T.AddMinutes(2)));
    }

    [Theory]
    [InlineData(SessionStatus.Waiting)]
    [InlineData(SessionStatus.Working)]
    public void A_delivery_from_a_prompt_or_a_turn_counts_and_a_blank_response_does_not(SessionStatus before)
    {
        var w = Watch(null, "s1");
        w.Evaluate([S(before, T.AddHours(-3))], T.AddHours(-3));
        w.Evaluate([S(SessionStatus.Idle, T.AddHours(-2), response: "Done.")], T.AddHours(-2));
        Assert.Equal(WatchRule.Finished, Assert.Single(w.Evaluate([S(SessionStatus.Idle, T.AddHours(-2), response: "Done.")], T)).Rule);

        var blank = Watch(null, "s1");
        blank.Evaluate([S(before, T.AddHours(-3))], T.AddHours(-3));
        blank.Evaluate([S(SessionStatus.Idle, T.AddHours(-2), response: "  ")], T.AddHours(-2));
        Assert.Single(blank.Evaluate([S(SessionStatus.Idle, T.AddHours(-2), response: "  ")], T));
    }

    [Fact]
    public void Only_the_main_agents_own_answer_counts_as_delivered_not_a_delegates_text()
    {
        Session Idle(Activity a) => S(SessionStatus.Idle, T.AddMinutes(-25)) with { Activity = a };
        var w = Watch(null, "s1");
        w.Evaluate([S(SessionStatus.Working, T.AddMinutes(-40))], T.AddMinutes(-30));
        // The top-level response is a delegate's (Source names it) and the main agent has said nothing of its own.
        var delegateOnly = new Activity { TurnCount = 1, Response = "delegate text", Source = new ActivitySource { Response = "delegate text" } };
        Assert.Single(w.Evaluate([Idle(delegateOnly)], T));

        var own = Watch(null, "s1");
        own.Evaluate([S(SessionStatus.Working, T.AddMinutes(-40))], T.AddMinutes(-30));
        var mainAnswer = delegateOnly with { Own = new OwnActivity { Response = "Done." } };
        Assert.Empty(own.Evaluate([Idle(mainAnswer)], T));
    }

    // S4b: R4 (delivered, then idle past the limit: an OFFER, the host ends nothing) and R3 (an owned session that vanished unexplained).
    static SessionWatch WatchEnded(Func<string, bool>? ended, params string[] owned) =>
        new(new WatchSettings(), id => owned.Contains(id) ? Boss : null, ended);

    [Fact]
    public void An_owned_Copilot_session_that_vanishes_never_raises_missing_and_the_log_says_why_once()
    {
        var log = new List<string>();
        var w = new SessionWatch(new WatchSettings(), id => Boss, _ => false, log.Add);
        w.Evaluate([S(SessionStatus.Working, T.AddHours(-1), "cop") with { Agent = AgentKind.Copilot }], T.AddMinutes(-1));
        Assert.Empty(w.Evaluate([], T));
        Assert.Empty(w.Evaluate([], T.AddHours(1)));
        Assert.Single(log, l => l.Contains("no clean-end signal"));
    }

    // A hook loads when a session starts, so Claude's SessionEnd counts only for a session that started after setup added it (S6 rollout risk).
    static SessionWatch ClaudeWatch(DateTimeOffset? hookAt, List<string>? log = null) =>
        new(new WatchSettings { CleanEndSince = hookAt is { } h ? new Dictionary<AgentKind, DateTimeOffset> { [AgentKind.Claude] = h } : new Dictionary<AgentKind, DateTimeOffset>() },
            _ => Boss, _ => false, log is null ? null : log.Add);

    static Session ClaudeStarted(DateTimeOffset? startedAt) => S(SessionStatus.Working, T.AddHours(-1)) with { Agent = AgentKind.Claude, StartedAt = startedAt };

    [Fact]
    public void A_Claude_vanish_raises_missing_only_for_a_session_that_started_after_the_SessionEnd_hook_was_added()
    {
        var after = ClaudeWatch(T.AddHours(-5));
        after.Evaluate([ClaudeStarted(T.AddHours(-2))], T.AddMinutes(-1));
        Assert.Equal(WatchRule.Missing, Assert.Single(after.Evaluate([], T)).Rule);

        var log = new List<string>();
        var before = ClaudeWatch(T.AddHours(-1), log);
        before.Evaluate([ClaudeStarted(T.AddHours(-2))], T.AddMinutes(-1));
        Assert.Empty(before.Evaluate([], T));
        Assert.Single(log, l => l.Contains("no clean-end signal"));
    }

    [Fact]
    public void A_Claude_vanish_raises_nothing_when_no_hook_time_is_recorded_or_the_session_start_is_unknown()
    {
        var none = ClaudeWatch(null);
        none.Evaluate([ClaudeStarted(T.AddHours(-2))], T.AddMinutes(-1));
        Assert.Empty(none.Evaluate([], T));

        var unknownStart = ClaudeWatch(T.AddHours(-5));
        unknownStart.Evaluate([ClaudeStarted(null)], T.AddMinutes(-1));
        Assert.Empty(unknownStart.Evaluate([], T));
    }

    [Fact]
    public void A_Codex_vanish_with_no_session_end_and_no_deliberate_end_raises_missing_but_a_session_end_or_a_stop_does_not()
    {
        var crash = WatchEnded(_ => false, "s1");
        crash.Evaluate([S(SessionStatus.Working, T.AddHours(-1))], T.AddMinutes(-1));
        Assert.Equal(WatchRule.Missing, Assert.Single(crash.Evaluate([], T)).Rule);

        var cleanExit = WatchEnded(id => id == "s1", "s1");
        cleanExit.Evaluate([S(SessionStatus.Working, T.AddHours(-1))], T.AddMinutes(-1));
        Assert.Empty(cleanExit.Evaluate([], T));

        var stopped = WatchEnded(_ => false, "s1");
        stopped.Evaluate([S(SessionStatus.Working, T.AddHours(-1))], T.AddMinutes(-2));
        stopped.Evaluate([S(SessionStatus.Ending, T.AddMinutes(-1))], T.AddMinutes(-1));
        Assert.Empty(stopped.Evaluate([], T));
    }

    static void Delivered(SessionWatch w, string id = "s1")
    {
        w.Evaluate([S(SessionStatus.Working, T.AddHours(-3), id)], T.AddHours(-3));
        w.Evaluate([S(SessionStatus.Idle, T.AddHours(-2), id, response: "Done.")], T.AddHours(-2));
    }

    [Fact]
    public void A_delivered_session_idle_for_thirty_minutes_is_offered_for_ending_and_not_before()
    {
        var w = Watch();
        Delivered(w);
        Assert.Empty(w.Evaluate([S(SessionStatus.Idle, T.AddMinutes(-29), response: "Done.")], T));
        var n = Assert.Single(w.Evaluate([S(SessionStatus.Idle, T.AddMinutes(-30), response: "Done.")], T));
        Assert.Equal((WatchRule.Finished, null), (n.Rule, n.Owner));
    }

    [Fact]
    public void A_finished_offer_names_the_owner_when_there_is_one_and_is_not_repeated_within_thirty_minutes()
    {
        var w = Watch(null, "s1");
        Delivered(w);
        Assert.Equal(Boss, Assert.Single(w.Evaluate([S(SessionStatus.Idle, T.AddHours(-1), response: "Done.")], T)).Owner);
        Assert.Empty(w.Evaluate([S(SessionStatus.Idle, T.AddHours(-1), response: "Done.")], T.AddMinutes(29)));
    }

    [Fact]
    public void A_session_that_never_delivered_or_whose_delivery_is_unknown_is_never_offered_for_ending()
    {
        var w = Watch();
        w.Evaluate([S(SessionStatus.Working, T.AddHours(-3))], T.AddHours(-3));
        Assert.Empty(w.Evaluate([S(SessionStatus.Idle, T.AddHours(-2))], T));
        Assert.Empty(Watch().Evaluate([S(SessionStatus.Idle, T.AddHours(-2), "restarted", response: "Done.")], T));
    }

    [Fact]
    public void An_owned_session_that_vanishes_with_no_end_on_record_is_missing_to_its_owner()
    {
        var w = WatchEnded(_ => false, "s1");
        w.Evaluate([S(SessionStatus.Working, T.AddHours(-1))], T.AddMinutes(-1));
        var n = Assert.Single(w.Evaluate([], T));
        Assert.Equal((WatchRule.Missing, "s1", Boss), (n.Rule, n.Session, n.Owner));
        Assert.Empty(w.Evaluate([], T.AddMinutes(1)));
    }

    [Fact]
    public void A_session_that_ended_on_purpose_or_has_no_owner_or_has_no_end_source_is_never_missing()
    {
        var purposeful = WatchEnded(_ => true, "s1");
        purposeful.Evaluate([S(SessionStatus.Working, T.AddHours(-1))], T.AddMinutes(-1));
        Assert.Empty(purposeful.Evaluate([], T));

        var unowned = WatchEnded(_ => false);
        unowned.Evaluate([S(SessionStatus.Working, T.AddHours(-1))], T.AddMinutes(-1));
        Assert.Empty(unowned.Evaluate([], T));

        var noSource = WatchEnded(null, "s1");
        noSource.Evaluate([S(SessionStatus.Working, T.AddHours(-1))], T.AddMinutes(-1));
        Assert.Empty(noSource.Evaluate([], T));
    }

    [Fact]
    public void A_held_pass_does_not_make_a_session_missing_and_one_that_returns_is_forgotten()
    {
        var w = WatchEnded(_ => false, "s1");
        w.Evaluate([S(SessionStatus.Working, T.AddHours(-1))], T.AddMinutes(-2));
        Assert.Empty(w.Evaluate([], T.AddMinutes(-1), held: true));
        Assert.Single(w.Evaluate([], T));
        w.Evaluate([S(SessionStatus.Working, T.AddHours(-1))], T.AddMinutes(1));
        Assert.Single(w.Evaluate([], T.AddHours(2)));
    }

    [Fact]
    public void A_missing_session_repeats_every_thirty_minutes_three_times_then_one_final_notice_then_silence()
    {
        var w = WatchEnded(_ => false, "s1");
        w.Evaluate([S(SessionStatus.Working, T.AddHours(-1))], T.AddMinutes(-1));
        var all = new List<WatchNotice>();
        for (var i = 0; i < 8; i++) all.AddRange(w.Evaluate([], T.AddMinutes(31 * i)));
        Assert.Equal([false, false, false, true], all.Select(n => n.Final));
    }

    [Fact]
    public void A_finished_offer_stops_after_the_cap_with_one_final_notice()
    {
        var w = Watch();
        Delivered(w);
        var all = new List<WatchNotice>();
        for (var i = 0; i < 8; i++) all.AddRange(w.Evaluate([S(SessionStatus.Idle, T.AddHours(-2), response: "Done.")], T.AddMinutes(31 * i)));
        Assert.Equal([false, false, false, true], all.Select(n => n.Final));
    }

    [Fact]
    public void An_owned_session_seen_ending_is_explained_when_it_goes_and_so_is_one_that_had_delivered()
    {
        var stopped = WatchEnded(_ => false, "s1");
        stopped.Evaluate([S(SessionStatus.Working, T.AddHours(-1))], T.AddMinutes(-2));
        stopped.Evaluate([S(SessionStatus.Ending, T.AddMinutes(-1))], T.AddMinutes(-1));
        Assert.Empty(stopped.Evaluate([], T));

        var finished = WatchEnded(_ => false, "s1");
        Delivered(finished);
        Assert.Empty(finished.Evaluate([], T));
    }

    [Fact]
    public void A_missing_session_is_forgotten_after_a_day()
    {
        var w = WatchEnded(_ => false, "s1");
        w.Evaluate([S(SessionStatus.Working, T.AddHours(-1))], T.AddMinutes(-1));
        Assert.Single(w.Evaluate([], T));
        Assert.Empty(w.Evaluate([], T.AddHours(25)));
        Assert.Empty(w.Evaluate([], T.AddHours(60)));
    }

    [Fact]
    public void A_zero_cap_still_sends_one_notice_then_one_final_notice()
    {
        var w = Watch(new WatchSettings { MaxPerEpisode = 0 });
        var since = T.AddMinutes(-5);
        var all = new List<WatchNotice>();
        for (var i = 0; i < 5; i++) all.AddRange(w.Evaluate([S(SessionStatus.Waiting, since)], T.AddMinutes(31 * i)));
        Assert.Equal([false, true], all.Select(n => n.Final));
    }

    [Fact]
    public void A_working_session_raises_no_notice() =>
        Assert.Empty(Watch(null, "s1").Evaluate([S(SessionStatus.Working, T.AddHours(-1))], T));
}
