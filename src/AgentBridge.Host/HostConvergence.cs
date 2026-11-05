namespace AgentBridge.Host;

// Host-only automatic convergence (#819): one pass, run from the host's existing update check, that
// either hands one verified release to the host's unattended apply under #805's committed departure
// guard or says, in a closed word, why not.
//
// Nothing here is a second scheduler, fence, announcement, cancel pipeline, halts reader or version
// rule. Quiet, the planned-state announcement, its cancel and the atomic departure commit are #805's
// LifecycleCoordinator; halts and no-downgrade are #580/#581's ReleaseEligibility; the release and
// the apply are their owners' ports. A port or condition nobody has supplied yet is a block with its
// own word, never a default that lets the update through. The owner chose host-only convergence with
// the app's Restart to update left manual (#819, comment 6647803922): the app being open is the
// AppRunning condition's block, and IHostUnattendedApply is Daily's seam, not today's ApplyAndRestart.
//
// There is deliberately no "every session idle" test: #805's quiet is no delivery in flight, no
// typing and no uncertain journal, so a session that always shows Working doesn't hold this back.
public sealed class HostConvergence
{
    // Observing #805's cancel never waits longer than its own bounded send plus a margin; past that
    // the lease's current status is reported. This bounds observation only, it sends nothing.
    static readonly TimeSpan CancelObservation = LifecycleCoordinator.AnnounceTimeout + TimeSpan.FromSeconds(1);

    readonly IConvergenceChannel channel;
    readonly IConvergenceRelease? release;
    readonly ReleaseEligibility? eligibility;
    readonly IHostUnattendedApply? apply;
    readonly IReadOnlyList<IDepartureCondition> conditions;
    readonly ChannelChangedCondition? channelCondition;
    readonly LifecycleCoordinator? lifecycle;
    readonly Func<string?> installed;
    readonly TimeSpan maxWait;
    readonly TimeProvider clock;
    readonly SemaphoreSlim once = new(1, 1);
    DepartureGuard? departed;

    public HostConvergence(
        IConvergenceChannel channel,
        IConvergenceRelease? release,
        ReleaseEligibility? eligibility,
        IHostUnattendedApply? apply,
        IReadOnlyList<IDepartureCondition>? conditions,
        LifecycleCoordinator? lifecycle,
        Func<string?> installed,
        TimeSpan? quietMaxWait = null,
        TimeProvider? time = null)
    {
        this.channel = channel;
        this.release = release;
        this.eligibility = eligibility;
        this.apply = apply;
        this.conditions = conditions is not null && Complete(conditions) ? [.. conditions] : [];
        channelCondition = this.conditions.OfType<ChannelChangedCondition>().SingleOrDefault();
        this.lifecycle = lifecycle;
        this.installed = installed;
        maxWait = quietMaxWait ?? LifecycleCoordinator.DefaultWait;
        clock = time ?? TimeProvider.System;
    }

    // #819's Update conditions, the only four this action adds to #805's quiet (SRE, #819 comment
    // 6655603535): AppRunning and OtherHostRunning are Daily's in-memory snapshots, TransferInFlight
    // the host-owned sign-in transfer owner's, ChannelChanged this class's. Exactly one of each.
    public static readonly IReadOnlyList<DepartureBlocker> UpdateConditionKinds =
        [DepartureBlocker.AppRunning, DepartureBlocker.OtherHostRunning, DepartureBlocker.ChannelChanged, DepartureBlocker.TransferInFlight];

