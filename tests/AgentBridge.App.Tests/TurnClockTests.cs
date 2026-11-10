using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.App.Tests;

public class TurnClockTests
{
    readonly FakeTimeProvider time = new(Make.T0);

    [Fact]
    public void A_turn_is_timed_from_the_send_to_the_session_settling()
    {
        var clock = new TurnClock(time);
        clock.Sent();
        time.Advance(TimeSpan.FromMilliseconds(500));
        clock.Seen(SessionStatus.Working);
        time.Advance(TimeSpan.FromMilliseconds(3700));
        clock.Seen(SessionStatus.Idle);
        Assert.Equal([4200], clock.Recent);
    }

    [Fact]
    public void A_status_before_the_session_starts_working_does_not_settle_the_turn()
    {
        var clock = new TurnClock(time);
        clock.Sent();
        clock.Seen(SessionStatus.Idle);
        Assert.Empty(clock.Recent);
    }

    [Fact]
    public void A_second_send_keeps_the_first_start_and_a_stale_turn_starts_again()
    {
        var clock = new TurnClock(time);
        clock.Sent();
        time.Advance(TimeSpan.FromSeconds(2));
        clock.Sent();
        clock.Seen(SessionStatus.Working);
        clock.Seen(SessionStatus.Idle);
        Assert.Equal([2000], clock.Recent);

        clock.Sent();
        time.Advance(TurnClock.Expire + TimeSpan.FromSeconds(1));
        clock.Sent();
        time.Advance(TimeSpan.FromMilliseconds(900));
        clock.Seen(SessionStatus.Working);
        clock.Seen(SessionStatus.Idle);
        Assert.Equal([2000, 900], clock.Recent);
    }

    [Fact]
    public void A_session_that_goes_away_reports_nothing_and_drain_empties_the_record()
    {
        var clock = new TurnClock(time);
        clock.Sent();
        clock.Seen(SessionStatus.Working);
        clock.Dropped();
        clock.Seen(SessionStatus.Idle);
        Assert.Empty(clock.Recent);

        clock.Sent();
        clock.Seen(SessionStatus.Working);
        clock.Seen(SessionStatus.Waiting);
        Assert.Single(clock.Drain());
        Assert.Empty(clock.Recent);
    }
}
