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
        Agent = AgentKind.Claude,
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
        Assert.Empty(w.Evaluate([S(SessionStatus.Idle, T.AddHours(-2), response: "Done, PR is open.")], T));
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
        Assert.Empty(w.Evaluate([S(SessionStatus.Idle, T.AddHours(-2), response: "Done.")], T));

        var blank = Watch(null, "s1");
        blank.Evaluate([S(before, T.AddHours(-3))], T.AddHours(-3));
        blank.Evaluate([S(SessionStatus.Idle, T.AddHours(-2), response: "  ")], T.AddHours(-2));
        Assert.Single(blank.Evaluate([S(SessionStatus.Idle, T.AddHours(-2), response: "  ")], T));
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