    // The composition root's one registration for DeparturePurpose.Update, passing the same
    // instances it gives this class. Not called anywhere yet: the other three owners' snapshots don't
    // exist, and an incomplete set is refused here rather than registered.
    public static void RegisterUpdateConditions(DepartureConditions registry, params IDepartureCondition[] registered)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (!Complete(registered)) { throw new ArgumentException("Update needs exactly the four #819 conditions", nameof(registered)); }
        registry.Register(DeparturePurpose.Update, registered);
    }

    static bool Complete(IReadOnlyList<IDepartureCondition> registered)
    {
        try
        {
            return registered.Count == UpdateConditionKinds.Count
                && registered.All(c => c is not null)
                && UpdateConditionKinds.All(kind => registered.Count(c => c.Kind == kind) == 1)
                && registered.Single(c => c.Kind == DepartureBlocker.ChannelChanged) is ChannelChangedCondition;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
    }

    // The last pass's outcome; null before the first pass. This is what the machine's report and
    // #755's Fleet row must show (SRE's condition, #819 comment 6653892825): app-running and every
    // other block is a visible reason there, never counted as converged. Nothing reads it yet.
    public ConvergenceDecision? Last { get; private set; }

    public async Task<ConvergenceDecision> RunOnceAsync(CancellationToken cancellationToken)
    {
        // An invalid bound is refused before anything is asked, never clamped into a wait.
        if (maxWait < TimeSpan.Zero || maxWait > LifecycleCoordinator.MaxWait) { throw new ArgumentOutOfRangeException("quietMaxWait"); }
        // Entry never waits, so it takes no token and can't throw; a cancellation that races entry is
        // seen just inside, where the pass owns Last. A pass that overlaps another says so.
        if (!await once.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            return Decide(ConvergenceOutcome.Waiting, cancellationToken.IsCancellationRequested ? ConvergenceReason.Cancelled : ConvergenceReason.PassRunning);
        }
        try
        {
            return Last = cancellationToken.IsCancellationRequested
                ? Decide(ConvergenceOutcome.Blocked, ConvergenceReason.Cancelled)
                : await PassAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { once.Release(); }
    }

    async Task<ConvergenceDecision> PassAsync(CancellationToken cancellationToken)
    {
        ConvergenceDecision Blocked(ConvergenceReason reason, string? version = null, CancellationOutcome? cancel = null) =>
            Decide(ConvergenceOutcome.Blocked, reason, version, cancel);

        // A committed departure is held until this process exits (#805): no second announce, commit
        // or apply, even when the first apply failed.
        if (departed is not null) { return Decide(ConvergenceOutcome.Blocked, ConvergenceReason.DepartureCommitted); }

        var current = installed();
        if (current is null) { return Blocked(ConvergenceReason.NotInstalled); }

        // #451(b)'s one validated selection; its source identity binds everything found under it.
        string? source;
        try { source = channel.Source(); }
        catch (Exception e) when (e is not OutOfMemoryException) { source = null; }
        if (source is null) { return Blocked(ConvergenceReason.ChannelUnavailable); }

        if (release is null) { return Blocked(ConvergenceReason.EligibilityUnavailable); }
        ReleaseCandidate candidate;
        try { candidate = await release.CandidateAsync(source, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Blocked(ConvergenceReason.Cancelled); }
        catch (Exception e) when (e is not OutOfMemoryException) { return Blocked(ConvergenceReason.EligibilityUnavailable); }
        if (candidate is { Kind: CandidateKind.None }) { return Decide(ConvergenceOutcome.Current, ConvergenceReason.None); }
        if (candidate is not { Kind: CandidateKind.Target, Release: { } target }) { return Blocked(ConvergenceReason.EligibilityUnavailable); }

        if (target.Source != source) { return Blocked(ConvergenceReason.ChannelChanged, target.Version); }
        if (!target.ArchitectureMatches) { return Blocked(ConvergenceReason.WrongArchitecture, target.Version); }
        if (!target.Verified) { return Blocked(ConvergenceReason.NotVerified, target.Version); }

        // Halts (#580, an unreadable list fails closed) and no-downgrade (#581) are SRE's one
        // function; there is no second comparison here.
        if (eligibility is null) { return Blocked(ConvergenceReason.EligibilityUnavailable, target.Version); }
        ReleaseEligibilityResult allowed;
        try { allowed = await eligibility.Evaluate(target.Version, current, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Blocked(ConvergenceReason.Cancelled, target.Version); }
        catch (Exception e) when (e is not OutOfMemoryException) { return Blocked(ConvergenceReason.HaltListUnavailable, target.Version); }
        if (!allowed.Allowed)
        {
            return allowed.Reason switch
            {
                ReleaseBlockedReason.NotNewer => Decide(ConvergenceOutcome.Current, ConvergenceReason.None),
                ReleaseBlockedReason.Halted => Blocked(ConvergenceReason.Halted, target.Version),
                ReleaseBlockedReason.InvalidVersion => Blocked(ConvergenceReason.VersionUnknown, target.Version),
                _ => Blocked(ConvergenceReason.HaltListUnavailable, target.Version),
            };
        }

        if (apply is null) { return Blocked(ConvergenceReason.ApplyUnavailable, target.Version); }
        if (channelCondition is null) { return Blocked(ConvergenceReason.ConditionsIncomplete, target.Version); }

        var committed = false;
        QuietLease? lease = null;
        channelCondition.Expect(source);
        try
        {
            // A first look before any lease or announcement, so a block that is already visible (the
            // app open, say) never sends a plan and its cancel. #805 asks every one again under its fence.
            if (Blocking() is { } early) { return Blocked(early, target.Version); }

            if (lifecycle is null) { return Blocked(ConvergenceReason.LifecycleUnavailable, target.Version); }
            QuietResult<QuietLease> acquired;
            try { acquired = await lifecycle.TryAcquireQuiet(maxWait, DeparturePurpose.Update, cancellationToken).ConfigureAwait(false); }
            catch (Exception e) when (e is not OutOfMemoryException) { return Blocked(ConvergenceReason.LifecycleUnavailable, target.Version); }
            if (acquired.Value is null) { return Refused(acquired.Reason, null, target.Version); }
            lease = acquired.Value;

            QuietResult<PlannedStateReceipt> announced;
            try { announced = await lifecycle.AnnouncePlanned(lease, cancellationToken).ConfigureAwait(false); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return Blocked(ConvergenceReason.AnnounceUnavailable, target.Version, await AbortAsync(lifecycle, lease).ConfigureAwait(false));
            }
            if (announced.Value is not { } receipt)
            {
                // #805 ends a refused lease itself; its idempotent abort then only returns that cancel,
                // so a refusal never waits on a cancel nobody dispatched.
                return Refused(announced.Reason, await AbortAsync(lifecycle, lease).ConfigureAwait(false), target.Version);
            }
            // Legacy is only a person's press (#815 slice 1); an update nobody pressed stops here.
            if (receipt.Legacy) { return Blocked(ConvergenceReason.AnnounceUnavailable, target.Version, await AbortAsync(lifecycle, lease).ConfigureAwait(false)); }
            if (cancellationToken.IsCancellationRequested) { return Blocked(ConvergenceReason.Cancelled, target.Version, await AbortAsync(lifecycle, lease).ConfigureAwait(false)); }

            // #451's selection as it is now; #805 compares it with the pass's under its fence.
            try { channelCondition.Observe(channel.Source()); }
            catch (Exception e) when (e is not OutOfMemoryException) { channelCondition.Observe(null); }

            DepartureResult result;
            try { result = lifecycle.CommitDeparture(lease); }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return Blocked(ConvergenceReason.LifecycleUnavailable, target.Version, await AbortAsync(lifecycle, lease).ConfigureAwait(false));
            }
            if (result.Guard is not { } guard) { return Aborted(result.Kind, await ObserveAsync(lease).ConfigureAwait(false), target.Version); }

            // The same lease is now #805's guard, held until this process exits; this class never
            // disposes it, on success, failure or timeout, and never applies a second time.
            committed = true;
            departed = guard;
            try { await apply.ApplyUnattendedAsync(target, guard, cancellationToken).ConfigureAwait(false); }
            catch (Exception e) when (e is not OutOfMemoryException) { return Blocked(ConvergenceReason.ApplyFailed, target.Version); }
            return Decide(ConvergenceOutcome.Applying, ConvergenceReason.None, target.Version);
        }
        finally
        {
            if (!committed)
            {
                // Idempotent: releases a lease no path above already ended, cancelling a sent plan.
                lease?.Dispose();
                channelCondition.Clear();
            }
        }
    }

    ConvergenceReason? Blocking()
    {
        foreach (var condition in conditions)
        {
            try { if (condition.IsBlocking()) { return Word(condition.Kind); } }
            catch (Exception e) when (e is not OutOfMemoryException) { return ConvergenceReason.ConditionFault; }
        }
        return null;
    }

    async Task<CancellationOutcome> AbortAsync(LifecycleCoordinator coordinator, QuietLease lease)
    {
        try { _ = coordinator.Abort(lease); }
        catch (Exception e) when (e is not OutOfMemoryException) { lease.Dispose(); }
        return await ObserveAsync(lease).ConfigureAwait(false);
    }

    // #805's bounded cancel outcome for this lease (NotRequired, Sent or CancelUnsent), or Pending
    // if it hasn't settled within observation. Never a second send.
    async Task<CancellationOutcome> ObserveAsync(QuietLease lease)
    {
        try { return await lease.Cancellation.WaitAsync(CancelObservation, clock).ConfigureAwait(false); }
        catch (TimeoutException) { return lease.CancellationStatus; }
    }

    // #805's quiet refusal. Someone else's delivery or lifecycle is a wait for the next check;
    // anything that can't be shown safe is a block. A result with neither value nor reason is no yes.
    ConvergenceDecision Refused(QuietBlockedReason? reason, CancellationOutcome? cancel, string version)
    {
        var (outcome, why) = reason switch
        {
            QuietBlockedReason.Typing => (ConvergenceOutcome.Waiting, ConvergenceReason.Typing),
            QuietBlockedReason.DeliveryInFlight => (ConvergenceOutcome.Waiting, ConvergenceReason.DeliveryInFlight),
            QuietBlockedReason.LifecycleInProgress => (ConvergenceOutcome.Waiting, ConvergenceReason.LifecycleInProgress),
            QuietBlockedReason.LeaseExpired => (ConvergenceOutcome.Waiting, ConvergenceReason.LeaseExpired),
            QuietBlockedReason.JournalUncertain => (ConvergenceOutcome.Blocked, ConvergenceReason.JournalUncertain),
            QuietBlockedReason.AnnounceUnacked => (ConvergenceOutcome.Blocked, ConvergenceReason.AnnounceUnacked),
            QuietBlockedReason.AnnounceUnavailable => (ConvergenceOutcome.Blocked, ConvergenceReason.AnnounceUnavailable),
            QuietBlockedReason.EligibilityChanged => (ConvergenceOutcome.Blocked, ConvergenceReason.EligibilityChanged),
            QuietBlockedReason.Cancelled => (ConvergenceOutcome.Blocked, ConvergenceReason.Cancelled),
            QuietBlockedReason.ConditionFault => (ConvergenceOutcome.Blocked, ConvergenceReason.ConditionFault),
            _ => (ConvergenceOutcome.Blocked, ConvergenceReason.LifecycleUnavailable),
        };
        return Decide(outcome, why, version, cancel);
    }

    // #805's commit abort: the closed blocker found under its fence, named as it was found.
    ConvergenceDecision Aborted(DepartureBlocker? kind, CancellationOutcome cancel, string version) => kind switch
    {
        DepartureBlocker.DeliveryInFlight or DepartureBlocker.Typing => Decide(ConvergenceOutcome.Waiting, Word(kind.Value), version, cancel),
        { } blocker => Decide(ConvergenceOutcome.Blocked, Word(blocker), version, cancel),
        null => Decide(ConvergenceOutcome.Blocked, ConvergenceReason.LifecycleUnavailable, version, cancel),
    };

    static ConvergenceReason Word(DepartureBlocker kind) => kind switch
    {
        DepartureBlocker.DeliveryInFlight => ConvergenceReason.DeliveryInFlight,
        DepartureBlocker.Typing => ConvergenceReason.Typing,
        DepartureBlocker.AnnounceUnacked => ConvergenceReason.AnnounceUnacked,
        DepartureBlocker.JournalUncertain => ConvergenceReason.JournalUncertain,
        DepartureBlocker.AppRunning => ConvergenceReason.AppRunning,
        DepartureBlocker.OtherHostRunning => ConvergenceReason.OtherHostRunning,
        DepartureBlocker.ChannelChanged => ConvergenceReason.ChannelChanged,
        DepartureBlocker.TransferInFlight => ConvergenceReason.TransferInFlight,
        _ => ConvergenceReason.ConditionFault,
    };

    ConvergenceDecision Decide(ConvergenceOutcome outcome, ConvergenceReason reason, string? version = null, CancellationOutcome? cancel = null) =>
        new(outcome, reason, version, clock.GetUtcNow(), cancel);
}

// Dogfood's (#451) ChannelChanged condition for DeparturePurpose.Update: blocks unless the channel
// source this pass started from is the one last observed. Synchronous and in memory, as #805 requires.
// Nothing expected (no pass in progress) is a block, so the registration never lets an update through
// on its own.
public sealed class ChannelChangedCondition : IDepartureCondition
{
    readonly Lock state = new();
    string? expected, observed;

    public DepartureBlocker Kind => DepartureBlocker.ChannelChanged;

    public bool IsBlocking()
    {
        lock (state) { return expected is null || observed != expected; }
    }

    // #451's current selection, from its in-memory snapshot; null when it is unavailable.
    public void Observe(string? source)
    {
        lock (state) { observed = source; }
    }

    internal void Expect(string source)
    {
        lock (state) { expected = observed = source; }
    }

    internal void Clear()
    {
        lock (state) { expected = observed = null; }
    }
}

public enum ConvergenceOutcome { Current, Waiting, Blocked, Applying }

// Closed: each has one fixed word (Word) and nothing else is ever shown, so no exception text, path
// or URL can reach health or a log through a decision.
public enum ConvergenceReason
{
    None,
    PassRunning,
    DepartureCommitted,
    NotInstalled,
    ChannelUnavailable,
    ChannelChanged,
    EligibilityUnavailable,
    Halted,
    HaltListUnavailable,
    VersionUnknown,
    WrongArchitecture,
    NotVerified,
    ApplyUnavailable,
    ConditionsIncomplete,
    AppRunning,
    OtherHostRunning,
    TransferInFlight,
    ConditionFault,
    LifecycleUnavailable,
    Typing,
    DeliveryInFlight,
    JournalUncertain,
    LifecycleInProgress,
    AnnounceUnacked,
    AnnounceUnavailable,
    LeaseExpired,
    EligibilityChanged,
    ApplyFailed,
    Cancelled,
}

// Cancel is #805's outcome for a plan this pass's lease announced and then abandoned; null when no
// lease was taken.
public sealed record ConvergenceDecision(ConvergenceOutcome Outcome, ConvergenceReason Reason, string? TargetVersion, DateTimeOffset At,
    CancellationOutcome? Cancel = null)
{
    public string Word => Reason switch
    {
        ConvergenceReason.None => "none",
        ConvergenceReason.PassRunning => "pass-running",
        ConvergenceReason.DepartureCommitted => "departure-committed",
        ConvergenceReason.NotInstalled => "not-installed",
        ConvergenceReason.ChannelUnavailable => "channel-unavailable",
        ConvergenceReason.ChannelChanged => "channel-changed",
        ConvergenceReason.EligibilityUnavailable => "eligibility-unavailable",
        ConvergenceReason.Halted => "halted",
        ConvergenceReason.HaltListUnavailable => "halt-list-unavailable",
        ConvergenceReason.VersionUnknown => "version-unknown",
        ConvergenceReason.WrongArchitecture => "wrong-architecture",
        ConvergenceReason.NotVerified => "not-verified",
        ConvergenceReason.ApplyUnavailable => "apply-unavailable",
        ConvergenceReason.ConditionsIncomplete => "conditions-incomplete",
        ConvergenceReason.AppRunning => "app-running",
        ConvergenceReason.OtherHostRunning => "other-host-running",
        ConvergenceReason.TransferInFlight => "transfer-in-flight",
        ConvergenceReason.ConditionFault => "condition-fault",
        ConvergenceReason.LifecycleUnavailable => "lifecycle-unavailable",
        ConvergenceReason.Typing => "typing",
        ConvergenceReason.DeliveryInFlight => "delivery-in-flight",
        ConvergenceReason.JournalUncertain => "journal-uncertain",
        ConvergenceReason.LifecycleInProgress => "lifecycle-in-progress",
        ConvergenceReason.AnnounceUnacked => "announce-unacked",
        ConvergenceReason.AnnounceUnavailable => "announce-unavailable",
        ConvergenceReason.LeaseExpired => "lease-expired",
        ConvergenceReason.EligibilityChanged => "eligibility-changed",
        ConvergenceReason.ApplyFailed => "apply-failed",
        ConvergenceReason.Cancelled => "cancelled",
        _ => "unknown",
    };

    public string? CancelWord => Cancel switch
    {
        CancellationOutcome.NotRequired => "not-required",
        CancellationOutcome.Pending => "pending",
        CancellationOutcome.Sent => "sent",
        CancellationOutcome.CancelUnsent => "cancel-unsent",
        _ => null,
    };
}

// #451(b)'s one validated channel selection (UpdateChannels.Resolve, Dogfood's writer): its Source
// ("{feed}@{identity}"), or null when it is unavailable. Nothing here reads update-channel.json.
public interface IConvergenceChannel
{
    string? Source();
}

// The newest release on this copy's #451 channel and architecture, with its reconstructed full
// package verified: #709/#719's one discovery and cache, Daily's verification. It decides nothing
// about halts or versions; ReleaseEligibility does. Unknown is never None.
public interface IConvergenceRelease
{
    Task<ReleaseCandidate> CandidateAsync(string channelSource, CancellationToken cancellationToken);
}

public enum CandidateKind { Unknown, None, Target }

public sealed record ReleaseCandidate(CandidateKind Kind, EligibleRelease? Release = null);

// What the release owner vouches for. Handle is theirs, passed through to the apply untouched, so
// the package applied is exactly the one verified.
public sealed record EligibleRelease(string Version, string Source, bool ArchitectureMatches, bool Verified, object? Handle = null);

// Daily's HostUpdates seam: apply this host's update without closing the app or another profile's
// host. Takes #805's committed guard as its authority and hands the same exclusion to the
// replacement; whether the app or another host is running is Daily's AppRunning/OtherHostRunning
// condition, asked under #805's fence, not a question this port answers.
public interface IHostUnattendedApply
{
    Task ApplyUnattendedAsync(EligibleRelease release, DepartureGuard guard, CancellationToken cancellationToken);
}
