namespace AgentBridge.Host.Fleet.Grants;

public enum GrantCounterProblem { None, Store, Malformed, Binding, Duplicate, Hourly, Daily, Live, Clock, NotFound, Full }
public enum GrantReservationState { Pending, Committed, Abandoned, NotLaunched, Exited, Refused, Failed }
public enum GrantLaunchOutcome { Unconfirmed, Started, Refused, Failed }
public enum GrantProcessStatus { Unknown, Live, Exited }
public sealed record GrantUsage(int Hourly, int Daily, int Live);
public sealed record GrantReservation(Guid Request, Guid Grant, long Generation, GrantSubject Subject, string Binding,
    DateTimeOffset AdmittedAt, GrantReservationState State, StopTarget? Target = null, string? Reason = null,
    DateTimeOffset? FinishedAt = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public GrantLaunchOutcome Outcome => State switch
    {
        GrantReservationState.Committed or GrantReservationState.Exited => GrantLaunchOutcome.Started,
        GrantReservationState.Refused => GrantLaunchOutcome.Refused,
        GrantReservationState.Failed => GrantLaunchOutcome.Failed,
        _ => GrantLaunchOutcome.Unconfirmed,
    };
}
public sealed record GrantReconciliation(StopTarget? Target, bool ProvenNotLaunched = false);
public sealed record GrantProcessObservation(GrantProcessStatus Status, ProcessInstance? Instance = null);
public sealed record GrantCounterResult(GrantCounterProblem Problem, GrantStoreProblem StoreProblem = GrantStoreProblem.None,
    GrantReservation? Reservation = null, GrantUsage? Usage = null)
{
    public bool Accepted => Problem == GrantCounterProblem.None;
}
internal sealed record GrantCounterState(DateTimeOffset ObservedAt, GrantReservation[] Reservations);

