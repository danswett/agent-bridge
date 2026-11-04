using System.Collections.Concurrent;

namespace AgentBridge.Host.Delivery;

// What every delivery into a live session shares, answers and replies alike, so their keys
// can never interleave: one delivery per session at a time (a second is busy), and one
// worker typing at a time across all sessions. A host makes one and hands it to each
// service that types.
public sealed class DeliveryGate
{
    readonly ConcurrentDictionary<string, Guid> sessions = new(StringComparer.Ordinal);
    readonly SemaphoreSlim typing = new(1, 1);

    // Takes the session for this request, or false while another request holds it.
    public bool TryClaim(string sessionId, Guid request) => sessions.TryAdd(sessionId, request);

    public void Release(string sessionId, Guid request) => sessions.TryRemove(new KeyValuePair<string, Guid>(sessionId, request));

    // The one typing slot. Whoever holds it makes its last checks, types, and lets go.
    public Task WaitToTypeAsync() => typing.WaitAsync();

    public void DoneTyping() => typing.Release();
}

// Each request's outcome, by its UUID: a resend gets the first request's outcome (still
// pending, or settled) and starts nothing. Settled outcomes are forgotten after RememberFor;
// the contract promises at least 10 minutes.
internal sealed class OutcomeMemory<T>(TimeProvider time, TimeSpan rememberFor)
{
    readonly Lock gate = new();
    readonly Dictionary<Guid, Entry> requests = [];

    sealed class Entry(DeliveryTimeline? timeline)
    {
        public TaskCompletionSource<T> Outcome { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DeliveryTimeline? Timeline { get; } = timeline;
        public DateTimeOffset? Settled;
    }

    // For a request with no stages to report (a stop, a launch).
    public Task<T> Once(Guid key, Func<Task<T>> deliver, Action<T> settled) => Run(key, null, null, _ => deliver(), settled);

    // The known outcome for a repeat; otherwise runs deliver once, off the caller's channel:
    // a channel that closes mid-delivery must not cut it short, and a resend on the next
    // channel gets the outcome. Either way the watch follows the one delivery's stages.
    public Task<T> Once(Guid key, DeliveryWatch? watch, Func<DeliveryTimeline> timeline, Func<DeliveryTimeline, Task<T>> deliver, Action<T> settled) =>
        Run(key, watch, timeline, t => deliver(t!), settled);

    Task<T> Run(Guid key, DeliveryWatch? watch, Func<DeliveryTimeline>? timeline, Func<DeliveryTimeline?, Task<T>> deliver, Action<T> settled)
    {
        Entry entry;
        bool repeat;
        lock (gate)
        {
            Forget();
            repeat = requests.TryGetValue(key, out var known);
            entry = known ?? (requests[key] = new Entry(timeline?.Invoke()));
        }
        // Outside the lock: a repeat's watch may hear a stage already reached, at once.
        entry.Timeline?.Watch(watch, first: !repeat);
        if (repeat) { return entry.Outcome.Task; }
        _ = Task.Run(async () =>
        {
            var result = await deliver(entry.Timeline);
            lock (gate) { entry.Settled = time.GetUtcNow(); }
            settled(result);
            entry.Timeline?.Settled();
            entry.Outcome.SetResult(result);
        }, CancellationToken.None);
        return entry.Outcome.Task;
    }

    void Forget()
    {
        var before = time.GetUtcNow() - rememberFor;
        foreach (var (key, entry) in requests.Where(r => r.Value.Settled < before).ToList()) { requests.Remove(key); }
    }
}
