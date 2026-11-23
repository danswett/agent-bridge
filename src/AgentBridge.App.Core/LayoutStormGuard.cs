namespace AgentBridge.App.Core;

// MITIGATION for #3782: corrections that follow a layout pass (restored reading position, anchor, pinned end)
// can feed each other until XAML throws LayoutCycleException. Only a correction that actually ran can feed the
// next pass, so Limit counts correction RUNS within Window, not raw LayoutUpdated passes: ordinary streaming
// raises many passes that correct nothing (#3842). At Limit runs, further corrections are skipped and the first
// skip of a storm is logged with its count.
public sealed class LayoutStormGuard(TimeProvider? time = null)
{
    public const int Limit = 60;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    readonly TimeProvider clock = time ?? TimeProvider.System;
    // Monotonic stamps: a wall-clock step (sync, DST, manual) must not end or extend a storm.
    readonly Queue<long> runs = [];
    bool announced;
    int peak, storms;

    // The most correction runs seen in one window, and how many storms were announced: the self-test reads both.
    public int Peak { get { lock (runs) { return peak; } } }
    public int Storms { get { lock (runs) { return storms; } } }

    // Stamps held: never more than Limit, so a dense burst cannot grow the queue (#3811).
    internal int Retained { get { lock (runs) { return runs.Count; } } }

    // True when a correction may run; count is the runs in the window. first is true once per storm, for the log.
    public bool Allowed(out int count, out bool first)
    {
        lock (runs)
        {
            Prune(clock.GetTimestamp());
            count = runs.Count;
            var allowed = count < Limit;
            first = !allowed && !announced;
            announced = !allowed;
            if (first) { storms++; }
            return allowed;
        }
    }

    // A correction ran: it moved the view, so it may cause another layout pass.
    public void Run()
    {
        lock (runs)
        {
            var now = clock.GetTimestamp();
            Prune(now);
            // At Limit the window is already full; the oldest stamps decide when it ends, so later ones add nothing.
            if (runs.Count < Limit) { runs.Enqueue(now); }
            peak = Math.Max(peak, runs.Count);
        }
    }

    void Prune(long now)
    {
        while (runs.Count > 0 && clock.GetElapsedTime(runs.Peek(), now) >= Window) { runs.Dequeue(); }
    }
}
