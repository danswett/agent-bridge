using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class LayoutStormGuardTests
{
    [Fact]
    public void Sixty_passes_in_a_second_proceed_and_the_next_is_a_storm_with_its_count()
    {
        var time = new FakeTimeProvider(); var guard = new LayoutStormGuard(time);
        for (var i = 0; i < LayoutStormGuard.Limit; i++) { Assert.True(guard.Pass(out _)); time.Advance(TimeSpan.FromMilliseconds(1)); }
        Assert.False(guard.Pass(out var count));
        Assert.Equal(LayoutStormGuard.Limit + 1, count);
    }

    [Fact]
    public void A_steady_fifty_passes_a_second_is_never_a_storm()
    {
        var time = new FakeTimeProvider(); var guard = new LayoutStormGuard(time);
        for (var i = 0; i < 500; i++) { Assert.True(guard.Pass(out var count)); Assert.True(count <= 51); time.Advance(TimeSpan.FromMilliseconds(20)); }
    }

    [Fact]
    public void The_peak_is_the_most_passes_seen_in_one_window()
    {
        var time = new FakeTimeProvider(); var guard = new LayoutStormGuard(time);
        for (var i = 0; i < 5; i++) { guard.Pass(out _); }
        time.Advance(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 3; i++) { guard.Pass(out _); }
        Assert.Equal(5, guard.Peak);
        Assert.Equal(0, guard.Storms);
    }

    [Fact]
    public void Each_window_that_goes_over_the_limit_is_one_storm()
    {
        var time = new FakeTimeProvider(); var guard = new LayoutStormGuard(time);
        for (var i = 0; i < 100; i++) { guard.Pass(out _); }
        Assert.Equal(1, guard.Storms);
        time.Advance(TimeSpan.FromSeconds(2));
        for (var i = 0; i < LayoutStormGuard.Limit + 1; i++) { guard.Pass(out _); }
        Assert.Equal(2, guard.Storms);
        Assert.Equal(100, guard.Peak);
    }

    [Fact]
    public void A_quiet_second_ends_the_storm()
    {
        var time = new FakeTimeProvider(); var guard = new LayoutStormGuard(time);
        for (var i = 0; i < 200; i++) { guard.Pass(out _); }
        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(guard.Pass(out _));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(guard.Pass(out var count));
        Assert.Equal(1, count);
    }

    // The fake's SetUtcNow moves its timestamp too, so a clock of our own: wall time can step, the stamp cannot.
    sealed class SteppedClock : TimeProvider
    {
        public DateTimeOffset Wall = new(2026, 11, 22, 20, 0, 0, TimeSpan.Zero);
        public long Stamp;
        public override DateTimeOffset GetUtcNow() => Wall;
        public override long GetTimestamp() => Stamp;
        public override long TimestampFrequency => 1000;
    }

    [Fact]
    public void A_forward_wall_clock_step_does_not_end_a_storm_early()
    {
        var time = new SteppedClock(); var guard = new LayoutStormGuard(time);
        for (var i = 0; i < 100; i++) { guard.Pass(out _); }
        time.Wall = time.Wall.AddHours(1);
        Assert.False(guard.Pass(out var count));
        Assert.Equal(101, count);
        time.Stamp += 1000;
        Assert.True(guard.Pass(out count));
        Assert.Equal(1, count);
    }
}