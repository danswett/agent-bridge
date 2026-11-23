namespace AgentBridge.App.Core;

// MITIGATION for #3782: corrections that follow a layout pass (restored reading position, anchor, pinned end)
// can feed each other until XAML throws LayoutCycleException. More than Limit passes within Window is a storm:
// those passes skip their corrections, and the first one is logged with its count.
public sealed class LayoutStormGuard
{
    public const int Limit = 60;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    readonly Queue<DateTimeOffset> passes = [];

    // True when this pass may run its corrections; count is the passes in the window, this one included.
    public bool Pass(DateTimeOffset now, out int count)
    {
        lock (passes)
        {
            while (passes.Count > 0 && now - passes.Peek() >= Window) { passes.Dequeue(); }
            passes.Enqueue(now);
            count = passes.Count;
            return count <= Limit;
        }
    }
}