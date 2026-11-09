using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class AgentLimitsTests
{
    DateTimeOffset now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    AgentLimits Make() => new(() => now);

    [Fact]
    public void The_seventh_message_in_a_minute_between_one_pair_is_refused_with_the_seconds_to_wait_and_admitted_after_it()
    {
        var limits = Make();
        for (var i = 0; i < 6; i++) { Assert.True(limits.CheckMessage("a", "b").Allowed); now = now.AddSeconds(5); }

        var refused = limits.CheckMessage("a", "b");
        Assert.False(refused.Allowed);
        Assert.Equal("rate-limited", refused.Reason);
        Assert.Equal(30, refused.RetryAfterSeconds);

        now = now.AddSeconds(30);
        Assert.True(limits.CheckMessage("a", "b").Allowed);
    }

    [Fact]
    public void Twelve_messages_a_minute_to_one_target_from_different_origins_is_the_most_and_a_refusal_takes_no_slot()
    {
        var limits = Make();
        for (var i = 0; i < 12; i++) { Assert.True(limits.CheckMessage($"o{i}", "t").Allowed); }
        Assert.False(limits.CheckMessage("fresh", "t").Allowed);

        // The refused message above must not have used a slot in its own pair window.
        now = now.AddMinutes(1);
        for (var i = 0; i < 6; i++) { Assert.True(limits.CheckMessage("fresh", "t").Allowed); }
    }

    [Fact]
    public void Launches_stop_at_three_an_hour_for_an_origin_and_ten_an_hour_for_the_machine()
    {
        var limits = Make();
        for (var i = 0; i < 3; i++) { Assert.True(limits.CheckLaunch("a").Allowed); }
        var refused = limits.CheckLaunch("a");
        Assert.False(refused.Allowed);
        Assert.Equal(3600, refused.RetryAfterSeconds);

        for (var i = 0; i < 7; i++) { Assert.True(limits.CheckLaunch($"o{i}").Allowed); }
        Assert.False(limits.CheckLaunch("another").Allowed);

        now = now.AddHours(1);
        Assert.True(limits.CheckLaunch("a").Allowed);
    }

    [Fact]
    public void A_flood_of_new_origins_never_wipes_a_live_window_and_a_full_table_refuses_a_new_origin()
    {
        var limits = Make();
        for (var i = 0; i < 60; i++) { limits.CheckRead("busy"); }
        for (var i = 0; i < 1023; i++) { Assert.True(limits.CheckRead($"o{i}").Allowed); }

        var refused = limits.CheckRead("one-too-many");
        Assert.False(refused.Allowed);
        Assert.False(limits.CheckRead("busy").Allowed);

        now = now.AddMinutes(1);
        Assert.True(limits.CheckRead("one-too-many").Allowed);
    }

    [Fact]
    public void Reads_stop_at_sixty_a_minute_per_origin_and_a_new_instance_starts_empty()
    {
        var limits = Make();
        for (var i = 0; i < 60; i++) { Assert.True(limits.CheckRead("a").Allowed); }
        Assert.False(limits.CheckRead("a").Allowed);
        Assert.True(limits.CheckRead("b").Allowed);
        Assert.True(Make().CheckRead("a").Allowed);
    }
}
