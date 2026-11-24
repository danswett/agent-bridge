using AgentBridge.App.Core;
using Xunit;

namespace AgentBridge.App.Tests;

// #4243: the published-app self-test's "wheel down to the end" check. It failed in about one of three main runs on "quiet gap 771" (1833 on another run) while the behaviour held (worst back 0):
// it took the WORST distance from the end over a fixed 60 frames after the last notch, so a hosted runner that was still catching up in the first frames failed it. The judge is now STATE:
// the view has settled when, for a run of consecutive rendered frames, it sits at the end, is not moving, and neither its offset nor its extent changes. "Never moves back up" stays strict.
public sealed class ScrollSettleTests
{
    const double End = 40000;

    // Frames of a view that closes the distance to the end over `lag` frames, then rests there.
    static ScrollSettle Feed(ScrollSettle settle, int lag, int rest, double startGap = 771)
    {
        for (var i = 0; i < lag; i++) { settle.Observe(End - startGap * (1 - (i + 1.0) / (lag + 1)), End, moving: true); }
        for (var i = 0; i < rest; i++) { settle.Observe(End, End, moving: false); }
        return settle;
    }

    [Fact]
    public void A_view_that_is_still_catching_up_in_the_first_frames_passes_once_it_rests_at_the_end()
    {
        var settle = Feed(new ScrollSettle(high: End - 771), lag: 20, rest: 40);

        Assert.True(settle.Done);
        Assert.True(settle.Passed, settle.Describe());
        Assert.Equal(0, settle.WorstBack);
    }

    // The failure being fixed: the same view, on a slower runner, takes many times as many frames to get there. The verdict is the state it reaches, not how long that took.
    [Theory]
    [InlineData(5)]
    [InlineData(60)]
    [InlineData(400)]
    public void How_many_frames_the_catching_up_takes_does_not_change_the_verdict(int lag)
    {
        var settle = Feed(new ScrollSettle(high: End - 1833), lag, rest: 40, startGap: 1833);

        Assert.True(settle.Passed, settle.Describe());
    }

    [Fact]
    public void A_view_that_parks_short_of_the_end_never_settles_and_fails_when_the_frame_budget_runs_out()
    {
        var settle = new ScrollSettle(high: End - 771, maxFrames: 100);

        for (var i = 0; i < 200; i++) { settle.Observe(End - 771, End, moving: false); }

        Assert.True(settle.Done);
        Assert.False(settle.Passed);
        Assert.False(settle.Settled);
        Assert.Equal(100, settle.Frames);
        Assert.Contains("771", settle.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Moving_back_up_fails_however_the_view_ends()
    {
        var settle = new ScrollSettle(high: 39000);
        settle.Observe(39200, End, moving: true);
        settle.Observe(39170, End, moving: true);      // 30 back up from the highest it has been
        Feed(settle, lag: 5, rest: 40);

        Assert.True(settle.Settled);
        Assert.False(settle.Passed);
        Assert.Equal(30, settle.WorstBack, 3);
    }

    [Fact]
    public void Moving_back_up_after_it_has_rested_at_the_end_fails_too()
    {
        var settle = Feed(new ScrollSettle(high: End), lag: 0, rest: 10);
        settle.Observe(End - 6, End, moving: false);
        Feed(settle, lag: 0, rest: 40);

        Assert.False(settle.Passed);
        Assert.Equal(6, settle.WorstBack, 3);
    }

    [Fact]
    public void A_rounding_dip_within_the_tolerance_is_not_moving_back_up()
    {
        var settle = new ScrollSettle(high: End);
        settle.Observe(End - 1.0, End, moving: false);
        Feed(settle, lag: 0, rest: 40);

        Assert.True(settle.Passed, settle.Describe());
    }

    // Moving back up in the notches before the settle (the loop in the self-test) is carried in, so the settle cannot launder it.
    [Fact]
    public void Back_movement_seen_during_the_notches_is_carried_into_the_verdict()
    {
        var settle = Feed(new ScrollSettle(high: End, worstBack: 3.0), lag: 0, rest: 40);

        Assert.True(settle.Done);
        Assert.False(settle.Passed);
        Assert.Equal(3.0, settle.WorstBack, 3);
    }

    [Fact]
    public void More_history_drawing_while_at_the_end_reopens_the_settle_and_the_pin_following_it_passes()
    {
        var settle = Feed(new ScrollSettle(high: End), lag: 0, rest: 20);   // nearly settled
        Assert.False(settle.Done);
        settle.Observe(End, End + 200, moving: false);                      // the extent grew: the view is 200 short of the new end
        settle.Observe(End + 120, End + 200, moving: true);
        for (var i = 0; i < 40; i++) { settle.Observe(End + 200, End + 200, moving: false); }

        Assert.True(settle.Passed, settle.Describe());
        Assert.Equal(0, settle.WorstBack);
    }

    // History still drawing with the pin keeping up: the view is at the end on every frame, but its offset and extent change on every frame, so it is not at rest yet.
    [Fact]
    public void A_view_at_the_end_whose_offset_and_extent_keep_growing_together_is_not_settled()
    {
        var settle = new ScrollSettle(high: End, maxFrames: 1000);

        for (var i = 0; i < 100; i++) { settle.Observe(End + i * 40, End + i * 40, moving: false); }

        Assert.False(settle.Settled);
        Assert.False(settle.Done);
    }

    [Fact]
    public void Resting_at_the_end_is_not_settled_while_the_view_still_reports_moving()
    {
        var settle = new ScrollSettle(high: End, maxFrames: 1000);

        for (var i = 0; i < 100; i++) { settle.Observe(End, End, moving: true); }

        Assert.False(settle.Settled);
        Assert.False(settle.Done);
    }

    [Fact]
    public void A_frame_that_is_not_still_restarts_the_count_so_a_flicker_cannot_settle_the_view()
    {
        var settle = new ScrollSettle(high: End, stillFrames: 30);
        for (var round = 0; round < 10; round++)
        {
            for (var i = 0; i < 29; i++) { settle.Observe(End, End, moving: false); }
            settle.Observe(End, End, moving: true);
        }

        Assert.False(settle.Settled);
    }

    [Fact]
    public void Once_done_further_frames_change_nothing()
    {
        var settle = Feed(new ScrollSettle(high: End), lag: 0, rest: 40);
        var frames = settle.Frames;

        settle.Observe(End - 500, End, moving: false);

        Assert.Equal(frames, settle.Frames);
        Assert.True(settle.Passed);
    }
}
