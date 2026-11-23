namespace AgentBridge.App.Core;

// The app's last-chance rule for a UI-thread exception: keep running, but only while the exceptions are few.
public sealed class UiExceptionGuard(TimeProvider? time = null)
{
    public const int Limit = 3;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    readonly TimeProvider clock = time ?? TimeProvider.System;
    // Monotonic stamps: a wall-clock step (sync, DST, manual) must not allow more survivals in a storm.
    readonly Queue<long> survived = [];

    // Only survived exceptions count: a refused one ends the app, so it cannot extend a storm.
    public bool Allow()
    {
        lock (survived)
        {
            var now = clock.GetTimestamp();
            while (survived.Count > 0 && clock.GetElapsedTime(survived.Peek(), now) >= Window) { survived.Dequeue(); }
            if (survived.Count >= Limit) { return false; }
            survived.Enqueue(now);
            return true;
        }
    }
}