// Receiver-owned inputs and reconciliation evidence only; no launch or process inspection runs here.
public sealed class GrantCounters(GrantStore store, TimeProvider? time = null)
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    const int MaxRequests = 4096;
    static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    public GrantCounterResult Reserve(Guid grant, long generation, Guid request, GrantSubject subject, string binding) =>
        grant == Guid.Empty ? new(GrantCounterProblem.Malformed) : Run(grant, generation, true,
            (state, document, now) => Admit(state, document, grant, generation, request, subject, binding, now),
            duplicateRequest: request);

    internal static (GrantCounterState State, GrantCounterResult Result) Admit(GrantCounterState state, GrantDocument? document,
        Guid grant, long generation, Guid request, GrantSubject subject, string binding, DateTimeOffset now)
    {
        if (request == Guid.Empty || !GrantValidator.Subject(subject) || binding is not { Length: 64 }
            || !binding.All(char.IsAsciiHexDigit)) { return (state, new(GrantCounterProblem.Malformed)); }
        var previous = state.Reservations.SingleOrDefault(r => r.Request == request);
        if (previous is not null)
        {
            var matches = previous.Grant == grant && previous.Generation == generation && previous.Subject == subject && previous.Binding == binding;
            return (state, matches ? new(GrantCounterProblem.Duplicate, Reservation: previous, Usage: Count(state, grant, now))
                : new(GrantCounterProblem.Binding));
        }
        if (!document!.Subjects.Contains(subject) || !document.Actions.Contains("launch"))
        { return (state, new(GrantCounterProblem.Binding)); }
        if (state.Reservations.Length >= MaxRequests) { return (state, new(GrantCounterProblem.Full)); }
        var usage = Count(state, grant, now);
        var problem = usage.Hourly >= document.Caps.Hourly ? GrantCounterProblem.Hourly
            : usage.Daily >= document.Caps.Daily ? GrantCounterProblem.Daily
            : usage.Live >= document.Caps.Live ? GrantCounterProblem.Live : GrantCounterProblem.None;
        if (problem != GrantCounterProblem.None) { return (state, new(problem, Usage: usage)); }
        var reservation = new GrantReservation(request, grant, generation, subject, binding, now, GrantReservationState.Pending);
        state = state with { Reservations = [.. state.Reservations, reservation] };
        return (state, new(GrantCounterProblem.None, Reservation: reservation, Usage: Count(state, grant, now)));
    }

    public GrantCounterResult Read(Guid grant, long generation) => Run(grant, generation, false,
        (state, _, now) => (state, new(GrantCounterProblem.None, Usage: Count(state, grant, now))));
    public GrantCounterResult Commit(Guid request, StopTarget target) => Change(request, (reservation, _) =>
        !GrantValidator.Target(target) || reservation.State is GrantReservationState.NotLaunched or GrantReservationState.Exited
            or GrantReservationState.Refused or GrantReservationState.Failed
            || reservation.Target is { } existing && existing != target ? null
            : reservation with { State = GrantReservationState.Committed, Target = target });
    public GrantCounterResult Abandon(Guid request, bool provenNotLaunched) => Change(request, (reservation, now) =>
        reservation.Target is not null || reservation.State is GrantReservationState.Committed or GrantReservationState.Exited
            or GrantReservationState.Refused or GrantReservationState.Failed ? null
            : reservation with { State = provenNotLaunched || reservation.State == GrantReservationState.NotLaunched
                ? GrantReservationState.NotLaunched : GrantReservationState.Abandoned,
                FinishedAt = provenNotLaunched ? reservation.FinishedAt ?? now : reservation.FinishedAt });
    public GrantCounterResult Complete(Guid request, GrantLaunchOutcome outcome, string reason) => Change(request, (reservation, now) =>
    {
        if (outcome is not (GrantLaunchOutcome.Refused or GrantLaunchOutcome.Failed) || reason is not { Length: > 0 and <= 64 }
            || !reason.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')) { return null; }
        if (reservation.State is GrantReservationState.Refused or GrantReservationState.Failed)
        { return reservation.Outcome == outcome && reservation.Reason == reason ? reservation : null; }
        return reservation.State is GrantReservationState.Pending or GrantReservationState.Abandoned
            ? reservation with { State = outcome == GrantLaunchOutcome.Refused ? GrantReservationState.Refused : GrantReservationState.Failed,
                Reason = reason, FinishedAt = now } : null;
    });
    GrantCounterResult Change(Guid request, Func<GrantReservation, DateTimeOffset, GrantReservation?> change) =>
        Run(Guid.Empty, 0, false, (state, _, now) =>
        {
            var reservation = state.Reservations.SingleOrDefault(r => r.Request == request);
            if (reservation is null) { return (state, new(GrantCounterProblem.NotFound)); }
            var next = change(reservation, now);
            return next is null ? (state, new(GrantCounterProblem.Binding, Reservation: reservation))
                : (state with { Reservations = state.Reservations.Select(r => r.Request == request ? next : r).ToArray() },
                    new(GrantCounterProblem.None, Reservation: next));
        });

    public GrantCounterResult Reconcile(Guid grant, Func<GrantReservation, GrantReconciliation> lookup,
        Func<int, GrantProcessObservation> inspect) => Run(Guid.Empty, 0, false, (state, _, now) =>
        {
            if (grant == Guid.Empty) { return (state, new(GrantCounterProblem.Malformed)); }
            var reservations = new List<GrantReservation>();
            foreach (var reservation in state.Reservations)
            {
                if (reservation.Grant != grant || reservation.State is GrantReservationState.NotLaunched or GrantReservationState.Exited
                    or GrantReservationState.Refused or GrantReservationState.Failed)
                { reservations.Add(reservation); continue; }
                var evidence = reservation.Target is null ? lookup(reservation) : new(reservation.Target);
                if (evidence.ProvenNotLaunched && evidence.Target is not null
                    || evidence.Target is not null && !GrantValidator.Target(evidence.Target))
                { return (state, new(GrantCounterProblem.Malformed)); }
                var next = reservation;
                if (evidence.ProvenNotLaunched)
                { next = reservation with { State = GrantReservationState.NotLaunched, FinishedAt = now }; }
                else if (evidence.Target is { } target)
                {
                    var observed = inspect(target.ProcessInstance.Pid);
                    if (!Enum.IsDefined(observed.Status) || observed.Status == GrantProcessStatus.Live
                        && (observed.Instance is null || observed.Instance.Pid != target.ProcessInstance.Pid || observed.Instance.StartedAt == default))
                    { return (state, new(GrantCounterProblem.Malformed)); }
                    var exited = observed.Status == GrantProcessStatus.Exited || observed.Status == GrantProcessStatus.Live
                        && observed.Instance != target.ProcessInstance;
                    next = reservation with { Target = target, FinishedAt = exited ? now : null, State = exited
                        ? GrantReservationState.Exited : GrantReservationState.Committed };
                }
                reservations.Add(next);
            }
            return (state with { Reservations = reservations.ToArray() }, new(GrantCounterProblem.None));
        }, auditGrant: grant);

    GrantCounterResult Run(Guid grant, long generation, bool admission,
        Func<GrantCounterState, GrantDocument?, DateTimeOffset, (GrantCounterState State, GrantCounterResult Result)> change,
        Guid auditGrant = default, Guid duplicateRequest = default)
    {
        GrantCounterResult result = new(GrantCounterProblem.Store);
        var stored = store.Transaction(state =>
        {
            var now = clock.GetUtcNow();
            var counters = state.Counters ?? new GrantCounterState(now, []);
            if (now < counters.ObservedAt) { result = new(GrantCounterProblem.Clock); return (null, new(GrantStoreProblem.None)); }
            var retained = counters.Reservations.Where(r => now - (r.FinishedAt ?? r.AdmittedAt) < Retention
                || r.State is GrantReservationState.Pending or GrantReservationState.Committed or GrantReservationState.Abandoned).ToArray();
            if (retained.Length != counters.Reservations.Length) { counters = counters with { Reservations = retained }; }
            // A repeat reads its authenticated binding even after the issuing grant expires or is revoked; it never acts.
            var found = grant == Guid.Empty || counters.Reservations.Any(r => r.Request == duplicateRequest)
                ? new GrantStoreResult(GrantStoreProblem.None)
                : store.Find(state, grant, generation, checkTime: admission, observedAt: now);
            if (!found.Usable && found.Problem != GrantStoreProblem.Expired) { return (null, found); }
            var (next, outcome) = found.Usable ? change(counters, found.Grant, now)
                : (counters, new GrantCounterResult(GrantCounterProblem.Store, found.Problem));
            result = outcome;
            if (outcome.Problem == GrantCounterProblem.Duplicate) { return (null, new(GrantStoreProblem.None)); }
            next = next with { ObservedAt = now };
            if (state.Counters is { } current && current.ObservedAt == next.ObservedAt
                && current.Reservations.SequenceEqual(next.Reservations)) { return (null, new(GrantStoreProblem.None)); }
            var id = grant != Guid.Empty ? grant : outcome.Reservation?.Grant ?? auditGrant;
            if (id == Guid.Empty) { return (null, new(GrantStoreProblem.None)); }
            return (GrantStore.Next(state with { Counters = next }, id, admission && outcome.Accepted ? "grant.reserve" : "grant.update"),
                new(GrantStoreProblem.None));
        }, allowNew: false);
        return stored.Usable ? result : new(stored.Problem == GrantStoreProblem.Oversize ? GrantCounterProblem.Full
            : GrantCounterProblem.Store, stored.Problem);
    }
    static GrantUsage Count(GrantCounterState state, Guid grant, DateTimeOffset now)
    {
        var spent = state.Reservations.Where(r => r.Grant == grant && r.State != GrantReservationState.NotLaunched).ToArray();
        return new(spent.Count(r => now - r.AdmittedAt < TimeSpan.FromHours(1)),
            spent.Count(r => now - r.AdmittedAt < TimeSpan.FromDays(1)),
            spent.Count(r => r.State is GrantReservationState.Pending or GrantReservationState.Committed or GrantReservationState.Abandoned));
    }
    internal static bool Valid(GrantCounterState? state) => state is null || state.ObservedAt != default
        && state.Reservations is { Length: <= MaxRequests } reservations
        && reservations.Select(r => r?.Request).Distinct().Count() == reservations.Length
        && reservations.All(r => r is not null && r.Request != Guid.Empty && r.Grant != Guid.Empty && r.Generation > 0
            && GrantValidator.Subject(r.Subject) && r.Binding is { Length: 64 } && r.Binding.All(char.IsAsciiHexDigit)
            && r.AdmittedAt != default && r.AdmittedAt <= state.ObservedAt && Enum.IsDefined(r.State)
            && (r.FinishedAt is null || r.FinishedAt >= r.AdmittedAt && r.FinishedAt <= state.ObservedAt
                && r.State is GrantReservationState.NotLaunched or GrantReservationState.Exited or GrantReservationState.Refused or GrantReservationState.Failed)
            && (r.State is GrantReservationState.Refused or GrantReservationState.Failed
                ? r.Reason is { Length: > 0 and <= 64 } && r.Reason.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
                : r.Reason is null)
            && (r.Target is null ? r.State is not (GrantReservationState.Committed or GrantReservationState.Exited)
                : GrantValidator.Target(r.Target) && r.State is GrantReservationState.Committed or GrantReservationState.Exited));
}
