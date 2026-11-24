using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Xunit;

namespace AgentBridge.App.Tests;

// #4607 (scroll epic #4243): what the strict wheel self-test cannot reach, offline. It drives whole 120 notches through its own WheelForSelfTest, so it never sees a trackpad's high-resolution
// (sub-notch) deltas, the inertial tail that follows the last input, or a view scrolling UP into history while older turns are prepended (#4448 once saw a 402 px jump there). Each case
// is judged the way the self-test judges a notch: WheelNotchRule, or the block the person reads moving exactly what was asked.
public sealed class ScrollGapTests
{
    const double Extent = 48000;

    static SessionView Mid()
    {
        var view = new SessionView();
        (view.Scroller.ViewportHeight, view.Scroller.ScrollableHeight) = (600, Extent);
        return view;
    }

    // What a notch moved the read block, per group of events that make one notch: positive is the distance the view travelled, up or down.
    static List<double> PerNotch(IReadOnlyList<ScrollSample> samples, int eventsPerNotch, double step) =>
        Enumerable.Range(0, samples.Count / eventsPerNotch)
            .Select(n => -Math.Sign(step) * samples.Skip(n * eventsPerNotch).Take(eventsPerNotch).Sum(s => s.Visible)).ToList();

    // (1) A trackpad reports its movement as small wheel deltas, not whole notches of 120.
    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(119)]
    [InlineData(-1)]
    [InlineData(-15)]
    [InlineData(-119)]
    public void A_sub_notch_wheel_delta_takes_the_view_from_the_pin_like_a_whole_notch(int delta)
    {
        var view = Mid();
        view.Moved(30000);
        view.Scroller.Wheel(delta);
        Assert.False(view.Pinned);
        Assert.Equal(1, view.Releases);
    }

    [Theory]
    [InlineData(Extent, -15)]
    [InlineData(0, 15)]
    public void A_sub_notch_delta_toward_the_edge_the_view_is_at_moves_nothing_and_lets_go_of_nothing(double at, int delta)
    {
        var view = Mid();
        view.Moved(at);
        var releases = view.Releases;
        view.Scroller.Wheel(delta);
        Assert.Equal(releases, view.Releases);
    }

    [Theory]
    [InlineData(1, -15)]
    [InlineData(-1, 15)]
    public void Eight_sub_notch_events_move_the_read_block_one_notch_whatever_realises_above_it(int direction, int delta)
    {
        const int eventsPerNotch = 8;
        var step = direction * ScrollMeasure.Notch / eventsPerNotch;
        var samples = ScrollMeasure.Run(Mid(), ScrollInput.Wheel, Extent / 2, 8 * eventsPerNotch, step: step, wheelDelta: delta);
        Assert.True(ScrollMeasure.Steady(samples), ScrollMeasure.Table(samples));
        var moved = PerNotch(samples, eventsPerNotch, step);
        Assert.Null(WheelNotchRule.Failure(moved, ScrollMeasure.Notch));
    }

    // (2) After the last wheel event the view coasts: its changes are intermediate and nothing more is input. A correction of ours is a ChangeView without animation, which would cancel the
    // coasting where it is (#4243), so none is made until the tail ends; the layout that arrived meanwhile is then put back, leaving the block where the tail stopped.
    [Theory]
    [InlineData(1, -15)]
    [InlineData(-1, 15)]
    public void An_inertial_tail_after_the_last_input_is_not_cut_short_and_ends_with_the_block_still(int direction, int delta)
    {
        double[] tail = [24, 20, 16, 13, 10, 8, 6, 5, 4, 3, 2, 1];
        var steps = tail.Select(t => direction * t).ToArray();
        var view = Mid();
        var samples = ScrollMeasure.Run(view, ScrollInput.Inertia, Extent / 2, steps.Length, stepSizes: steps, wheelDelta: delta);
        var table = ScrollMeasure.Table(samples);
        Assert.False(view.Pinned);
        Assert.All(samples.Take(steps.Length), s => Assert.False(s.Corrected, table));
        Assert.Equal(steps, samples.Take(steps.Length).Select(s => s.Actual - s.From));
        var released = samples[^1];
        Assert.True(released.Corrected, table);
        Assert.True(ScrollMeasure.Steady([released]), table);
    }

    // The same tail with the layout realising nothing: it moves exactly what the events asked, and at the release there is nothing to put back.
    [Fact]
    public void An_inertial_tail_over_a_transcript_with_no_estimate_errors_asks_nothing_of_the_layout()
    {
        var samples = ScrollMeasure.Run(Mid(), ScrollInput.Inertia, Extent / 2, 6, growth: [0], stepSizes: [-30, -20, -12, -6, -3, -1], wheelDelta: 15);
        Assert.True(ScrollMeasure.Steady(samples), ScrollMeasure.Table(samples));
        Assert.All(samples, s => Assert.False(s.Corrected));
    }

    // (3) Scrolling up through history. Every row is 500 tall, so a row drawn above moves the block being read down by 500 and the draw's correction must take exactly that back. The turns
    // under the view are older ones the person has not yet seen drawn, two at a draw, as the draw timer brings them.
    const double Row = 500, Viewport = 600, Below = 6000;

    sealed class History
    {
        public SessionView View { get; } = new();
        public int Drawn0 { get; }

        public History()
        {
            View.PrepareHistoryForProbe(olderTurns: 12);
            View.ReleasePin();
            Drawn0 = View.ShownCount;
            View.Scroller.ViewportHeight = Viewport;
            View.LayoutDuringMeasure = () => View.Scroller.ScrollableHeight = View.ShownCount * Row + Below - Viewport;
            View.Scroller.ScrollableHeight = Drawn0 * Row + Below - Viewport;
            // Row i of the drawn turns starts i rows down the transcript, wherever the view is.
            View.RealizeRowsForProbe(i => new FrameworkElement { Top = i * Row - View.Scroller.VerticalOffset, Height = Row });
            View.Scroller.VerticalOffset = 300;
            View.Scroller.Targets.Clear();
        }

        // Where the turn the person was reading when they started is on screen: the first one drawn, which every prepend pushes down the transcript.
        public double Block => (View.ShownCount - Drawn0) * Row - View.Scroller.VerticalOffset;

        // The draw timer's tick between two inputs, and the correction it asks for landing as a final view change (or not, to show what the measure sees).
        public void Draw(bool correctionLands = true)
        {
            View.DrawEarlierForProbe();
            if (correctionLands && View.Scroller.Targets.Count > 0) { View.Moved(View.Scroller.Targets[^1]); }
            View.Scroller.Targets.Clear();
        }
    }

    static List<double> UpwardNotches(History h, int notches, bool correctionLands = true)
    {
        var moved = new List<double>();
        for (var n = 0; n < notches; n++)
        {
            var before = h.Block;
            h.View.Scroller.Wheel(120);
            h.View.Moved(h.View.Scroller.VerticalOffset - ScrollMeasure.Notch);
            h.Draw(correctionLands);
            moved.Add(h.Block - before);
        }
        return moved;
    }

    [Fact]
    public void Scrolling_up_notch_by_notch_while_older_turns_are_prepended_moves_the_read_block_a_notch_each()
    {
        var h = new History();
        var moved = UpwardNotches(h, 12);
        Assert.True(h.View.ShownCount - h.Drawn0 >= 6, "older turns were drawn above the view during the notches");
        Assert.Null(WheelNotchRule.Failure(moved, ScrollMeasure.Notch));
    }

    // The measure is only worth anything if it tells the jump: a prepend whose correction never lands moves the block by the rows drawn (the 402 px of #4448 was one such).
    [Fact]
    public void The_measure_tells_a_prepend_whose_correction_never_lands_from_one_that_keeps_the_block_in_place()
    {
        var moved = UpwardNotches(new History(), 4, correctionLands: false);
        Assert.StartsWith("per-notch rule", WheelNotchRule.Failure(moved, ScrollMeasure.Notch));
    }

    [Fact]
    public void Eight_sub_notch_events_up_with_a_draw_between_each_move_the_read_block_a_notch()
    {
        var h = new History();
        var moved = new List<double>();
        for (var notch = 0; notch < 8; notch++)
        {
            var before = h.Block;
            for (var e = 0; e < 8; e++)
            {
                h.View.Scroller.Wheel(15);
                h.View.Moved(h.View.Scroller.VerticalOffset - ScrollMeasure.Notch / 8);
                h.Draw();
            }
            moved.Add(h.Block - before);
        }
        Assert.True(h.View.ShownCount > h.Drawn0);
        Assert.Null(WheelNotchRule.Failure(moved, ScrollMeasure.Notch));
    }

    // Older turns are never drawn under an inertial tail: its view changes are intermediate, the draw waits for the last, and then it keeps the block where the tail left it.
    [Fact]
    public void Older_turns_wait_out_an_inertial_tail_and_are_then_drawn_without_moving_the_block()
    {
        var h = new History();
        h.View.Scroller.Wheel(15);
        h.View.GestureForProbe("scroll");
        foreach (var step in new double[] { 24, 20, 16, 13, 10, 8, 6, 5, 4, 3, 2, 1 })
        {
            h.View.Moved(h.View.Scroller.VerticalOffset - step, final: false);
            h.View.DrawEarlierForProbe();
        }
        Assert.Equal(h.Drawn0, h.View.ShownCount);
        Assert.Empty(h.View.Scroller.Targets);

        h.View.GestureForProbe("none");
        h.View.Moved(h.View.Scroller.VerticalOffset);
        var stopped = h.Block;
        h.Draw();
        Assert.True(h.View.ShownCount > h.Drawn0);
        Assert.Equal(stopped, h.Block, 0.5);
    }
}
