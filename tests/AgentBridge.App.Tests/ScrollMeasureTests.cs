using Xunit;
using Xunit.Abstractions;

namespace AgentBridge.App.Tests;

// The measuring harness for #4243 (ScrollMeasure): what a wheel or a trackpad asked for against where the view was,
// near the end of a long transcript with tall tool rows and in the middle of it. These measure and print the table;
// the fix asserts Steady on the same runs. No product code is exercised that these do not already compile.
public sealed class ScrollMeasureTests(ITestOutputHelper output)
{
    const double Extent = 48000;

    static SessionView View()
    {
        var view = new SessionView();
        (view.Scroller.ViewportHeight, view.Scroller.ScrollableHeight) = (600, Extent);
        return view;
    }

    [Theory]
    [InlineData(ScrollInput.Wheel, Extent - 240)]
    [InlineData(ScrollInput.Wheel, Extent / 2)]
    [InlineData(ScrollInput.Pan, Extent - 240)]
    [InlineData(ScrollInput.Pan, Extent / 2)]
    public void Each_step_is_measured_as_asked_against_got(ScrollInput input, double start)
    {
        var samples = ScrollMeasure.Run(View(), input, start, 12);
        output.WriteLine($"{input} from {start} (extent {Extent}), tall tool rows:\n{ScrollMeasure.Table(samples)}");
        Assert.Equal(12 + (input == ScrollInput.Pan ? 1 : 0), samples.Count);
        // Every ask is one notch on from where the view was left, held at the end.
        Assert.All(samples.Where(s => s.Step < 12), s => Assert.Equal(Math.Min(s.From + ScrollMeasure.Notch, Extent), s.Requested));
    }

    [Theory]
    [InlineData(ScrollInput.Wheel)]
    [InlineData(ScrollInput.Pan)]
    public void A_transcript_with_no_estimate_errors_moves_every_step_as_asked_and_stops_at_the_end(ScrollInput input)
    {
        var samples = ScrollMeasure.Run(View(), input, Extent - 240, 12, growth: [0]);
        Assert.True(ScrollMeasure.Steady(samples), ScrollMeasure.Table(samples));
        Assert.Equal(Extent, samples[^1].Actual);
    }

    [Fact]
    public void The_measurement_sees_a_correction_that_goes_against_the_wheel()
    {
        // The table is the harness's own check: a row realised under the top block is put back with a ChangeView
        // between two notches, and that is a step that did not go as asked.
        var samples = ScrollMeasure.Run(View(), ScrollInput.Wheel, Extent - 240, 6, growth: [-30]);
        Assert.Contains(samples, s => s.Corrected && s.Actual < s.Requested);
        Assert.False(ScrollMeasure.Steady(samples), ScrollMeasure.Table(samples));
    }
}
