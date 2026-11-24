using AgentBridge.App.Core;
using Xunit;

namespace AgentBridge.App.Tests;

// #4480 (review of #4439): the fix's tests pinned only the one-line helper, so reverting its wiring in DrawEarlier left every test green. These run the real DrawEarlier twice with the first
// correction still on its way (the probe's scroller does not animate, so the offset stays where the first draw found it, as it does a few frames into the real animation).
// The numbers are the logged case: the view at 17437, a prepend of 1040 px, then one of 998 before the first correction has arrived: the second must go to 18477 + 998 = 19475.
public sealed class ConversationKeepDrawTests
{
    static SessionView ViewedAt17437()
    {
        var view = new SessionView();
        view.PrepareHistoryForProbe(olderTurns: 3);
        view.ReleasePin();
        view.Scroller.VerticalOffset = 17437;
        view.Scroller.ScrollableHeight = 18000;
        view.Scroller.ViewportHeight = 3000;
        view.Scroller.Targets.Clear();
        return view;
    }

    [Fact]
    public void A_second_prepend_while_the_first_correction_is_on_its_way_is_counted_from_where_the_view_is_going()
    {
        var view = ViewedAt17437();

        view.MeasuredExtent = 19040;           // the first draw adds 1040
        view.DrawEarlierForProbe();
        view.MeasuredExtent = 20038;           // the second adds 998, before the first correction has arrived
        view.DrawEarlierForProbe();

        Assert.Equal([18477.0, 19475.0], view.Scroller.Targets);       // the old wiring went to 18435, below the 18477 already in flight
    }

    // The branch with a stored turn in view: the target is the offset that puts that turn back where it was. Counted from the offset the animation has reached, it loses the first correction
    // the same way (the reviewer's second P2; the probe now supplies realized rows, which it never did).
    [Fact]
    public void With_a_stored_turn_in_view_the_second_prepend_is_also_counted_from_where_the_view_is_going()
    {
        var view = ViewedAt17437();
        double shifted = 0;
        view.RealizeRowsForProbe(_ => new Microsoft.UI.Xaml.FrameworkElement { Top = 100 + shifted });
        view.LayoutDuringMeasure = () => shifted = view.MeasuredExtent!.Value - 18000;       // what a prepend does: the turn in view moves down by what was added above it

        view.MeasuredExtent = 19040;
        view.DrawEarlierForProbe();
        view.MeasuredExtent = 20038;
        view.DrawEarlierForProbe();

        Assert.Equal([18477.0, 19475.0], view.Scroller.Targets);
    }

    // Only a prepend adds height above the view, so only a prepend counts a correction in flight: a restore for any other reason keeps the turn where it was, from the offset it has now.
    [Fact]
    public void A_restore_that_is_not_a_prepend_still_counts_from_the_offset_the_view_has()
    {
        var view = ViewedAt17437();
        view.MeasuredExtent = 18000;            // the layout the restore runs keeps the extent the view has
        view.RealizeRowsForProbe(_ => new Microsoft.UI.Xaml.FrameworkElement { Top = 150 });
        view.AnchorForProbe(18477);             // some earlier correction is on its way
        view.Scroller.Targets.Clear();

        view.KeepPlaceOfTurnForProbe("newer", top: 100);          // the turn in view has moved 50 down since it was noted

        Assert.Equal([17487.0], view.Scroller.Targets);           // 17437 + 50, not 18477 + 50
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void A_pending_target_that_is_not_a_finite_number_is_never_the_baseline(double pending)
    {
        Assert.Equal(17437, ConversationKeep.PrependBaseline(pendingTarget: pending, offset: 17437));
    }
}
