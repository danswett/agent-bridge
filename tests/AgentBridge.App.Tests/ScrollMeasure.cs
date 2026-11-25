namespace AgentBridge.App.Tests;

// #4243 / #4301: the transcript's wheel and trackpad scrolling, measured offline through the end-pin harness
// (ConversationEndPinHarness). Each input asks the view to move one step; the layout then realises a row off its
// estimate, which moves the block being read, and the top-block correction may ask for another offset to put it back.
//
// What a person sees is where the READ BLOCK is on screen, not the scroller's raw offset: a row realised above it
// that is 112 px taller than estimated pushes the block down 112, and the correction's +112 on the offset takes
// exactly that back. A sample therefore records the block's on-screen displacement for the step (Visible), and
// what it would have been had the correction been held back (VisibleIfHeld).
// Inertia (#4607): a trackpad's tail after the last input. The last wheel event has come; the view then coasts, its
// changes intermediate with the scroller moving, until one final change. Like a pan, it is the person's until it ends.
public enum ScrollInput { Wheel, Pan, Inertia }

public readonly record struct ScrollSample(int Step, double From, double Requested, double Actual, bool Corrected, double Growth)
{
    // How far the read block moved on screen: the layout's growth above it, less what the offset moved (positive is down).
    public double Visible => Growth - (Actual - From);
    // The same, had the correction not asked for another place.
    public double VisibleIfHeld => Growth - (Requested - From);
    // What the input asked of the block (a notch down is minus one notch).
    public double Asked => -(Requested - From);
}

public static class ScrollMeasure
{
    public const double Notch = 48;

    // Tall tool rows (estimated 48, real 160) and text rows (estimated 48, real 90) realising above the top block
    // as the view goes by: positive when the block is pushed down, negative when a row shrinks under it.
    public static readonly IReadOnlyList<double> TallToolRows = [112, -30, 112, 42, -30, 112, 42, 112, -30, 42];

    // Steps toward the end from start, by a wheel (a final view change per notch, nothing held between them) or a
    // trackpad pan (the pan holds the view, its steps intermediate, until it is let go and one final change settles it).
    // stepSizes gives each step its own distance (a trackpad's decaying tail, negative is up); wheelDelta is what each wheel event reports (a trackpad's sub-notch 15, say; negative is down).
    public static IReadOnlyList<ScrollSample> Run(SessionView view, ScrollInput input, double start, int steps, IReadOnlyList<double>? growth = null, double step = Notch,
        IReadOnlyList<double>? stepSizes = null, int wheelDelta = 120)
    {
        growth ??= TallToolRows;
        var samples = new List<ScrollSample>();
        var offset = start;
        view.Moved(offset);
        view.Scroller.Targets.Clear();
        if (input == ScrollInput.Pan) { view.GestureForProbe("pan"); }
        if (input == ScrollInput.Inertia) { view.Scroller.Wheel(wheelDelta); view.GestureForProbe("scroll"); }
        for (var i = 0; i < steps; i++)
        {
            var requested = Math.Clamp(offset + (stepSizes is null ? step : stepSizes[i % stepSizes.Count]), 0, view.Scroller.ScrollableHeight);
            if (input == ScrollInput.Wheel) { view.Scroller.Wheel(wheelDelta); }
            view.Moved(requested, final: input == ScrollInput.Wheel);
            var from = offset;
            var g = growth[i % growth.Count];
            offset = Settle(view, g, requested, out var corrected);
            samples.Add(new(i, from, requested, offset, corrected, g));
        }
        if (input != ScrollInput.Wheel)
        {
            // Let go (or the tail ends): the view is still again, and the layout since may now be put back.
            view.GestureForProbe("none");
            view.Moved(offset);
            var g = growth[steps % growth.Count];
            var released = Settle(view, g, offset, out var corrected);
            samples.Add(new(steps, offset, offset, released, corrected, g));
        }
        return samples;
    }

    // The anchor was taken at the view change; the layout then moved the top block by growth, and the correction runs.
    static double Settle(SessionView view, double growth, double requested, out bool corrected)
    {
        // Rows realised taller than estimated also lengthen what there is to scroll, by as much.
        view.Scroller.ScrollableHeight += growth;
        view.TakeAnchorForProbe(40 - growth);
        view.LaidOutForProbe();
        view.DrainForProbe();
        corrected = view.Scroller.Targets.Count > 0;
        var actual = corrected ? view.Scroller.Targets[^1] : requested;
        view.Scroller.Targets.Clear();
        view.Scroller.VerticalOffset = actual;
        return actual;
    }

    // Every step moved the read block on screen exactly as asked (a notch, a clamp at the end, nothing at a pan's
    // release), whatever the layout realised above it: the correction takes the growth back.
    public static bool Steady(IEnumerable<ScrollSample> samples) => samples.All(s => Math.Abs(s.Visible - s.Asked) < 0.5);

    public static string Table(IEnumerable<ScrollSample> samples) =>
        string.Join("\n", samples.Select(s => $"step {s.Step,2}: asked {s.Asked,5:0.#}  layout pushed {s.Growth,5:0.#}  offset {s.From,8:0.#} -> {s.Actual,8:0.#}  block moves {s.Visible,5:0.#} on screen (held back: {s.VisibleIfHeld,5:0.#}){(s.Corrected ? "  corrected" : "")}"));
}
