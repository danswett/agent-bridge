using AgentBridge.Host.Delivery;
using System.Text.Json.Serialization;

namespace AgentBridge.Host;

public enum QuietBlockedReason { Typing, DeliveryInFlight, JournalUncertain, LifecycleInProgress, AnnounceUnacked, AnnounceUnavailable, LeaseExpired, EligibilityChanged, Cancelled, ConditionFault }
public enum DepartureMode { SelfInitiatedGraceful, ForcedRecovery, ExternalRestart, QueuedInputHandover }
public enum CancellationOutcome { NotRequired, Pending, Sent, CancelUnsent }

public sealed record QuietResult<T> where T : class
{
    public T? Value { get; }
    public QuietBlockedReason? Reason { get; }
    public bool Succeeded => Value is not null;
    QuietResult(T? value, QuietBlockedReason? reason) => (Value, Reason) = (value, reason);
    internal static QuietResult<T> Granted(T value) => new(value, null);
    internal static QuietResult<T> Blocked(QuietBlockedReason reason) => new(null, reason);
}

public sealed record PlannedStateMessage(
    [property: JsonPropertyName("transition")] Guid Transition,
    [property: JsonPropertyName("seq")] long Sequence,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null,
    [property: JsonPropertyName("expectedReturnSeconds"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? ExpectedReturnSeconds = null)
{
    [JsonPropertyName("t")] public string Type => "host-planned-state";
    [JsonPropertyName("v")] public int Version => 1;
}

// The transport preserves sequence order and validates durable acceptance of this exact
// message. Generation is an in-memory connection snapshot, never an I/O-performing getter.
public interface IPlannedStateTransport
{
    bool Negotiated { get; }
    long Generation { get; }
    Task<bool> SendAsync(PlannedStateMessage message, CancellationToken cancellationToken);
}

public sealed class UnnegotiatedPlannedStateTransport : IPlannedStateTransport
{
    public bool Negotiated => false;
    public long Generation => 0;
    public Task<bool> SendAsync(PlannedStateMessage message, CancellationToken cancellationToken) => Task.FromResult(false);
}

public sealed class PlannedStateReceipt
{
    public Guid Transition { get; }
    public bool Legacy { get; }
    internal long Generation { get; }
    internal PlannedStateReceipt(Guid transition, bool legacy, long generation) => (Transition, Legacy, Generation) = (transition, legacy, generation);
}

public sealed class QuietLease : IDisposable
{
    internal LifecycleCoordinator Owner { get; }
    internal DeliveryGate.Exclusion Fence { get; }
    internal bool Sent, CancelScheduled, Committed, Disposed;
    internal long Generation;
    internal PlannedStateReceipt? Receipt;
    readonly TaskCompletionSource<CancellationOutcome> cancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int cancellationStatus;
    public Guid Transition { get; } = Guid.NewGuid();
    public DeparturePurpose Purpose { get; }
    public bool IsCurrent => !Disposed && Fence.IsCurrent;
    public CancellationOutcome CancellationStatus => (CancellationOutcome)Volatile.Read(ref cancellationStatus);
    public Task<CancellationOutcome> Cancellation => cancellation.Task;

    internal QuietLease(LifecycleCoordinator owner, DeliveryGate.Exclusion fence, DeparturePurpose purpose)
    {
        (Owner, Fence, Purpose) = (owner, fence, purpose);
        fence.Expired += () => owner.Release(this);
    }

    internal void Cancelling() => Volatile.Write(ref cancellationStatus, (int)CancellationOutcome.Pending);
    internal void Cancelled(CancellationOutcome outcome)
    {
        Volatile.Write(ref cancellationStatus, (int)outcome);
        cancellation.TrySetResult(outcome);
    }

    public void Dispose() => Owner.Release(this);
}

// There is deliberately no release operation/finalizer: even Dispose cannot admit another
// delivery after commitment. If apply subsequently fails, the host remains fenced until exit.
public sealed class DepartureGuard : IDisposable
{
    public Guid Transition { get; }
    internal DepartureGuard(QuietLease lease) => Transition = lease.Transition;
    public void Dispose() { }
}

public sealed class LifecycleCoordinator
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan LeaseLifetime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan AnnounceTimeout = TimeSpan.FromSeconds(5);
    readonly Lock state = new();
    readonly DeliveryGate delivery;
    readonly IPlannedStateTransport transport;
    readonly TimeProvider time;
    readonly IReadOnlyDictionary<DeparturePurpose, (DepartureBlocker Kind, IDepartureCondition Condition)[]> conditions;
    bool evaluatingConditions, reentryFault;
    long sequence;

    public LifecycleCoordinator(DeliveryGate delivery, DepartureConditions conditions, IPlannedStateTransport? transport = null, TimeProvider? time = null)
    {
        this.delivery = delivery;
        this.conditions = conditions.Freeze();
        this.transport = transport ?? new UnnegotiatedPlannedStateTransport();
        this.time = time ?? TimeProvider.System;
    }

    public Task<QuietResult<QuietLease>> TryAcquireQuiet(DeparturePurpose purpose, CancellationToken cancellationToken = default) =>
        TryAcquireQuiet(DefaultWait, purpose, cancellationToken);

    public async Task<QuietResult<QuietLease>> TryAcquireQuiet(TimeSpan maxWait, DeparturePurpose purpose, CancellationToken cancellationToken = default,
        DepartureMode mode = DepartureMode.SelfInitiatedGraceful)
    {
        lock (state) { if (RejectReentry()) { return QuietResult<QuietLease>.Blocked(QuietBlockedReason.ConditionFault); } }
        if (maxWait < TimeSpan.Zero || maxWait > MaxWait) { throw new ArgumentOutOfRangeException(nameof(maxWait)); }
        if (!Enum.IsDefined(purpose)) { throw new ArgumentOutOfRangeException(nameof(purpose)); }
        if (!Enum.IsDefined(mode)) { throw new ArgumentOutOfRangeException(nameof(mode)); }
        if (purpose == DeparturePurpose.WatchdogRecovery || mode != DepartureMode.SelfInitiatedGraceful)
        { return QuietResult<QuietLease>.Blocked(QuietBlockedReason.JournalUncertain); }
        var started = time.GetTimestamp();
        while (true)
        {
            if (cancellationToken.IsCancellationRequested) { return QuietResult<QuietLease>.Blocked(QuietBlockedReason.Cancelled); }
            if (delivery.TryExclude(LeaseLifetime, out var blocker) is { } fence)
            {
                return QuietResult<QuietLease>.Granted(new QuietLease(this, fence, purpose));
            }
            var remaining = maxWait - time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero)
            {
                return QuietResult<QuietLease>.Blocked(blocker switch
                {
                    DeliveryGate.Blocker.Typing => QuietBlockedReason.Typing,
                    DeliveryGate.Blocker.DeliveryInFlight => QuietBlockedReason.DeliveryInFlight,
                    DeliveryGate.Blocker.JournalUncertain => QuietBlockedReason.JournalUncertain,
                    _ => QuietBlockedReason.LifecycleInProgress,
                });
            }
            try { await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(25, remaining.TotalMilliseconds)), time, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return QuietResult<QuietLease>.Blocked(QuietBlockedReason.Cancelled); }
        }
    }

    public async Task<QuietResult<PlannedStateReceipt>> AnnouncePlanned(QuietLease lease, CancellationToken cancellationToken = default)
    {
        PlannedStateMessage? message = null;
        lock (state)
        {
            if (RejectReentry()) { return QuietResult<PlannedStateReceipt>.Blocked(QuietBlockedReason.ConditionFault); }
            if (!Current(lease) || lease.Committed) { return QuietResult<PlannedStateReceipt>.Blocked(QuietBlockedReason.LeaseExpired); }
            if (cancellationToken.IsCancellationRequested) { return QuietResult<PlannedStateReceipt>.Blocked(QuietBlockedReason.Cancelled); }
            if (lease.Receipt is { } receipt) { return QuietResult<PlannedStateReceipt>.Granted(receipt); }
            if (lease.Sent) { return QuietResult<PlannedStateReceipt>.Blocked(QuietBlockedReason.AnnounceUnacked); }
            lease.Generation = transport.Generation;
            if (!transport.Negotiated)
            {
                if (lease.Purpose is DeparturePurpose.RemoteRestart or DeparturePurpose.RemoteStop)
                {
                    lease.Receipt = new PlannedStateReceipt(lease.Transition, true, lease.Generation);
                    return QuietResult<PlannedStateReceipt>.Granted(lease.Receipt);
                }
            }
            else
            {
                var (reason, seconds) = Plan(lease.Purpose);
                lease.Sent = true;
                message = new PlannedStateMessage(lease.Transition, ++sequence, "planned", reason, seconds);
            }
        }
        if (message is null)
        {
            Release(lease);
            return QuietResult<PlannedStateReceipt>.Blocked(QuietBlockedReason.AnnounceUnavailable);
        }
        var failure = QuietBlockedReason.AnnounceUnacked;
        try
        {
            using var timeout = new CancellationTokenSource(AnnounceTimeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
            var token = linked.Token;
            var started = time.GetTimestamp();
            var sending = Task.Run(() => transport.SendAsync(message, token));
            var accepted = await sending.WaitAsync(AnnounceTimeout, time, token).ConfigureAwait(false);
            lock (state)
            {
                if (!Current(lease)) { failure = QuietBlockedReason.LeaseExpired; }
                else if (cancellationToken.IsCancellationRequested) { failure = QuietBlockedReason.Cancelled; }
                else if (timeout.IsCancellationRequested || time.GetElapsedTime(started) >= AnnounceTimeout) { failure = QuietBlockedReason.AnnounceUnacked; }
                else if (accepted && transport.Negotiated && transport.Generation == lease.Generation)
                {
                    lease.Receipt = new PlannedStateReceipt(lease.Transition, false, lease.Generation);
                    return QuietResult<PlannedStateReceipt>.Granted(lease.Receipt);
                }
            }
        }
        catch (OperationCanceledException) { failure = cancellationToken.IsCancellationRequested ? QuietBlockedReason.Cancelled : QuietBlockedReason.AnnounceUnacked; }
        catch (TimeoutException) { }
        catch (Exception e) when (e is IOException or InvalidOperationException or HttpRequestException or System.Net.WebSockets.WebSocketException)
        { failure = QuietBlockedReason.AnnounceUnavailable; }
        Release(lease);
        return QuietResult<PlannedStateReceipt>.Blocked(failure);
    }

    public DepartureResult CommitDeparture(QuietLease lease)
    {
        CancellationWork? cancel = null;
        DepartureResult result;
        lock (state)
        {
            if (RejectReentry()) { return DepartureResult.Abort(DepartureBlocker.ConditionFault); }
            if (lease.Committed) { return DepartureResult.Abort(DepartureBlocker.ConditionFault); }
            if (!Current(lease))
            {
                cancel = EndLease(lease);
                result = DepartureResult.Abort(DepartureBlocker.ConditionFault);
            }
            else
            {
                DepartureBlocker? blocked = null;
                reentryFault = false;
                var committed = lease.Fence.TryCommit(fenceBlocker =>
                {
                    blocked = fenceBlocker switch
                    {
                        DeliveryGate.Blocker.JournalUncertain => DepartureBlocker.JournalUncertain,
                        DeliveryGate.Blocker.Typing => DepartureBlocker.Typing,
                        DeliveryGate.Blocker.DeliveryInFlight => DepartureBlocker.DeliveryInFlight,
                        _ => null,
                    };
                    var receipt = lease.Receipt;
                    if (receipt is null || transport.Generation != receipt.Generation
                        || (receipt.Legacy ? transport.Negotiated : !transport.Negotiated)) { blocked ??= DepartureBlocker.AnnounceUnacked; }
                    if (!conditions.TryGetValue(lease.Purpose, out var registered)) { blocked = DepartureBlocker.ConditionFault; }
                    evaluatingConditions = true;
                    try
                    {
                        foreach (var (kind, condition) in registered ?? [])
                        {
                            try { if (condition.IsBlocking()) { blocked ??= kind; } }
                            catch { blocked = DepartureBlocker.ConditionFault; }
                        }
                    }
                    finally { evaluatingConditions = false; }
                    if (reentryFault) { blocked = DepartureBlocker.ConditionFault; }
                    return blocked is null;
                });
                if (committed)
                {
                    lease.Committed = true;
                    result = DepartureResult.Commit(new DepartureGuard(lease));
                }
                else
                {
                    cancel = EndLease(lease);
                    result = DepartureResult.Abort(blocked ?? DepartureBlocker.ConditionFault);
                }
            }
        }
        Dispatch(cancel);
        return result;
    }

    public Task<CancellationOutcome> Abort(QuietLease lease)
    {
        lock (state)
        {
            if (RejectReentry() || !ReferenceEquals(lease.Owner, this) || lease.Committed)
            { return Task.FromResult(CancellationOutcome.CancelUnsent); }
        }
        Release(lease);
        return lease.Cancellation;
    }

    internal void Release(QuietLease lease)
    {
        CancellationWork? cancel;
        lock (state)
        {
            if (RejectReentry() || lease.Committed || !ReferenceEquals(lease.Owner, this)) { return; }
            cancel = EndLease(lease);
        }
        Dispatch(cancel);
    }

    sealed record CancellationWork(QuietLease Lease, PlannedStateMessage? Message, CancellationOutcome? Immediate);

    // Called with state held; no transport send/callback occurs until Dispatch after unlocking.
    CancellationWork? EndLease(QuietLease lease)
    {
        if (!ReferenceEquals(lease.Owner, this) || lease.Committed || lease.CancelScheduled) { return null; }
        lease.Disposed = true;
        lease.CancelScheduled = true;
        lease.Fence.Dispose();
        if (!lease.Sent) { return new(lease, null, CancellationOutcome.NotRequired); }
        if (transport.Generation != lease.Generation) { return new(lease, null, CancellationOutcome.CancelUnsent); }
        lease.Cancelling();
        return new(lease, new PlannedStateMessage(lease.Transition, ++sequence, "cancelled"), null);
    }

    void Dispatch(CancellationWork? work)
    {
        if (work is null) { return; }
        if (work.Immediate is { } immediate) { work.Lease.Cancelled(immediate); return; }
        _ = SendCancellation(work.Lease, work.Message!);
    }

    async Task SendCancellation(QuietLease lease, PlannedStateMessage message)
    {
        var outcome = CancellationOutcome.CancelUnsent;
        try
        {
            using var timeout = new CancellationTokenSource(AnnounceTimeout, time);
            // Isolate even a transport that blocks before returning its Task; cancellation
            // cannot retain the fence, an internal lock, or the next acquisition.
            var token = timeout.Token;
            var started = time.GetTimestamp();
            var sending = Task.Run(() => transport.SendAsync(message, token));
            if (await sending.WaitAsync(AnnounceTimeout, time, token).ConfigureAwait(false)
                && !timeout.IsCancellationRequested && time.GetElapsedTime(started) < AnnounceTimeout) { outcome = CancellationOutcome.Sent; }
        }
        catch (Exception) { outcome = CancellationOutcome.CancelUnsent; }
        lease.Cancelled(outcome);
    }

    bool Current(QuietLease lease) => ReferenceEquals(lease.Owner, this) && lease.IsCurrent;

    bool RejectReentry()
    {
        if (!evaluatingConditions) { return false; }
        reentryFault = true;
        return true;
    }

    static (string Reason, int? Seconds) Plan(DeparturePurpose purpose) => purpose switch
    {
        DeparturePurpose.Update => ("update", 90),
        DeparturePurpose.RemoteRestart => ("restart", 60),
        DeparturePurpose.RemoteStop => ("stop", null),
        DeparturePurpose.Sleep => ("sleep", null),
        DeparturePurpose.Shutdown => ("shutdown", null),
        DeparturePurpose.Reboot => ("reboot", 300),
        DeparturePurpose.SignOut => ("sign-out", null),
        _ => throw new InvalidOperationException("forced recovery requires a journal"),
    };
}
