using Xunit;

namespace AgentBridge.Transport.Tests;

// The wait before a relay client tries again (#578): full jitter up to min(30 s, 1 s * 2^n), and
// a 1012's first retry spread over 0-5 s or the relay's own hint.
public class ReconnectBackoffTests
{
    static ReconnectBackoff Pinned(double r) => new(() => r);

    static TimeSpan[] Schedule(ReconnectBackoff b, int n) => [.. Enumerable.Range(0, n).Select(_ => b.Next())];

    [Fact]
    public void The_longest_waits_double_from_one_second_and_stop_at_thirty()
    {
        var waits = Schedule(Pinned(0.999_999_999), 8);

        Assert.Equal([1, 2, 4, 8, 16, 30, 30, 30], waits.Select(w => (int)Math.Round(w.TotalSeconds)));
        Assert.All(waits, w => Assert.True(w <= ReconnectBackoff.Cap));
    }

    [Fact]
    public void A_wait_is_drawn_from_zero_up_to_its_ceiling_not_fixed_at_it()
    {
        Assert.All(Schedule(Pinned(0), 8), w => Assert.Equal(TimeSpan.Zero, w));
        Assert.Equal([0.5, 1, 2, 4, 8, 15], Schedule(Pinned(0.5), 6).Select(w => w.TotalSeconds));
    }

    [Fact]
    public void Two_clients_with_their_own_random_draws_do_not_retry_at_the_same_marks()
    {
        var a = new ReconnectBackoff(new Random(1).NextDouble);
        var b = new ReconnectBackoff(new Random(2).NextDouble);

        Assert.NotEqual(Schedule(a, 6), Schedule(b, 6));
    }

    [Fact]
    public void A_random_source_outside_zero_to_one_never_stretches_or_reverses_a_wait()
    {
        Assert.Equal(TimeSpan.Zero, Pinned(1.5).Next());
        Assert.Equal(TimeSpan.Zero, Pinned(-1).Next());
        Assert.Equal(TimeSpan.Zero, Pinned(double.NaN).Next());
    }

    [Fact]
    public void Only_a_successful_connection_starts_the_schedule_again()
    {
        var b = Pinned(0.5);
        Schedule(b, 4);
        Assert.Equal(4, b.Attempt);

        b.Reset();

        Assert.Equal(0, b.Attempt);
        Assert.Equal(TimeSpan.FromSeconds(0.5), b.Next());
    }

    [Fact]
    public void Many_attempts_stay_at_the_cap_without_overflowing()
    {
        var b = Pinned(0.999_999_999);
        Schedule(b, 200);

        Assert.True(b.Next() is var w && w > TimeSpan.FromSeconds(29) && w <= ReconnectBackoff.Cap);
    }

    [Fact]
    public void After_a_1012_the_first_retry_is_spread_over_five_seconds_then_the_schedule_carries_on()
    {
        var b = Pinned(0.5);

        Assert.Equal(TimeSpan.FromSeconds(2.5), b.Next(CloseCodes.Restarting, "restarting"));
        Assert.Equal(TimeSpan.FromSeconds(1), b.Next());
        Assert.True(Pinned(0.999_999_999).Next(CloseCodes.Restarting, "restarting") is var w && w > TimeSpan.FromSeconds(4.9) && w <= TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Other_close_codes_get_the_ordinary_jittered_wait()
    {
        foreach (var code in new int?[] { 1000, 1006, CloseCodes.RateLimited, CloseCodes.Protocol, null })
        {
            Assert.Equal(TimeSpan.FromSeconds(0.5), Pinned(0.5).Next(code, "restarting;spread=60"));
        }
    }

    [Theory]
    [InlineData("restarting;spread=20", 20)]
    [InlineData("restarting;spread=0", 1)]
    [InlineData("restarting;spread=600", 60)]
    [InlineData("restarting;spread=1", 1)]
    public void A_1012_can_widen_or_narrow_its_spread_within_one_to_sixty_seconds(string reason, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), ReconnectBackoff.SpreadFrom(reason));
        Assert.Equal(TimeSpan.FromSeconds(seconds / 2.0), Pinned(0.5).Next(CloseCodes.Restarting, reason));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("restarting")]
    [InlineData("restarting;spread=")]
    [InlineData("restarting;spread=-5")]
    [InlineData("restarting;spread=+5")]
    [InlineData("restarting;spread= 5")]
    [InlineData("restarting;spread=5.5")]
    [InlineData("restarting;spread=0x10")]
    [InlineData("restarting;spread=99999999999999999999")]
    [InlineData("restarting;spread=5;spread=60")]
    [InlineData("RESTARTING;spread=20")]
    [InlineData("stopping;spread=20")]
    [InlineData("restarting;wait=20")]
    [InlineData("restarting;spread=２０")]
    [InlineData("restarting;spread=20\n")]
    public void A_malformed_hint_is_ignored_and_the_spread_stays_five_seconds(string? reason)
    {
        Assert.Null(ReconnectBackoff.SpreadFrom(reason));
        Assert.Equal(TimeSpan.FromSeconds(2.5), Pinned(0.5).Next(CloseCodes.Restarting, reason));
    }

    [Fact]
    public void A_narrow_hint_late_in_the_schedule_does_not_shorten_the_ordinary_ceiling()
    {
        var b = Pinned(0.5);
        Schedule(b, 5);

        Assert.Equal(TimeSpan.FromSeconds(15), b.Next(CloseCodes.Restarting, "restarting;spread=1"));
    }
}
