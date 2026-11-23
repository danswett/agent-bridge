namespace AgentBridge.App.Core;

// The app's last-chance rule for a UI-thread exception: keep running, but only while the exceptions are few.
public sealed class UiExceptionGuard
{
    public const int Limit = 3;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    readonly Queue<DateTimeOffset> survived = [];

    // Only survived exceptions count: a refused one ends the app, so it cannot extend a storm.
    public bool Allow(DateTimeOffset now)
    {
        lock (survived)
        {
            while (survived.Count > 0 && now - survived.Peek() >= Window) { survived.Dequeue(); }
            if (survived.Count >= Limit) { return false; }
            survived.Enqueue(now);
            return true;
        }
    }
}