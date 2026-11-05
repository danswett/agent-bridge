namespace AgentBridge.Host;

public enum DepartureState { None, Committed, Stuck }

// Reason is null until Stuck, then always DepartureWatchdog.StuckReason.
public sealed record DepartureStatus(DepartureState State, DateTimeOffset? CommittedAt = null, string? Reason = null)
{
    public static readonly DepartureStatus None = new(DepartureState.None);
}

public interface IDepartureStuckHandler
{
    // Called at most once per process, off the committing thread. A throw is contained.
    void DepartureStuck(DepartureStatus status);
}

// #805 N1: a committed DepartureGuard holds the delivery fence until the process exits. If the
// follow-on action (an update apply, a restart) fails after commit, nothing else would ever let
// the host take deliveries again. This notices that the process outlived its departure.
public sealed class DepartureWatchdog : IDisposable
{
    public const string StuckReason = "departure-stuck";
    public static readonly TimeSpan DefaultBound = TimeSpan.FromMinutes(2);
    readonly Lock state = new();
    readonly IDepartureStuckHandler? handler;
    readonly TimeProvider time;
    readonly TimeSpan bound;
    DepartureStatus status = DepartureStatus.None;
    ITimer? timer;

    // A null handler only marks the state, for the status to show.
    public DepartureWatchdog(IDepartureStuckHandler? handler = null, TimeProvider? time = null, TimeSpan? bound = null)
    {
        this.bound = bound ?? DefaultBound;
        if (this.bound <= TimeSpan.Zero || this.bound == Timeout.InfiniteTimeSpan) { throw new ArgumentOutOfRangeException(nameof(bound)); }
        this.handler = handler;
        this.time = time ?? TimeProvider.System;
    }

    public TimeSpan Bound => bound;

    public DepartureStatus Status { get { lock (state) { return status; } } }

    // Armed once, by LifecycleCoordinator.CommitDeparture; a commit cannot be undone, so neither
    // can this.
    internal void Arm()
    {
        lock (state)
        {
            if (status.State != DepartureState.None) { return; }
            status = new DepartureStatus(DepartureState.Committed, time.GetUtcNow());
            timer = time.CreateTimer(_ => Elapsed(), null, bound, Timeout.InfiniteTimeSpan);
        }
    }

    void Elapsed()
    {
        DepartureStatus stuck;
        lock (state)
        {
            if (status.State != DepartureState.Committed) { return; }
            stuck = status = status with { State = DepartureState.Stuck, Reason = StuckReason };
        }
        // A throwing handler must not take down the timer thread; the state is already marked.
        try { handler?.DepartureStuck(stuck); }
        catch (Exception) { }
    }

    public void Dispose()
    {
        lock (state) { timer?.Dispose(); }
    }
}

// The host's default: one fixed line, then the existing host.restart path (HostHandover: a
// supervised host exits for its launcher to restart, an unsupervised one starts its replacement
// after it has stopped). No exception text or path is logged.
public sealed class RestartingDepartureStuckHandler(Action<string> log, Action? restart) : IDepartureStuckHandler
{
    public void DepartureStuck(DepartureStatus status)
    {
        if (restart is null)
        {
            log("departure stuck: committed departure did not exit; deliveries stay refused until the host restarts");
            return;
        }
        log("departure stuck: committed departure did not exit; restarting the host");
        restart();
    }
}
