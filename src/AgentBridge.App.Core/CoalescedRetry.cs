namespace AgentBridge.App.Core;

// One pending retry at a time (#3811): a restore skipped by a layout storm is tried again once after the window,
// however many passes were skipped meanwhile. A request made by the retry itself schedules the next one.
public sealed class CoalescedRetry
{
    readonly object gate = new();
    bool pending;

    public void Request(TimeSpan delay, Action<TimeSpan, Action> schedule, Action work)
    {
        lock (gate)
        {
            if (pending) { return; }
            pending = true;
        }
        schedule(delay, () => { lock (gate) { pending = false; } work(); });
    }
}
