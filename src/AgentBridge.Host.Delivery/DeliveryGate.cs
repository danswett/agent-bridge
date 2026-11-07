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
        internal void Complete<T>(TaskCompletionSource<T> outcome, T result, Exception? error)
        {
            lock (owner.state)
            {
                if (error is null) { outcome.SetResult(result); }
                else { outcome.TrySetException(error); }
                Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
            lock (owner.state) { owner.deliveries--; }
            owner.AdmissionReleased?.Invoke();
        }
    }

    // Test hook: runs right after a delivery's admission is released, so a test can assert
    // the outcome had already completed (a quiet grant could otherwise overtake it).
    internal Action? AdmissionReleased;

    internal void Complete<T>(IDisposable admission, TaskCompletionSource<T> outcome, T result, Exception? error = null) =>
        ((Delivery)admission).Complete(outcome, result, error);

    internal Exclusion? TryExclude(TimeSpan lifetime, out Blocker blocker)
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
        if (exclusion is { Committed: false, Committing: false } held && time.GetElapsedTime(held.Started) >= held.Lifetime)
        {
            exclusion = null;
            // Never call a host/transport callback while holding the delivery lock.
            _ = Task.Run(held.NotifyExpired);
        }
    }

    internal sealed class Exclusion : IDisposable
    {
        readonly DeliveryGate owner;
        readonly ITimer timer;
        internal long Started { get; }
        internal TimeSpan Lifetime { get; }
        internal bool Committed { get; private set; }
        internal bool Committing { get; private set; }
        // Monotonic and lock-free: Started and Lifetime never change.
        internal bool LifetimeElapsed => owner.time.GetElapsedTime(Started) >= Lifetime;
        internal event Action? Expired;
        internal void NotifyExpired() => Expired?.Invoke();

        internal Exclusion(DeliveryGate owner, TimeSpan lifetime)
        {
            this.owner = owner;
            Started = owner.time.GetTimestamp();
            Lifetime = lifetime;
            timer = owner.time.CreateTimer(_ => { lock (owner.state) { owner.Expire(); } }, null, lifetime, Timeout.InfiniteTimeSpan);
        }

        internal bool IsCurrent
        {
            get { lock (owner.state) { owner.Expire(); return ReferenceEquals(owner.exclusion, this); } }
        }

        internal bool TryBeginCommit(out Blocker? blocker)
        {
            lock (owner.state)
            {
                owner.Expire();
                blocker = owner.uncertain ? Blocker.JournalUncertain : owner.isTyping ? Blocker.Typing
                    : owner.deliveries > 0 || !owner.sessions.IsEmpty ? Blocker.DeliveryInFlight : null;
                if (!ReferenceEquals(owner.exclusion, this) || Committed || Committing) { return false; }
                Committing = true;
                return true;
            }
        }

        internal bool TryCommit(DepartureConnection connection, long generation, bool negotiated, long revision, out bool connectionChanged)
        {
            lock (owner.state)
            {
                connectionChanged = false;
                if (!ReferenceEquals(owner.exclusion, this) || !Committing || Committed
                    || owner.uncertain || owner.isTyping || owner.deliveries > 0 || !owner.sessions.IsEmpty
                    || owner.time.GetElapsedTime(Started) >= Lifetime) { return false; }
                lock (connection.Sync)
                {
                    if (!connection.Matches(generation, negotiated, revision)) { connectionChanged = true; return false; }
                    Committed = true;
                    Committing = false;
                    return true;
                }
            }
        }

        internal void Dispose()
        {
            lock (owner.state)
            {
                if (Committed) { return; }
                if (ReferenceEquals(owner.exclusion, this)) { owner.exclusion = null; }
            }
            timer.Dispose();
        }

        void IDisposable.Dispose() => Dispose();
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
        // What a read-only recheck has made of the settled outcome since (#467): the latest one,
        // finished or still reading, which a repeat then gets.
        public Task<T>? Rechecked;
        // Who admitted it, and for what (an answer's sender, session and question): a lookup by
        // the request's UUID is a repeat only when it says the same, decided under the lock.
        public object? Owner;
    }

    // Whether the request is remembered now, pending or settled. Reads only: nothing is started.
    public bool Knows(Guid key)
    {
        lock (gate)
        {
            Forget();
            return requests.ContainsKey(key);
        }
    }

    // For a request with no stages to report (a stop, a launch).
    public Task<T> Once(Guid key, Func<Task<T>> deliver, Action<T> settled) => Run(key, null, null, _ => deliver(), settled);

    // The known outcome for a repeat; otherwise runs deliver once, off the caller's channel:
    // a channel that closes mid-delivery must not cut it short, and a resend on the next
    // channel gets the outcome. Either way the watch follows the one delivery's stages.
    // recheck, given a settled outcome, may say what it has come to since (an unconfirmed answer
    // the session recorded late, #467); it must never act, only read, and what it says is kept as
    // the request's outcome from then on. It may take a bounded moment, and a repeat meanwhile
    // gets that same recheck's outcome.
    // owner: what the request was admitted for. A UUID already remembered for another owner is
    // not a repeat: nothing of its outcome is given, no watch is attached, and foreign (which
    // must start a request of its own under another UUID) answers instead.
    // repeated: how a repeat's outcome is said, applied to every call that is not the one
    // admission, decided under the same lock as the admission.
    public Task<T> Once(Guid key, DeliveryWatch? watch, Func<DeliveryTimeline> timeline, Func<DeliveryTimeline, Task<T>> deliver, Action<T> settled,
        Func<T, Task<T?>>? recheck = null, object? owner = null, Func<Task<T>>? foreign = null, Func<T, T>? repeated = null) =>
        Run(key, watch, timeline, t => deliver(t!), settled, recheck, owner, foreign, repeated);

    Task<T> Run(Guid key, DeliveryWatch? watch, Func<DeliveryTimeline>? timeline, Func<DeliveryTimeline?, Task<T>> deliver, Action<T> settled,
        Func<T, Task<T?>>? recheck = null, object? owner = null, Func<Task<T>>? foreign = null, Func<T, T>? repeated = null)
    {
        Entry entry = null!;
        bool repeat = false;
        bool divert = false;
        IDisposable? delivery = null;
        Task<T>? rechecked = null;
        // A recheck started by this repeat waits for its watch: one that reads the late record
        // and confirms at once would otherwise confirm before the repeat's arrival is taken, and
        // its timings say confirmed 0 (#510 review).
        TaskCompletionSource? watching = null;
        lock (gate)
        {
            Forget();
            repeat = requests.TryGetValue(key, out var known);
            if (repeat && foreign is not null && !Equals(known!.Owner, owner)) { divert = true; }
            else
            {
                if (!repeat && deliveryGate is not null && (delivery = deliveryGate.TryEnterDelivery()) is null)
                {
                    return Task.FromResult(blocked is null ? throw new InvalidOperationException("no departure refusal was supplied") : blocked());
                }
                try { entry = known ?? (requests[key] = new Entry(timeline?.Invoke()) { Owner = owner }); }
                catch { delivery?.Dispose(); throw; }
                if (repeat && recheck is not null && entry.Settled is not null)
                {
                    var latest = entry.Rechecked ?? entry.Outcome.Task;
                    // One still reading is the answer to this repeat too; a finished one is read again.
                    if (latest.IsCompletedSuccessfully)
                    {
                        watching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        rechecked = entry.Rechecked = Recheck(recheck, latest.Result, watching.Task);
                    }
                    else { rechecked = entry.Rechecked = latest; }
                }
            }
        }
        if (divert) { return foreign!(); }
        // Outside the lock: a repeat's watch may hear a stage already reached, at once.
        entry.Timeline?.Watch(watch, first: !repeat);
        watching?.SetResult();
        if (repeat)
        {
            var said = rechecked ?? entry.Rechecked ?? entry.Outcome.Task;
            return repeated is null ? said : Said(said, repeated);
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await deliver(entry.Timeline);
                lock (gate) { entry.Settled = time.GetUtcNow(); }
                settled(result);
                entry.Timeline?.Settled();
                if (delivery is not null) { deliveryGate!.Complete(delivery, entry.Outcome, result); }
                else { entry.Outcome.SetResult(result); }
            }
            catch (Exception e)
            {
                deliveryGate?.MarkJournalUncertain();
                lock (gate) { entry.Settled = time.GetUtcNow(); }
                if (delivery is not null) { deliveryGate!.Complete(delivery, entry.Outcome, default!, e); }
                else { entry.Outcome.TrySetException(e); }
            }
            finally { delivery?.Dispose(); }
        }, CancellationToken.None);
        return entry.Outcome.Task;

        async Task<T> Recheck(Func<T, Task<T?>> read, T first, Task watched)
        {
            await watched;
            T? later;
            try { later = await read(first); }
            catch (Exception) { later = default; }
            if (later is null) { return first; }
            settled(later);
            return later;
        }
    }

    static async Task<T> Said(Task<T> outcome, Func<T, T> repeated) => repeated(await outcome);

    void Forget()
    {
        var before = time.GetUtcNow() - rememberFor;
        foreach (var (key, entry) in requests.Where(r => r.Value.Settled < before).ToList()) { requests.Remove(key); }
    }
}
