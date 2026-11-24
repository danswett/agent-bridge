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

    // The blank viewport the ARM64 self-test saw mid wheel burst (phase control-wheel:rapid-burst, 8 notches a frame,
    // viewport 363, list CacheLength 2): can OUR code ask for an offset the list has not realised rows around? The
    // list realises the viewport and CacheLength (2) viewports either side as of the last layout; a frame that ends
    // further on than that shows rows the layout has not made yet. Realisation speed is not modelled here (that is
    // WinUI's, and slower on ARM64): this is only the distance our own requests and corrections cover in one frame.
    [Fact]
    public void A_rapid_wheel_burst_with_tall_rows_asks_for_less_than_the_realised_window_a_frame()
    {
        const double viewport = 363, cacheLength = 2, notch = ScrollMeasure.Notch;
        var rows = new List<string>();
        int? firstPartial = null, firstFull = null;
        for (var perFrame = 1; perFrame <= 40; perFrame++)
        {
            var view = View();
            view.Scroller.ViewportHeight = viewport;
            var samples = ScrollMeasure.Run(view, ScrollInput.Wheel, Extent / 2, perFrame);
            var jump = samples[^1].Actual - samples[0].From;
            // Realised: the viewport and CacheLength viewports below it, as of the layout before this frame.
            var below = viewport + cacheLength * viewport;
            var partial = jump + viewport > below;
            var full = jump > below;
            if (partial) { firstPartial ??= perFrame; }
            if (full) { firstFull ??= perFrame; }
            rows.Add($"{perFrame,2} notches a frame: view moves {jump,6:0.#} px (realised {below:0.#} below the top edge){(full ? "  NO ROW IN VIEW" : partial ? "  partly past the realised rows" : "")}");
        }
        output.WriteLine(string.Join("\n", rows));
        output.WriteLine($"first frame size with rows missing from part of the view: {firstPartial}; with none in view: {firstFull}");
        // The self-test's 8 notches a frame is well inside the window, corrections included, and so is any
        // wheel a person turns (a free-spinning one is a few notches a frame at most).
        Assert.True(firstPartial is null or > 8, $"first partial {firstPartial}");
        Assert.True(firstFull is null or > 8);
        _ = notch;
    }
}
