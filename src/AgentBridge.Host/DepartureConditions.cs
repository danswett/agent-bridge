namespace AgentBridge.Host;

public enum DeparturePurpose { Update, RemoteRestart, RemoteStop, WatchdogRecovery, Sleep, Shutdown, Reboot, SignOut }
public enum DepartureBlocker { DeliveryInFlight, Typing, AnnounceUnacked, JournalUncertain, AppRunning, OtherHostRunning, ChannelChanged, TransferInFlight, ConditionFault }

public interface IDepartureCondition
{
    DepartureBlocker Kind { get; }
    // Synchronous, no I/O or side effects: read the owner's already-refreshed memory snapshot.
    bool IsBlocking();
}

public sealed class DepartureConditions
{
    readonly Dictionary<DeparturePurpose, (DepartureBlocker Kind, IDepartureCondition Condition)[]> conditions = [];
    bool frozen;

    public void Register(DeparturePurpose purpose, params IDepartureCondition[] registered)
    {
        if (frozen) { throw new InvalidOperationException("departure conditions are frozen at composition"); }
        if (!Enum.IsDefined(purpose)) { throw new ArgumentOutOfRangeException(nameof(purpose)); }
        ArgumentNullException.ThrowIfNull(registered);
        var snapshot = registered.Select(condition =>
        {
            ArgumentNullException.ThrowIfNull(condition);
            var kind = condition.Kind;
            if (!Enum.IsDefined(kind)) { throw new ArgumentOutOfRangeException(nameof(registered), "unknown departure blocker"); }
            return (kind, condition);
        }).ToArray();
        if (!conditions.TryAdd(purpose, snapshot)) { throw new InvalidOperationException("this purpose was already registered"); }
    }

    internal IReadOnlyDictionary<DeparturePurpose, (DepartureBlocker Kind, IDepartureCondition Condition)[]> Freeze()
    {
        frozen = true;
        return conditions.ToDictionary(p => p.Key, p => p.Value.ToArray());
    }
}

public sealed record DepartureResult
{
    public DepartureGuard? Guard { get; }
    public DepartureBlocker? Kind { get; }
    public bool Committed => Guard is not null;
    DepartureResult(DepartureGuard? guard, DepartureBlocker? kind) => (Guard, Kind) = (guard, kind);
    internal static DepartureResult Commit(DepartureGuard guard) => new(guard, null);
    internal static DepartureResult Abort(DepartureBlocker kind) => new(null, kind);
}
