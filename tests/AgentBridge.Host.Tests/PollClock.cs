using Stopwatch = System.Diagnostics.Stopwatch;

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
    bool frozen;
    // Once a frozen clock has been let run: its time moves, but a budget only after a quiet moment.
    bool afterFrozen;
    long lastActive = Stopwatch.GetTimestamp();
    static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);
    // The longest wait a frozen clock still fires: a service's look-again interval, not its budget.
    static readonly long SteadyTicks = TimeSpan.FromMilliseconds(100).Ticks;

    // start: where its time begins. A fixed date unless the code reads it against something on the
    // wall clock, as a migration does against the files and spool names it writes.
    // frozen: time stands still, so a budget (a wait for a session to register, a window to look
    // ready) cannot run out however slow the machine or the test is; only short waits fire, each
    // at the time it was armed. Run() lets time move on, for a test that is about a budget
    // running out (#421: a launch test's 5 s budget ran out under load before the host's first
    // look at the window, so its trust question was never seen).
    public PollClock(DateTimeOffset? start = null, bool frozen = false)
    {
        epoch = start ?? new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        this.frozen = frozen;
        driving = Task.Run(DriveAsync);
    }

    public void Run()
    {
        lock (gate) { afterFrozen = frozen; frozen = false; lastActive = Stopwatch.GetTimestamp(); }
        armed.Release();
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
            bool still, settling, gated = false;
            lock (gate)
            {
                (still, settling) = (frozen, afterFrozen);
                due = waits.Count == 0 ? null : waits.MinBy(w => (w.DueAt, w.Order));
                if ((still || settling) && due is not null && due.DueAt - now > SteadyTicks)
                {
                    // A budget is jumped to only once nothing has armed or fired for a while: it
                    // is the soonest wait only because the service is between two looks, and
                    // jumping then ran it out before its first look (a 400 ms wait, before the
                    // session it waited for was even seen).
                    if (still || Stopwatch.GetElapsedTime(lastActive) < Quiet) { due = null; gated = !still; }
                }
                if (due is not null)
                {
                    if (!still && due.DueAt > now) { Interlocked.Exchange(ref now, due.DueAt); }
                    waits.Remove(due);
                    if (due.Period is { } every) { due.Arm(now + every); }
                    lastActive = Stopwatch.GetTimestamp();
                }
            }
            if (due is not null)
            {
                due.Callback(due.State);
                // Time hardly moves, so a look-again wait fires at once: not faster than the
                // thread pool and the files it reads can keep up with.
                if (still || settling) { await Task.Delay(1); }
                continue;
            }
            try
            {
                if (gated) { await armed.WaitAsync(TimeSpan.FromMilliseconds(20), stop.Token); }
                else { await armed.WaitAsync(stop.Token); }
            }
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
            clock.lastActive = Stopwatch.GetTimestamp();
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