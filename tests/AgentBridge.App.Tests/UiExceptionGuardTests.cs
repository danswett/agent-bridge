using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class UiExceptionGuardTests
{
    [Fact]
    public void Three_exceptions_in_ten_seconds_are_survived_and_the_fourth_is_not()
    {
        var time = new FakeTimeProvider(); var guard = new UiExceptionGuard(time);
        Assert.True(guard.Allow());
        time.Advance(TimeSpan.FromSeconds(1)); Assert.True(guard.Allow());
        time.Advance(TimeSpan.FromSeconds(8)); Assert.True(guard.Allow());
        time.Advance(TimeSpan.FromSeconds(0.5)); Assert.False(guard.Allow());
    }

    [Fact]
    public void A_quiet_window_lets_the_app_survive_again()
    {
        var time = new FakeTimeProvider(); var guard = new UiExceptionGuard(time);
        for (var i = 0; i < 3; i++) { Assert.True(guard.Allow()); time.Advance(TimeSpan.FromSeconds(1)); }
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(guard.Allow());
    }

    [Fact]
    public void Refused_exceptions_do_not_extend_the_storm()
    {
        var time = new FakeTimeProvider(); var guard = new UiExceptionGuard(time);
        for (var i = 0; i < 3; i++) { Assert.True(guard.Allow()); }
        time.Advance(TimeSpan.FromSeconds(5)); Assert.False(guard.Allow());
        time.Advance(TimeSpan.FromSeconds(5.5)); Assert.True(guard.Allow());
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
    public void A_forward_wall_clock_step_does_not_allow_more_survivals_in_the_window()
    {
        var time = new SteppedClock(); var guard = new UiExceptionGuard(time);
        for (var i = 0; i < 3; i++) { Assert.True(guard.Allow()); }
        time.Wall = time.Wall.AddHours(1);
        Assert.False(guard.Allow());
        time.Stamp += 10_000;
        Assert.True(guard.Allow());
    }
}