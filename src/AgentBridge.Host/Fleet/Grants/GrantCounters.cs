namespace AgentBridge.Host.Fleet.Grants;

public enum GrantCounterProblem { None, Store, Malformed, Binding, Duplicate, Hourly, Daily, Live, Clock, NotFound }
public enum GrantReservationState { Pending, Committed, Abandoned, NotLaunched, Exited }
public enum GrantProcessStatus { Unknown, Live, Exited }
public sealed record GrantUsage(int Hourly, int Daily, int Live);
public sealed record GrantReservation(Guid Request, Guid Grant, long Generation, GrantSubject Subject, string Binding,
    DateTimeOffset AdmittedAt, GrantReservationState State, StopTarget? Target = null);
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
    public GrantCounterResult Reserve(Guid grant, long generation, Guid request, GrantSubject subject, string binding) =>
        grant == Guid.Empty ? new(GrantCounterProblem.Malformed) : Run(grant, generation, true, (state, document, now) =>
        {
            if (request == Guid.Empty || !GrantValidator.Subject(subject) || binding is not { Length: 64 }
                || !binding.All(char.IsAsciiHexDigit)) { return (state, new(GrantCounterProblem.Malformed)); }
            if (!document!.Subjects.Contains(subject) || !document.Actions.Contains("launch"))
            { return (state, new(GrantCounterProblem.Binding)); }
            var previous = state.Reservations.SingleOrDefault(r => r.Request == request);
            if (previous is not null)
            {
                return (state, new(previous.Grant == grant && previous.Generation == generation
                    && previous.Subject == subject && previous.Binding == binding ? GrantCounterProblem.Duplicate : GrantCounterProblem.Binding));
            }
            var usage = Count(state, grant, now);
            var problem = usage.Hourly >= document.Caps.Hourly ? GrantCounterProblem.Hourly
                : usage.Daily >= document.Caps.Daily ? GrantCounterProblem.Daily
                : usage.Live >= document.Caps.Live ? GrantCounterProblem.Live : GrantCounterProblem.None;
            if (problem != GrantCounterProblem.None) { return (state, new(problem, Usage: usage)); }
            var reservation = new GrantReservation(request, grant, generation, subject, binding, now, GrantReservationState.Pending);
            state = state with { Reservations = [.. state.Reservations, reservation] };
            return (state, new(GrantCounterProblem.None, Reservation: reservation, Usage: Count(state, grant, now)));
        });

    public GrantCounterResult Read(Guid grant, long generation) => Run(grant, generation, false,
        (state, _, now) => (state, new(GrantCounterProblem.None, Usage: Count(state, grant, now))));
    public GrantCounterResult Commit(Guid request, StopTarget target) => Change(request, reservation =>
        !GrantValidator.Target(target) || reservation.State is GrantReservationState.NotLaunched or GrantReservationState.Exited
            || reservation.Target is { } existing && existing != target ? null
            : reservation with { State = GrantReservationState.Committed, Target = target });
    public GrantCounterResult Abandon(Guid request, bool provenNotLaunched) => Change(request, reservation =>
        reservation.Target is not null || reservation.State is GrantReservationState.Committed or GrantReservationState.Exited ? null
            : reservation with { State = provenNotLaunched || reservation.State == GrantReservationState.NotLaunched
                ? GrantReservationState.NotLaunched : GrantReservationState.Abandoned });
    GrantCounterResult Change(Guid request, Func<GrantReservation, GrantReservation?> change) =>
        Run(Guid.Empty, 0, false, (state, _, _) =>
        {
            var reservation = state.Reservations.SingleOrDefault(r => r.Request == request);
            if (reservation is null) { return (state, new(GrantCounterProblem.NotFound)); }
            var next = change(reservation);
            return next is null ? (state, new(GrantCounterProblem.Binding, Reservation: reservation))
                : (state with { Reservations = state.Reservations.Select(r => r.Request == request ? next : r).ToArray() },
                    new(GrantCounterProblem.None, Reservation: next));
        });

    public GrantCounterResult Reconcile(Guid grant, Func<GrantReservation, GrantReconciliation> lookup,
        Func<int, GrantProcessObservation> inspect) => Run(Guid.Empty, 0, false, (state, _, _) =>
        {
            if (grant == Guid.Empty) { return (state, new(GrantCounterProblem.Malformed)); }
            var reservations = new List<GrantReservation>();
            foreach (var reservation in state.Reservations)
            {
                if (reservation.Grant != grant || reservation.State is GrantReservationState.NotLaunched or GrantReservationState.Exited)
                { reservations.Add(reservation); continue; }
                var evidence = reservation.Target is null ? lookup(reservation) : new(reservation.Target);
                if (evidence.ProvenNotLaunched && evidence.Target is not null
                    || evidence.Target is not null && !GrantValidator.Target(evidence.Target))
                { return (state, new(GrantCounterProblem.Malformed)); }
                var next = reservation;
                if (evidence.ProvenNotLaunched) { next = reservation with { State = GrantReservationState.NotLaunched }; }
                else if (evidence.Target is { } target)
                {
                    var observed = inspect(target.ProcessInstance.Pid);
                    if (!Enum.IsDefined(observed.Status) || observed.Status == GrantProcessStatus.Live
                        && (observed.Instance is null || observed.Instance.Pid != target.ProcessInstance.Pid || observed.Instance.StartedAt == default))
                    { return (state, new(GrantCounterProblem.Malformed)); }
                    var exited = observed.Status == GrantProcessStatus.Exited || observed.Status == GrantProcessStatus.Live
                        && observed.Instance != target.ProcessInstance;
                    next = reservation with { Target = target, State = exited
                        ? GrantReservationState.Exited : GrantReservationState.Committed };
                }
                reservations.Add(next);
            }
            return (state with { Reservations = reservations.ToArray() }, new(GrantCounterProblem.None));
        }, auditGrant: grant);

    GrantCounterResult Run(Guid grant, long generation, bool admission,
        Func<GrantCounterState, GrantDocument?, DateTimeOffset, (GrantCounterState State, GrantCounterResult Result)> change,
        Guid auditGrant = default)
    {
        GrantCounterResult result = new(GrantCounterProblem.Store);
        var stored = store.Transaction(state =>
        {
            var now = clock.GetUtcNow();
            var counters = state.Counters ?? new GrantCounterState(now, []);
            if (now < counters.ObservedAt) { result = new(GrantCounterProblem.Clock); return (null, new(GrantStoreProblem.None)); }
            var found = grant == Guid.Empty ? new GrantStoreResult(GrantStoreProblem.None)
                : store.Find(state, grant, generation, checkTime: admission, observedAt: now);
            if (!found.Usable && found.Problem != GrantStoreProblem.Expired) { return (null, found); }
            var (next, outcome) = found.Usable ? change(counters, found.Grant, now)
                : (counters, new GrantCounterResult(GrantCounterProblem.Store, found.Problem));
            result = outcome;
            next = next with { ObservedAt = now };
            if (state.Counters is { } current && current.ObservedAt == next.ObservedAt
                && current.Reservations.SequenceEqual(next.Reservations)) { return (null, new(GrantStoreProblem.None)); }
            var id = grant != Guid.Empty ? grant : outcome.Reservation?.Grant ?? auditGrant;
            if (id == Guid.Empty) { return (null, new(GrantStoreProblem.None)); }
            return (GrantStore.Next(state with { Counters = next }, id, admission && outcome.Accepted ? "grant.reserve" : "grant.update"),
                new(GrantStoreProblem.None));
        }, allowNew: false);
        return stored.Usable ? result : new(GrantCounterProblem.Store, stored.Problem);
    }
    static GrantUsage Count(GrantCounterState state, Guid grant, DateTimeOffset now)
    {
        var spent = state.Reservations.Where(r => r.Grant == grant && r.State != GrantReservationState.NotLaunched).ToArray();
        return new(spent.Count(r => now - r.AdmittedAt < TimeSpan.FromHours(1)),
            spent.Count(r => now - r.AdmittedAt < TimeSpan.FromDays(1)),
            spent.Count(r => r.State is GrantReservationState.Pending or GrantReservationState.Committed or GrantReservationState.Abandoned));
    }
    internal static bool Valid(GrantCounterState? state) => state is null || state.ObservedAt != default
        && state.Reservations is { Length: <= 4096 } reservations
        && reservations.Select(r => r?.Request).Distinct().Count() == reservations.Length
        && reservations.All(r => r is not null && r.Request != Guid.Empty && r.Grant != Guid.Empty && r.Generation > 0
            && GrantValidator.Subject(r.Subject) && r.Binding is { Length: 64 } && r.Binding.All(char.IsAsciiHexDigit)
            && r.AdmittedAt != default && r.AdmittedAt <= state.ObservedAt && Enum.IsDefined(r.State)
            && (r.Target is null ? r.State is not (GrantReservationState.Committed or GrantReservationState.Exited)
                : GrantValidator.Target(r.Target) && r.State is GrantReservationState.Committed or GrantReservationState.Exited));
}
