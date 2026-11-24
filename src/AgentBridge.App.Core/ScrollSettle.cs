namespace AgentBridge.App.Core;

// The published-app self-test's "wheel down to the end" verdict (#4243), judged by STATE, never by how long the view takes. It once failed about one run in three on a worst-of-60-frames distance from
// the end ("quiet gap 771", and 1833 on another run) that a loaded hosted runner misses while the behaviour held (worst back 0). The view has SETTLED when, for a run of consecutive rendered frames,
// it sits at the end (within the tolerance), reports no movement, and neither its offset nor its extent changes: more history drawing, or the pin following it, restarts the run. It passes when it
// settles within a frame budget and never moved back up: the fall below the highest offset seen is tracked on every frame, seeded with what the notches before it saw, and stays strict.
public sealed class ScrollSettle
{
    readonly int stillFrames;
    readonly int maxFrames;
    readonly double tolerance;
    double high;
    double lastOffset = double.NaN;
    double lastExtent = double.NaN;
    int still;

    /// <param name="high">The highest offset seen so far (the notches before this).</param>
    /// <param name="worstBack">The worst fall below it seen so far.</param>
    /// <param name="stillFrames">Consecutive rendered frames at the end, unmoving and unchanged, that settle the view.</param>
    /// <param name="maxFrames">The frame budget (about 90 s at 60 fps; CI 45357085188 was still following history that drew for over 10 s, so 600 frames was too few): a view that has not settled by then fails, so a view that never arrives cannot hang the run.</param>
    /// <param name="tolerance">Pixels of rounding: a fall of no more than this is not moving back up, and this close to the end is at the end.</param>
    public ScrollSettle(double high, double worstBack = 0, int stillFrames = 30, int maxFrames = 5400, double tolerance = 1.5)
    {
        (this.high, WorstBack, this.stillFrames, this.maxFrames, this.tolerance) = (high, worstBack, stillFrames, maxFrames, tolerance);
    }

    public int Frames { get; private set; }

    /// <summary>The extent on the first frame, on the last, and on how many frames it changed: why a view that did not settle did not.</summary>
    public double FirstExtent { get; private set; }

    public double Extent { get; private set; }

    public int ExtentChanges { get; private set; }

    public double WorstBack { get; private set; }

    /// <summary>Distance from the end on the last frame.</summary>
    public double Gap { get; private set; }

    public bool Settled => still >= stillFrames;

    public bool Done => Settled || Frames >= maxFrames;

    public bool Passed => Settled && WorstBack <= tolerance;

    /// <summary>One rendered frame: where the view is, how far it can go, and whether it reports itself moving.</summary>
    public void Observe(double offset, double extent, bool moving)
    {
        if (Done) { return; }
        Frames++;
        WorstBack = Math.Max(WorstBack, high - offset);
        high = Math.Max(high, offset);
        Gap = extent - offset;
        if (Frames == 1) { FirstExtent = extent; }
        else if (extent != lastExtent) { ExtentChanges++; }
        Extent = extent;
        var unchanged = offset == lastOffset && extent == lastExtent;
        still = Gap <= tolerance && !moving && unchanged ? still + 1 : 0;
        (lastOffset, lastExtent) = (offset, extent);
    }

    public string Describe() => Settled
        ? $"settled at the end after {Frames} frames, worst back {WorstBack:0.#}"
        : $"did not settle in {Frames} frames: {Gap:0.#} from the end, {still} of {stillFrames} still frames, worst back {WorstBack:0.#}, extent {FirstExtent:0} to {Extent:0} (changed on {ExtentChanges} frames)";
}
