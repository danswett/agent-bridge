namespace AgentBridge.App.Core;

// The UI thread's frames through the self-test's live-resize pass (#765): each interval between
// two frames, in milliseconds, with the window's width class (ui.md §5.2) it was drawn at. The
// issue's bar is a 60 Hz display's: the 95th percentile within a frame, and no frame over two.
public sealed class FrameTimes
{
    public const double Budget = 16.7;
    public const double Long = 33;

    readonly List<(double Ms, PaneMode Mode, bool Switched)> frames = [];
    PaneMode? last;

    public int Count => frames.Count;

    // windowWidth: the window's width in effective pixels when the frame was drawn. A frame drawn at
    // another width class than the one before switched the layout, and is counted as such too.
    public void Add(double ms, double windowWidth)
    {
        var mode = PaneLayoutViewModel.ModeFor(windowWidth);
        frames.Add((ms, mode, last is { } before && before != mode));
        last = mode;
    }

    public FrameSummary All => FrameSummary.Of(frames.Select(f => f.Ms));
    public FrameSummary In(PaneMode mode) => FrameSummary.Of(frames.Where(f => f.Mode == mode).Select(f => f.Ms));
    // The frames that switched the layout, which a width class's own summary also holds.
    public FrameSummary Switching => FrameSummary.Of(frames.Where(f => f.Switched).Select(f => f.Ms));

    public Dictionary<string, FrameSummary> Report() => new()
    {
        ["all"] = All.Rounded(),
        ["threePanes"] = In(PaneMode.Three).Rounded(),
        ["twoPanes"] = In(PaneMode.Two).Rounded(),
        ["oneColumn"] = In(PaneMode.One).Rounded(),
        ["switching"] = Switching.Rounded(),
    };
}

// Percentiles by nearest rank, so each is a frame that was drawn. Meets: #765's bar, decided on
// the intervals as measured, never on a rounded p95.
public sealed record FrameSummary(int Frames, double P50, double P95, double Max, int OverBudget, int OverLong)
{
    public bool Meets { get; init; } = Frames > 0 && P95 <= FrameTimes.Budget && OverLong == 0;

    public static FrameSummary Of(IEnumerable<double> intervals)
    {
        var sorted = intervals.Order().ToArray();
        if (sorted.Length == 0) { return new FrameSummary(0, 0, 0, 0, 0, 0); }
        double Rank(double p) => sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1)];
        return new FrameSummary(sorted.Length, Rank(0.5), Rank(0.95), sorted[^1],
            sorted.Count(ms => ms > FrameTimes.Budget), sorted.Count(ms => ms > FrameTimes.Long));
    }

    // To the hundredth for the report, keeping whether it met the bar as measured.
    public FrameSummary Rounded() => this with { P50 = Math.Round(P50, 2), P95 = Math.Round(P95, 2), Max = Math.Round(Max, 2) };
}
