namespace AgentBridge.App.Core;

// How the published-app wheel self-test (SelfTest.WheelNotch) judges a notch and a settled view: by what the
// person sees, not by numbers layout may change on its own (#4243).
public static class WheelNotchMeasure
{
    // How far a notch moved what is read: the block nearest the viewport's middle that is drawn both before and
    // after, by its top within the viewport. Rows re-measured elsewhere change the extent, not this. Null when
    // no block is drawn both times.
    public static double? OnScreenMove(IReadOnlyDictionary<int, double> topsBefore, IReadOnlyDictionary<int, double> topsAfter, double viewport)
    {
        var both = topsBefore.Keys.Where(topsAfter.ContainsKey).ToList();
        if (both.Count == 0) { return null; }
        var middle = both.MinBy(i => Math.Abs(topsBefore[i] - viewport / 2));
        return topsAfter[middle] - topsBefore[middle];
    }

    // Settled: the last `frames` frames show the same offset and extent, and no scroll under way.
    public static bool Settled(IReadOnlyList<(double Offset, double Extent, bool Moving)> samples, int frames)
    {
        if (samples.Count < frames) { return false; }
        var last = samples.Skip(samples.Count - frames).ToList();
        return last.All(s => !s.Moving && Math.Abs(s.Offset - last[0].Offset) < 0.5 && Math.Abs(s.Extent - last[0].Extent) < 0.5);
    }
}
