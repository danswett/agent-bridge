namespace AgentBridge.App.Tests;

// #4243: the transcript's wheel and trackpad scrolling, measured offline through the end-pin harness
// (ConversationEndPinHarness), with no product change. Each input asks the view to move one step; the layout
// then realises a row off its estimate and the top block's correction may ask for another place. A sample is
// what the input asked for against where the view actually was once the layout had settled, so a fix is
// pointed at it with Assert.True(ScrollMeasure.Steady(samples)) and the table (Table) names the notch that broke.
public enum ScrollInput { Wheel, Pan }

public readonly record struct ScrollSample(int Step, double From, double Requested, double Actual, bool Corrected)
{
    // How far the view really moved on this step, against the Requested - From it was asked for.
    public double Moved => Actual - From;
}

public static class ScrollMeasure
{
    public const double Notch = 48;

    // Tall tool rows (estimated 48, real 160) and text rows (estimated 48, real 90) realising around the top block
    // as the view goes by: positive when the block is taken lower than it was, negative when a row shrinks under it.
    public static readonly IReadOnlyList<double> TallToolRows = [112, -30, 112, 42, -30, 112, 42, 112, -30, 42];

    // Steps toward the end from start, by a wheel (a final view change per notch, nothing held between them) or a
    // trackpad pan (the pan holds the view, its steps intermediate, until it is let go and one final change settles it).
    public static IReadOnlyList<ScrollSample> Run(SessionView view, ScrollInput input, double start, int steps, IReadOnlyList<double>? growth = null, double step = Notch)
    {
        growth ??= TallToolRows;
        var samples = new List<ScrollSample>();
        var offset = start;
        view.Moved(offset);
        view.Scroller.Targets.Clear();
        if (input == ScrollInput.Pan) { view.GestureForProbe("pan"); }
        for (var i = 0; i < steps; i++)
        {
            var requested = Math.Clamp(offset + step, 0, view.Scroller.ScrollableHeight);
            if (input == ScrollInput.Wheel) { view.Scroller.Wheel(); }
            view.Moved(requested, final: input == ScrollInput.Wheel);
            offset = Settle(view, growth[i % growth.Count], requested, out var corrected);
            samples.Add(new(i, samples.Count == 0 ? start : samples[^1].Actual, requested, offset, corrected));
        }
        if (input == ScrollInput.Pan)
        {
            // Let go: the view is still again, and the layout since may now be put back.
            view.GestureForProbe("none");
            view.Moved(offset);
            var released = Settle(view, growth[steps % growth.Count], offset, out var corrected);
            samples.Add(new(steps, offset, offset, released, corrected));
        }
        return samples;
    }

    // The anchor was taken at the view change; the layout then moved the top block by growth, and the correction runs.
    static double Settle(SessionView view, double growth, double requested, out bool corrected)
    {
        view.TakeAnchorForProbe(40 - growth);
        view.LaidOutForProbe();
        view.DrainForProbe();
        corrected = view.Scroller.Targets.Count > 0;
        var actual = corrected ? view.Scroller.Targets[^1] : requested;
        view.Scroller.Targets.Clear();
        view.Scroller.VerticalOffset = actual;
        return actual;
    }

    // Every step moved the view as asked, so each notch went the same distance (a step against the end is clamped
    // by the request itself), and the view never went back.
    public static bool Steady(IEnumerable<ScrollSample> samples) => samples.All(s => !s.Corrected && s.Actual >= s.From);

    public static string Table(IEnumerable<ScrollSample> samples) =>
        string.Join('\n', samples.Select(s => $"step {s.Step,2}: from {s.From,8:0.#} asked {s.Requested,8:0.#} got {s.Actual,8:0.#} moved {s.Moved,6:0.#}{(s.Corrected ? "  CORRECTED" : "")}"));
}
