namespace AgentBridge.App.Core;

// Corrections that follow a layout pass (the restored reading position, the anchor, the pinned end and the scroll mode
// they set) run once per dispatcher turn, never inside the layout event: setting layout properties there starts the
// next layout pass, and corrections that feed each other end in a LayoutCycleException (#3782). Layout that the work
// itself causes runs it at most once more in that turn.
public sealed class DeferredPass
{
    readonly object gate = new();
    bool queued, running, again;

    public void Request(Action<Action> post, Action work)
    {
        lock (gate)
        {
            if (running) { again = true; return; }
            if (queued) { return; }
            queued = true;
        }
        post(() => Run(work));
    }

    void Run(Action work)
    {
        lock (gate) { queued = false; }
        for (var pass = 0; pass < 2; pass++)
        {
            lock (gate) { running = true; again = false; }
            try { work(); }
            finally { lock (gate) { running = false; } }
            lock (gate) { if (!again) { return; } }
        }
    }
}