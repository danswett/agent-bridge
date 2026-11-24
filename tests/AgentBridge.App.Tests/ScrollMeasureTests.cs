using Xunit;
using Xunit.Abstractions;

namespace AgentBridge.App.Tests;

// The measuring harness for #4243 / #4301 (ScrollMeasure): where the block being read goes on screen for each wheel
// notch or trackpad pan step, near the end of a long transcript with tall tool rows and in the middle of it.
public sealed class ScrollMeasureTests(ITestOutputHelper output)
{
    const double Extent = 48000;

    static SessionView View()
    {
        var view = new SessionView();
        (view.Scroller.ViewportHeight, view.Scroller.ScrollableHeight) = (600, Extent);
        return view;
    }

    // A row realised above the top block pushes the read block down by what it grew; the correction raises the
    // offset by the same, so the raw offset moves a notch plus the growth (160 for a notch and a 112 row) while the
    // block moves exactly one notch on screen. Every notch, and a pan's release, must look the same to the eye.
    [Theory]
    [InlineData(ScrollInput.Wheel, Extent - 240)]
    [InlineData(ScrollInput.Wheel, Extent / 2)]
    [InlineData(ScrollInput.Pan, Extent - 240)]
    [InlineData(ScrollInput.Pan, Extent / 2)]
    public void Every_step_moves_the_read_block_on_screen_as_asked_whatever_realises_above_it(ScrollInput input, double start)
    {
        var samples = ScrollMeasure.Run(View(), input, start, 12);
        output.WriteLine($"{input} from {start} (extent {Extent}), tall tool rows:\n{ScrollMeasure.Table(samples)}");
        Assert.Equal(12 + (input == ScrollInput.Pan ? 1 : 0), samples.Count);
        Assert.All(samples.Where(s => s.Step < 12), s => Assert.InRange(s.Requested - s.From, 0, ScrollMeasure.Notch));
        if (input == ScrollInput.Wheel)
        {
            Assert.True(ScrollMeasure.Steady(samples), ScrollMeasure.Table(samples));
            return;
        }
        // A pan is the person's until it ends (the correction never fights a finger or a touchpad, #1756): the layout's
        // growth reaches the screen meanwhile, and at the release the view is still again, nothing more to undo.
        Assert.All(samples.Where(s => s.Step < 12), s => Assert.False(s.Corrected));
        Assert.True(ScrollMeasure.Steady(samples.Skip(12)), ScrollMeasure.Table(samples));
    }

    [Theory]
    [InlineData(ScrollInput.Wheel)]
    [InlineData(ScrollInput.Pan)]
    public void A_transcript_with_no_estimate_errors_moves_every_step_as_asked_and_stops_at_the_end(ScrollInput input)
    {
        var samples = ScrollMeasure.Run(View(), input, Extent - 240, 12, growth: [0]);
        Assert.True(ScrollMeasure.Steady(samples), ScrollMeasure.Table(samples));
        Assert.All(samples, s => Assert.False(s.Corrected));
        Assert.Equal(Extent, samples[^1].Actual);
    }

    // The measure is only worth anything if it tells a view that does the wrong thing: holding the correction back
    // (the objection on #4301) would leave the block where the layout pushed it, 64 px down against a wheel
    // that is scrolling down, then 78 px up, then a notch: no two notches alike.
    [Fact]
    public void The_measure_tells_a_held_back_correction_from_one_that_keeps_the_block_a_notch_a_step()
    {
        var samples = ScrollMeasure.Run(View(), ScrollInput.Wheel, Extent / 2, 10);
        Assert.True(ScrollMeasure.Steady(samples));
        var held = samples.Select(s => s.VisibleIfHeld).ToArray();
        output.WriteLine($"on screen, correction held back: {string.Join(", ", held.Select(d => d.ToString("0.#")))}");
        Assert.Contains(held, d => d > 0);
        Assert.Contains(held, d => d < -ScrollMeasure.Notch);
        Assert.False(samples.Select(s => new ScrollSample(s.Step, s.From, s.Requested, s.Requested, false, s.Growth)).All(h => Math.Abs(h.Visible - h.Asked) < 0.5));
    }

    // The blank viewport the ARM64 self-test saw mid wheel burst (phase control-wheel:rapid-burst, 8 notches a frame,
    // viewport 363, list CacheLength 2) is not explained by our corrections: the list has realised the viewport and
    // CacheLength (2) viewports either side as of the last layout, and what the view moves relative to its ROWS in a
    // frame is what the person asked (the correction only cancels the growth above), so the self-test's 8 notches
    // (384 px) stay well inside the 726 px below the viewport that are realised. Realisation speed is WinUI's, and
    // slower on ARM64: not modelled here.
    [Fact]
    public void A_rapid_wheel_burst_moves_the_view_across_its_rows_by_no_more_than_the_realised_window_holds()
    {
        const double viewport = 363, cacheLength = 2;
        var rows = new List<string>();
        int? firstPartial = null, firstFull = null;
        for (var perFrame = 1; perFrame <= 40; perFrame++)
        {
            var view = View();
            view.Scroller.ViewportHeight = viewport;
            var samples = ScrollMeasure.Run(view, ScrollInput.Wheel, Extent / 2, perFrame);
            Assert.True(ScrollMeasure.Steady(samples));
            // How far the viewport moved across the rows: the block's displacement on screen, taken the other way.
            var across = -samples.Sum(s => s.Visible);
            var realisedBelow = viewport + cacheLength * viewport;
            var partial = across + viewport > realisedBelow;
            var full = across > realisedBelow;
            if (partial) { firstPartial ??= perFrame; }
            if (full) { firstFull ??= perFrame; }
            rows.Add($"{perFrame,2} notches a frame: the view moves {across,6:0.#} px across its rows ({realisedBelow:0.#} realised below the top edge){(full ? "  NO ROW IN VIEW" : partial ? "  partly past the realised rows" : "")}");
        }
        output.WriteLine(string.Join("\n", rows));
        output.WriteLine($"first frame size with rows missing from part of the view: {firstPartial}; with none in view: {firstFull}");
        // 8 notches a frame, the self-test's rate, is inside the window; a free-spinning wheel is a few a frame at most.
        Assert.True(firstPartial is null or > 8, $"first partial {firstPartial}");
        Assert.True(firstFull is null or > 8, $"first full {firstFull}");
    }
}
