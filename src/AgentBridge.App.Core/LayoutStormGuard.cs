namespace AgentBridge.App.Core;

// MITIGATION for #3782: corrections that follow a layout pass (restored reading position, anchor, pinned end)
// can feed each other until XAML throws LayoutCycleException. More than Limit passes within Window is a storm:
// those passes skip their corrections, and the first one is logged with its count.
public sealed class LayoutStormGuard(TimeProvider? time = null)
{
    public const int Limit = 60;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    readonly TimeProvider clock = time ?? TimeProvider.System;
    // Monotonic stamps: a wall-clock step (sync, DST, manual) must not end or extend a storm.
    readonly Queue<long> passes = [];

    // True when this pass may run its corrections; count is the passes in the window, this one included.
    public bool Pass(out int count)
    {
        lock (passes)
        {
            var now = clock.GetTimestamp();
            while (passes.Count > 0 && clock.GetElapsedTime(passes.Peek(), now) >= Window) { passes.Dequeue(); }
            passes.Enqueue(now);
            count = passes.Count;
            return count <= Limit;
        }
    }
}