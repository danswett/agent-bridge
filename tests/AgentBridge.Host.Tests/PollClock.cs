using Microsoft.Extensions.Time.Testing;

// The time a service that polls sees: it moves on only while the service waits, and then by
// exactly what it waits for, so each look is PollInterval after the last however slow the
// machine is. On the wall clock, a test host stalled under load ran AnswerService's 2 s
// confirmation out between two looks, before the 300 ms Enter on Claude's review screen was
// ever pressed, and the answer read Unconfirmed (#486).
sealed class PollClock : TimeProvider, IAsyncDisposable
{
    readonly FakeTimeProvider inner = new();
    readonly object gate = new();
    readonly List<Pending> waits = [];
    readonly CancellationTokenSource stop = new();
    readonly Task driving;

    public PollClock() => driving = Task.Run(DriveAsync);

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
    public override long GetTimestamp() => inner.GetTimestamp();
    public override long TimestampFrequency => inner.TimestampFrequency;
    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var wait = new Pending(this);
        wait.Timer = inner.CreateTimer(s => { wait.Fired(); callback(s); }, state, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        wait.Change(dueTime, period);
        return wait;
    }

    // Whatever waits soonest is woken; nothing moves while nothing waits.
    async Task DriveAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            DateTimeOffset? next;
            lock (gate) { next = waits.Count == 0 ? null : waits.Min(w => w.DueAt); }
            if (next is { } at)
            {
                var by = at - inner.GetUtcNow();
                inner.Advance(by > TimeSpan.Zero ? by : TimeSpan.FromTicks(1));
                continue;
            }
            try { await Task.Delay(1, stop.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        await driving;
    }

    sealed class Pending(PollClock clock) : ITimer
    {
        public ITimer Timer = null!;
        public DateTimeOffset DueAt;
        TimeSpan period = Timeout.InfiniteTimeSpan;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.gate)
            {
                this.period = period;
                clock.waits.Remove(this);
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    DueAt = clock.inner.GetUtcNow() + dueTime;
                    clock.waits.Add(this);
                }
            }
            return Timer.Change(dueTime, period);
        }

        public void Fired()
        {
            lock (clock.gate)
            {
                clock.waits.Remove(this);
                if (period != Timeout.InfiniteTimeSpan)
                {
                    DueAt = clock.inner.GetUtcNow() + period;
                    clock.waits.Add(this);
                }
            }
        }

        public void Dispose()
        {
            lock (clock.gate) { clock.waits.Remove(this); }
            Timer.Dispose();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
