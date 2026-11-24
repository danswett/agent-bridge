using AgentBridge.App.Core;
using Xunit;

namespace AgentBridge.App.Tests;

// #4243: older history is drawn above the reader two turns at a time, and each draw puts the reading place back by the height it added (target = where the view is + the extent's growth).
// "Where the view is" has to be where it is GOING when an earlier correction is still animating: a draw that started from the animation's current offset computed a target below the one already in
// flight and lost that draw's whole height (frame traces of the wheel stage, six local runs: 1015 to 1300 px unrestored at some step in every run, summing to the 2.4k to 8k px by which the
// view then parked short of the end, which is what failed "wheel down to the end" on main about one run in three).
public sealed class ConversationKeepTests
{
    [Fact]
    public void With_no_correction_in_flight_the_baseline_is_where_the_view_is()
    {
        Assert.Equal(17437, ConversationKeep.PrependBaseline(pendingTarget: null, offset: 17437));
    }

    [Fact]
    public void With_a_correction_in_flight_the_baseline_is_where_it_is_going_not_where_it_has_got_to()
    {
        Assert.Equal(18477, ConversationKeep.PrependBaseline(pendingTarget: 18477, offset: 17437));
    }

    // The existing probe (ConversationEndPinTests.A_prepend_during_our_own_pending_correction_compensates_the_added_rows) has the view at 200 and a correction pending toward 150, behind it: unchanged.
    [Fact]
    public void A_pending_target_behind_the_view_is_not_taken_as_the_baseline()
    {
        Assert.Equal(200, ConversationKeep.PrependBaseline(pendingTarget: 150, offset: 200));
    }

    // The trace of the lost draw: the first prepend grew the extent by 1040 and is animating the offset from 17437 to 18477; the second grew it by 998 before that finished.
    [Fact]
    public void Two_prepends_that_overlap_move_the_place_by_both_heights_where_the_old_baseline_lost_the_second()
    {
        const double firstGrowth = 1040, secondGrowth = 998, start = 17437;
        var firstTarget = start + firstGrowth;

        var stale = start + secondGrowth;                                                                                // the offset the animation had when the second draw began
        var kept = ConversationKeep.PrependBaseline(pendingTarget: firstTarget, offset: start) + secondGrowth;

        Assert.Equal(firstTarget + secondGrowth, kept);
        Assert.True(stale < firstTarget, "the old target was below the one already in flight, so the 'only move forward' test did nothing and the second height was lost");
    }
}
