// The time a service that polls sees: it moves on only while the service waits, and then by
// exactly what it waits for, so each look is PollInterval after the last however slow the
// machine is. On the wall clock, a test host stalled under load ran AnswerService's 2 s
// confirmation out between two looks, before the 300 ms Enter on Claude's review screen was
// ever pressed, and the answer read Unconfirmed (#486).
//
// Its timers are its own rather than a FakeTimeProvider's: a wait is armed and becomes the next
// deadline in one step, under one lock, so time cannot be moved on to a deadline whose timer is
// not armed yet (which re-armed it from the later time, a wait of 25 ms becoming 50 ms).
sealed class PollClock : TimeProvider, IAsyncDisposable
{
    readonly DateTimeOffset epoch;

    readonly object gate = new();
    readonly List<Wait> waits = [];
    readonly SemaphoreSlim armed = new(0);
    readonly CancellationTokenSource stop = new();
    readonly Task driving;
    long now;
    long order;
    Action? onNextWait;

    // start: where its time begins. A fixed date unless the code reads it against something on the
    // wall clock, as a migration does against the files and spool names it writes.
    public PollClock(DateTimeOffset? start = null)
    {
        epoch = start ?? new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        driving = Task.Run(DriveAsync);
    }

    public override DateTimeOffset GetUtcNow() => epoch.AddTicks(Interlocked.Read(ref now));
    public override long GetTimestamp() => Interlocked.Read(ref now);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var wait = new Wait(this, callback, state);
        wait.Change(dueTime, period);
        return wait;
    }

    // Runs once, when something next starts to wait: a service waits only after it has looked,
    // so what is done then is seen at its next look and never before the one it just made.
    public void OnNextWait(Action then)
    {
        lock (gate) { onNextWait += then; }
    }

    // Whatever waits soonest is woken; nothing moves while nothing waits.
    async Task DriveAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            Wait? due;
            lock (gate)
            {
                due = waits.Count == 0 ? null : waits.MinBy(w => (w.DueAt, w.Order));
                if (due is not null)
                {
                    if (due.DueAt > now) { Interlocked.Exchange(ref now, due.DueAt); }
                    waits.Remove(due);
                    if (due.Period is { } every) { due.Arm(now + every); }
                }
            }
            if (due is not null)
            {
                due.Callback(due.State);
                continue;
            }
            try { await armed.WaitAsync(stop.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        await driving;
    }

    sealed class Wait(PollClock clock, TimerCallback callback, object? state) : ITimer
    {
        public readonly TimerCallback Callback = callback;
        public readonly object? State = state;
        public long DueAt;
        public long Order;
        public long? Period;

        // Under the clock's lock.
        public void Arm(long at)
        {
            DueAt = at;
            Order = clock.order++;
            clock.waits.Add(this);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock.gate)
            {
                clock.waits.Remove(this);
                Period = period == Timeout.InfiniteTimeSpan || period == TimeSpan.Zero ? null : period.Ticks;
                if (dueTime == Timeout.InfiniteTimeSpan) { return true; }
                Arm(clock.now + Math.Max(0, dueTime.Ticks));
            }
            clock.armed.Release();
            Action? then;
            lock (clock.gate) { (then, clock.onNextWait) = (clock.onNextWait, null); }
            then?.Invoke();
            return true;
        }

        public void Dispose()
        {
            lock (clock.gate) { clock.waits.Remove(this); }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}