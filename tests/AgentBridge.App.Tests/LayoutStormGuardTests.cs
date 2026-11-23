using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class LayoutStormGuardTests
{
    // A pass that finds nothing to correct is not a run; only corrections that move the view can feed the next pass.
    static bool Correcting(LayoutStormGuard guard, out int runs, out bool first)
    {
        var allowed = guard.Allowed(out runs, out first);
        if (allowed) { guard.Run(); }
        return allowed;
    }

    [Fact]
    public void Sixty_correction_runs_in_a_second_proceed_and_the_next_is_a_storm_with_its_count()
    {
        var time = new FakeTimeProvider(); var guard = new LayoutStormGuard(time);
        for (var i = 0; i < LayoutStormGuard.Limit; i++) { Assert.True(Correcting(guard, out _, out _)); time.Advance(TimeSpan.FromMilliseconds(1)); }
        Assert.False(Correcting(guard, out var runs, out var first));
        Assert.Equal(LayoutStormGuard.Limit, runs);
        Assert.True(first);
        Assert.False(Correcting(guard, out _, out first));
        Assert.False(first);
    }

    [Fact]
    public void Ordinary_streaming_at_the_measured_peak_of_95_passes_a_second_never_trips()
    {
        var time = new FakeTimeProvider(); var guard = new LayoutStormGuard(time);
        // CI self-test peak: 95 raw LayoutUpdated passes a second, one in three correcting (about 32 runs).
        for (var i = 0; i < 950; i++)
        {
            Assert.True(guard.Allowed(out _, out _));
            if (i % 3 == 0) { guard.Run(); }
            time.Advance(TimeSpan.FromMilliseconds(1000.0 / 95));
        }
    }

    [Fact]
    public void Even_every_one_of_95_passes_correcting_trips_only_past_sixty_runs_in_the_window()
    {
        var time = new FakeTimeProvider(); var guard = new LayoutStormGuard(time);
        var tripped = 0;
        for (var i = 0; i < 95; i++)
        {
            if (!Correcting(guard, out _, out _)) { tripped = i; break; }
            time.Advance(TimeSpan.FromMilliseconds(1000.0 / 95));
        }
        Assert.Equal(LayoutStormGuard.Limit, tripped);
    }

    [Fact]
    public void A_reentrant_loop_where_every_pass_corrects_still_trips_and_never_exceeds_the_limit_in_a_window()
    {
        var time = new FakeTimeProvider(); var guard = new LayoutStormGuard(time);
        int skipped = 0, announced = 0, peak = 0;
        for (var i = 0; i < 1000; i++)
        {
            if (!Correcting(guard, out var runs, out var first)) { skipped++; if (first) { announced++; } }
            peak = Math.Max(peak, runs);
            time.Advance(TimeSpan.FromMilliseconds(1));
        }
        Assert.True(skipped > 0);
        Assert.True(announced >= 1);
        Assert.True(peak <= LayoutStormGuard.Limit);
    }

    [Fact]
    public void The_peak_is_the_most_correction_runs_seen_in_one_window()
    {
        var time = new FakeTimeProvider(); var guard = new LayoutStormGuard(time);
        for (var i = 0; i < 5; i++) { guard.Run(); }
        time.Advance(TimeSpan.FromSeconds(2));
        for (var i = 0; i < 3; i++) { guard.Run(); }
        Assert.Equal(5, guard.Peak);
        Assert.Equal(0, guard.Storms);
    }

    [Fact]
    public void Each_window_that_goes_over_the_limit_is_one_storm()
    {
        var time = new FakeTimeProvider(); var guard = new LayoutStormGuard(time);
        for (var i = 0; i < 100; i++) { Correcting(guard, out _, out _); }
        Assert.Equal(1, guard.Storms);
        time.Advance(TimeSpan.FromSeconds(2));
        for (var i = 0; i < LayoutStormGuard.Limit + 1; i++) { Correcting(guard, out _, out _); }
        Assert.Equal(2, guard.Storms);
        Assert.Equal(LayoutStormGuard.Limit, guard.Peak);
    }

    [Fact]
    public void A_quiet_second_ends_the_storm()
    {
        var time = new FakeTimeProvider(); var guard = new LayoutStormGuard(time);
        for (var i = 0; i < 200; i++) { Correcting(guard, out _, out _); }
        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(guard.Allowed(out _, out _));
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.True(guard.Allowed(out var runs, out _));
        Assert.Equal(0, runs);
        Assert.True(Correcting(guard, out _, out _));
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
        for (var i = 0; i < 100; i++) { Correcting(guard, out _, out _); }
        time.Wall = time.Wall.AddHours(1);
        Assert.False(guard.Allowed(out var runs, out _));
        Assert.Equal(LayoutStormGuard.Limit, runs);
        time.Stamp += 1000;
        Assert.True(guard.Allowed(out runs, out _));
        Assert.Equal(0, runs);
    }

    // #3811: a dense burst keeps at most Limit stamps (the oldest decide when the window ends), not one per event.
    [Fact]
    public void A_dense_burst_retains_at_most_the_limit_and_the_decision_is_unchanged()
    {
        var time = new FakeTimeProvider(); var guard = new LayoutStormGuard(time);
        for (var i = 0; i < 100_000; i++) { guard.Run(); }
        Assert.Equal(LayoutStormGuard.Limit, guard.Retained);
        Assert.False(guard.Allowed(out var runs, out _));
        Assert.Equal(LayoutStormGuard.Limit, runs);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(guard.Allowed(out _, out _));
        Assert.Equal(0, guard.Retained);
    }
}
