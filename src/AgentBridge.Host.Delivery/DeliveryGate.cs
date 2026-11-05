using System.Collections.Concurrent;

namespace AgentBridge.Host.Delivery;

// What every delivery into a live session shares, answers and replies alike, so their keys
// can never interleave: one delivery per session at a time (a second is busy), and one
// worker typing at a time across all sessions. A host makes one and hands it to each
// service that types.
public sealed class DeliveryGate
{
    readonly Lock state = new();
    readonly ConcurrentDictionary<string, Guid> sessions = new(StringComparer.Ordinal);
    readonly SemaphoreSlim typing = new(1, 1);
    readonly TimeProvider time;
    int deliveries;
    bool isTyping;
    bool uncertain;
    Exclusion? exclusion;

    public DeliveryGate(TimeProvider? time = null) => this.time = time ?? TimeProvider.System;

    public enum Blocker { Typing, DeliveryInFlight, LifecycleInProgress, JournalUncertain }

    public void MarkJournalUncertain() { lock (state) { uncertain = true; } }

    // Admission must precede OutcomeMemory's Task.Run, not just the later session claim.
    public IDisposable? TryEnterDelivery()
    {
        lock (state)
        {
            Expire();
            if (exclusion is not null) { return null; }
            deliveries++;
            return new Delivery(this);
        }
    }

    sealed class Delivery(DeliveryGate owner) : IDisposable
    {
        int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
            lock (owner.state) { owner.deliveries--; }
        }
    }

    public Exclusion? TryExclude(TimeSpan lifetime, out Blocker blocker)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromSeconds(30)) { throw new ArgumentOutOfRangeException(nameof(lifetime)); }
        lock (state)
        {
            Expire();
            blocker = uncertain ? Blocker.JournalUncertain : isTyping ? Blocker.Typing
                : deliveries > 0 || !sessions.IsEmpty ? Blocker.DeliveryInFlight : Blocker.LifecycleInProgress;
            if (uncertain || isTyping || deliveries > 0 || !sessions.IsEmpty || exclusion is not null) { return null; }
            return exclusion = new Exclusion(this, lifetime);
        }
    }

    void Expire()
    {
        if (exclusion is { Committed: false } held && time.GetElapsedTime(held.Started) >= held.Lifetime)
        {
            exclusion = null;
            // Never call a host/transport callback while holding the delivery lock.
            _ = Task.Run(held.NotifyExpired);
        }
    }

    public sealed class Exclusion : IDisposable
    {
        readonly DeliveryGate owner;
        readonly ITimer timer;
        internal long Started { get; }
        internal TimeSpan Lifetime { get; }
        internal bool Committed { get; private set; }
        public event Action? Expired;
        internal void NotifyExpired() => Expired?.Invoke();

        internal Exclusion(DeliveryGate owner, TimeSpan lifetime)
        {
            this.owner = owner;
            Started = owner.time.GetTimestamp();
            Lifetime = lifetime;
            timer = owner.time.CreateTimer(_ => { lock (owner.state) { owner.Expire(); } }, null, lifetime, Timeout.InfiniteTimeSpan);
        }

        public bool IsCurrent
        {
            get { lock (owner.state) { owner.Expire(); return ReferenceEquals(owner.exclusion, this); } }
        }

        public bool TryCommit(Func<Blocker?, bool> allow)
        {
            lock (owner.state)
            {
                owner.Expire();
                if (!ReferenceEquals(owner.exclusion, this) || Committed) { return false; }
                Blocker? blocker = owner.uncertain ? Blocker.JournalUncertain : owner.isTyping ? Blocker.Typing
                    : owner.deliveries > 0 || !owner.sessions.IsEmpty ? Blocker.DeliveryInFlight : null;
                var accepted = allow(blocker);
                owner.Expire();
                if (!accepted || owner.uncertain || !ReferenceEquals(owner.exclusion, this))
                {
                    if (ReferenceEquals(owner.exclusion, this)) { owner.exclusion = null; }
                    return false;
                }
                Committed = true;
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner.state) { if (ReferenceEquals(owner.exclusion, this)) { owner.exclusion = null; } }
            timer.Dispose();
        }
    }

    // Takes the session for this request, or false while another request holds it.
    public bool TryClaim(string sessionId, Guid request)
    {
        lock (state)
        {
            Expire();
            return exclusion is null && sessions.TryAdd(sessionId, request);
        }
    }

    public void Release(string sessionId, Guid request) => sessions.TryRemove(new KeyValuePair<string, Guid>(sessionId, request));

    // The one typing slot. Whoever holds it makes its last checks, types, and lets go.
    public async Task WaitToTypeAsync()
    {
        await typing.WaitAsync();
        lock (state)
        {
            Expire();
            if (exclusion is not null)
            {
                typing.Release();
                throw new InvalidOperationException("the host is preparing a departure");
            }
            isTyping = true;
        }
    }

    public void DoneTyping()
    {
        lock (state) { isTyping = false; }
        typing.Release();
    }
}

// Each request's outcome, by its UUID: a resend gets the first request's outcome (still
// pending, or settled) and starts nothing. Settled outcomes are forgotten after RememberFor;
// the contract promises at least 10 minutes.
internal sealed class OutcomeMemory<T>(TimeProvider time, TimeSpan rememberFor, DeliveryGate? deliveryGate = null, Func<T>? blocked = null)
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
        IDisposable? delivery = null;
        lock (gate)
        {
            Forget();
            repeat = requests.TryGetValue(key, out var known);
            if (!repeat && deliveryGate is not null && (delivery = deliveryGate.TryEnterDelivery()) is null)
            {
                return Task.FromResult(blocked is null ? throw new InvalidOperationException("no departure refusal was supplied") : blocked());
            }
            try { entry = known ?? (requests[key] = new Entry(timeline?.Invoke())); }
            catch { delivery?.Dispose(); throw; }
        }
        // Outside the lock: a repeat's watch may hear a stage already reached, at once.
        entry.Timeline?.Watch(watch, first: !repeat);
        if (repeat) { return entry.Outcome.Task; }
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await deliver(entry.Timeline);
                lock (gate) { entry.Settled = time.GetUtcNow(); }
                settled(result);
                entry.Timeline?.Settled();
                delivery?.Dispose();
                entry.Outcome.SetResult(result);
            }
            catch (Exception e)
            {
                deliveryGate?.MarkJournalUncertain();
                lock (gate) { entry.Settled = time.GetUtcNow(); }
                entry.Outcome.TrySetException(e);
            }
            finally { delivery?.Dispose(); }
        }, CancellationToken.None);
        return entry.Outcome.Task;
    }

    void Forget()
    {
        var before = time.GetUtcNow() - rememberFor;
        foreach (var (key, entry) in requests.Where(r => r.Value.Settled < before).ToList()) { requests.Remove(key); }
    }
}
