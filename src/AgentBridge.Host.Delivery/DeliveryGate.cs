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

    sealed class Entry(Task<T> outcome)
    {
        public Task<T> Outcome { get; } = outcome;
        public DateTimeOffset? Settled;
    }

    // The known outcome for a repeat; otherwise runs deliver once, off the caller's channel:
    // a channel that closes mid-delivery must not cut it short, and a resend on the next
    // channel gets the outcome.
    public Task<T> Once(Guid key, Func<Task<T>> deliver, Action<T> settled)
    {
        TaskCompletionSource<T> outcome;
        Entry entry;
        lock (gate)
        {
            Forget();
            if (requests.TryGetValue(key, out var known)) { return known.Outcome; }
            outcome = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            entry = new Entry(outcome.Task);
            requests[key] = entry;
        }
        _ = Task.Run(async () =>
        {
            var result = await deliver();
            lock (gate) { entry.Settled = time.GetUtcNow(); }
            settled(result);
            outcome.SetResult(result);
        }, CancellationToken.None);
        return outcome.Task;
    }

    void Forget()
    {
        var before = time.GetUtcNow() - rememberFor;
        foreach (var (key, entry) in requests.Where(r => r.Value.Settled < before).ToList()) { requests.Remove(key); }
    }
}
