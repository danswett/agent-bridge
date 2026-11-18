using System.Collections.Concurrent;

namespace AgentBridge.Host.Core.Diagnostics;

public sealed record TimerCounts(long Created, long Fired);
public sealed record EfficiencySnapshot(IReadOnlyDictionary<string, TimerCounts> Timers, long AllocatedBytes,
    int Gen0Collections, int Gen1Collections, int Gen2Collections);

// Owners are fixed component roles supplied by composition, never session or device identifiers.
public sealed class CountingTimeProvider : TimeProvider
{
    sealed class Counter
    {
        public long Created;
        public long Fired;
    }

    readonly TimeProvider inner;
    readonly ConcurrentDictionary<string, Counter> owners;
    readonly Counter counter;

    public CountingTimeProvider(TimeProvider inner) : this(inner, new(StringComparer.Ordinal), "host") { }

    CountingTimeProvider(TimeProvider inner, ConcurrentDictionary<string, Counter> owners, string owner)
    {
        this.inner = inner;
        this.owners = owners;
        counter = owners.GetOrAdd(owner, _ => new Counter());
    }

    public CountingTimeProvider ForOwner(string owner) => new(inner, owners, owner);
    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;
    public override long GetTimestamp() => inner.GetTimestamp();
    public override long TimestampFrequency => inner.TimestampFrequency;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = inner.CreateTimer(s =>
        {
            Interlocked.Increment(ref counter.Fired);
            callback(s);
        }, state, dueTime, period);
        Interlocked.Increment(ref counter.Created);
        return timer;
    }

    public EfficiencySnapshot Snapshot() => new(
        owners.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key,
            pair => new TimerCounts(Interlocked.Read(ref pair.Value.Created), Interlocked.Read(ref pair.Value.Fired)),
            StringComparer.Ordinal),
        GC.GetTotalAllocatedBytes(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
}
