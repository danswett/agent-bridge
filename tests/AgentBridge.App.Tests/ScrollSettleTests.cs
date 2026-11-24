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

    // Arrival is a user-visible bound (Chief ruling on #4404): once the content stops changing the view is at the end within about a second (60 frames), because a pin that takes seconds is a
    // visible bug in the owner's wheel behaviour. The long wall-clock budget is only a hang backstop; it was the first attempt's mistake to let "however long it takes" pass.
    [Theory]
    [InlineData(5)]
    [InlineData(30)]
    [InlineData(59)]
    public void A_view_that_gets_to_the_end_within_the_arrival_bound_of_the_last_content_change_passes(int lag)
    {
        var settle = Feed(new ScrollSettle(high: End - 1833), lag, rest: 40, startGap: 1833);

        Assert.True(settle.Passed, settle.Describe());
    }

    [Theory]
    [InlineData(61)]
    [InlineData(400)]
    public void A_view_that_takes_longer_than_the_arrival_bound_to_reach_the_end_fails_however_it_ends(int lag)
    {
        var settle = Feed(new ScrollSettle(high: End - 1833), lag, rest: 40, startGap: 1833);

        Assert.True(settle.Done);
        Assert.False(settle.Passed);
        Assert.Contains("did not arrive", settle.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_parked_view_fails_at_the_arrival_bound_not_after_the_hang_backstop()
    {
        var settle = new ScrollSettle(high: End - 66);

        for (var i = 0; i < 200 && !settle.Done; i++) { settle.Observe(End - 66, End, moving: false); }

        Assert.True(settle.Done);
        Assert.False(settle.Passed);
        Assert.Equal(61, settle.Frames);                                   // the 61st frame without a content change and still short of the end
        Assert.Contains("66", settle.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void While_the_content_is_still_changing_the_arrival_clock_keeps_restarting()
    {
        var settle = new ScrollSettle(high: End - 50, maxFrames: 1000);

        // The end moves every 40 frames and the view is 50 behind it each time, catching up in the frames between: never 60 frames short of a final extent.
        for (var step = 0; step < 20; step++)
        {
            for (var i = 0; i < 40; i++) { settle.Observe(End + step * 200 - 50 + Math.Min(i * 5, 50), End + step * 200, moving: i < 10); }
        }

        Assert.False(settle.Done);
    }

    // The seed matters from the first frame: the notches before the settle have already raised the high-water mark, so a first frame below it is moving back up.
    [Fact]
    public void A_first_frame_below_the_seeded_high_water_mark_is_moving_back_up()
    {
        var settle = new ScrollSettle(high: 40000);

        settle.Observe(39900, End, moving: false);

        Assert.Equal(100, settle.WorstBack, 3);
        Assert.False(settle.Passed);
    }

    [Fact]
    public void A_view_that_never_rises_above_a_seeded_high_water_mark_still_fails_for_the_fall_below_it()
    {
        var settle = new ScrollSettle(high: 40000);
        for (var i = 0; i < 40; i++) { settle.Observe(39990, 39990, moving: false); }      // at its own end, 10 below where the notches had been

        Assert.True(settle.Done);
        Assert.False(settle.Passed);
        Assert.Equal(10, settle.WorstBack, 3);
    }

    [Fact]
    public void A_view_still_following_a_growing_end_after_600_frames_settles_within_the_default_budget()
    {
        var settle = new ScrollSettle(high: End - 5000);

        for (var i = 0; i < 1500; i++) { settle.Observe(End + i * 30 - 417, End + i * 30, moving: true); }   // the end keeps growing and the pin stays 417 behind it
        Assert.False(settle.Done);
        for (var i = 0; i < 40; i++) { settle.Observe(End + 45000, End + 45000, moving: false); }

        Assert.True(settle.Passed, settle.Describe());
        Assert.Equal(0, settle.WorstBack);
    }

    [Fact]
    public void A_view_that_does_not_settle_says_how_its_extent_changed()
    {
        var settle = new ScrollSettle(high: 10000, maxFrames: 50);

        for (var i = 0; i < 50; i++) { settle.Observe(10000 + i * 100, 20000 + i * 1000, moving: true); }

        Assert.True(settle.Done);
        Assert.Equal(20000, settle.FirstExtent);
        Assert.Equal(20000 + 49 * 1000, settle.Extent);
        Assert.Equal(49, settle.ExtentChanges);
        Assert.Contains("extent 20000 to 69000 (changed on 49 frames,", settle.Describe(), StringComparison.Ordinal);
    }

    // CI 45364490020: the view sat 66 short of a final extent for 90 s; the artifact has no trace, so the verdict carries the last frames (parked or creeping, moving or not).
    [Fact]
    public void A_view_that_does_not_settle_reports_its_last_frames_so_parked_can_be_told_from_creeping()
    {
        var settle = new ScrollSettle(high: 62000, maxFrames: 12);

        for (var i = 0; i < 12; i++) { settle.Observe(i < 6 ? 62000 + i * 3 : 62059, 63125, moving: i == 11); }

        var text = settle.Describe();
        Assert.Contains("last frames 62059/63125 62059/63125 62059/63125 62059/63125 62059/63125 62059/63125+", text, StringComparison.Ordinal);
        Assert.DoesNotContain("62000/", text, StringComparison.Ordinal);       // only the last six
    }

    [Fact]
    public void A_view_that_does_not_settle_says_how_long_ago_its_extent_last_changed()
    {
        var growing = new ScrollSettle(high: 10000, maxFrames: 100, arrivalFrames: int.MaxValue);
        for (var i = 0; i < 100; i++) { growing.Observe(10000 + i, 20000 + i * 50, moving: false); }
        var parked = new ScrollSettle(high: 10000, maxFrames: 100, arrivalFrames: int.MaxValue);
        for (var i = 0; i < 100; i++) { parked.Observe(10066, i < 10 ? 20000 + i * 50 : 20450, moving: false); }

        Assert.Equal(0, growing.FramesSinceExtentChange);
        Assert.Equal(90, parked.FramesSinceExtentChange);
        Assert.Contains("last changed 0 frames ago", growing.Describe(), StringComparison.Ordinal);
        Assert.Contains("last changed 90 frames ago", parked.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void With_the_arrival_bound_off_a_view_that_parks_short_of_the_end_fails_at_the_hang_backstop()
    {
        var settle = new ScrollSettle(high: End - 771, maxFrames: 100, arrivalFrames: int.MaxValue);

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
