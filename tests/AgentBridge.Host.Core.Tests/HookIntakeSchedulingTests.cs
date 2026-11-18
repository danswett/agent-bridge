using System.Threading.Channels;
using AgentBridge.Hooks;
using AgentBridge.Host.Core.Intake;
using Xunit;

public sealed class HookIntakeSchedulingTests
{
    static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Idle_intake_backs_off_to_at_most_fourteen_sweeps_in_five_minutes()
    {
        using var fixture = new Fixture();
        var time = new CountingTime(fixture);
        var passes = 0;
        await using var intake = Intake(fixture, time, beat: () => Interlocked.Increment(ref passes));
        intake.Start();

        var elapsed = TimeSpan.Zero;
        var sweeps = 1;
        while (true)
        {
            var wait = await time.NextWait();
            Assert.InRange(wait, TimeSpan.FromSeconds(2), Ceiling);
            Assert.Equal(sweeps, Volatile.Read(ref passes));
            Assert.True(File.Exists(SpoolContract.HeartbeatPath(fixture.Runtime)));
            if (elapsed + wait > TimeSpan.FromMinutes(5)) { break; }
            elapsed += wait;
            time.Advance(wait);
            sweeps++;
            Assert.True(sweeps <= 14, $"Idle intake ran {sweeps} sweeps in {elapsed}.");
        }
        Assert.InRange(sweeps, 10, 14);
        Assert.Equal(1, time.Active);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_nonpositive_sweep_interval_is_rejected_instead_of_spinning(int seconds)
    {
        using var fixture = new Fixture();
        Assert.Throws<ArgumentOutOfRangeException>(() => new HookIntake(new HookIntakeOptions
        {
            RuntimeRoot = fixture.Runtime,
            ClaudeHome = fixture.ClaudeHome,
            SweepInterval = TimeSpan.FromSeconds(seconds),
        }, fixture.Processes, fixture.Time));
    }

    [Fact]
    public async Task A_long_configured_sweep_still_renews_the_heartbeat_within_the_idle_ceiling()
    {
        using var fixture = new Fixture();
        var time = new CountingTime(fixture);
        await using var intake = Intake(fixture, time, interval: TimeSpan.FromMinutes(5));
        intake.Start();
        Assert.Equal(Ceiling, await time.NextWait());
        time.Advance(Ceiling);
        Assert.Equal(Ceiling, await time.NextWait());
    }

    [Theory]
    [InlineData("space")]
    [InlineData("write")]
    [InlineData("stand-back")]
    public async Task A_failed_heartbeat_or_stand_back_resets_the_retry_interval(string failure)
    {
        using var fixture = new Fixture();
        var time = new CountingTime(fixture);
        var blocked = false;
        var heartbeat = SpoolContract.HeartbeatPath(fixture.Runtime);
        await using var intake = new HookIntake(new HookIntakeOptions
        {
            RuntimeRoot = fixture.Runtime,
            ClaudeHome = fixture.ClaudeHome,
            UseFileSystemWatcher = false,
            AgentHaBridgeActive = () => Volatile.Read(ref blocked) && failure == "stand-back" ? "the daemon is running" : null,
            SpaceProbe = _ => Volatile.Read(ref blocked) && failure == "space" ? "the disk is full" : null,
        }, fixture.Processes, time);
        intake.Start();
        await ReachIdleCeiling(time);

        if (failure == "write")
        {
            File.Delete(heartbeat);
            Directory.CreateDirectory(heartbeat);
        }
        Volatile.Write(ref blocked, true);
        time.Advance(Ceiling);
        Assert.Equal(TimeSpan.FromSeconds(2), await time.NextWait());
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), await time.NextWait());

        if (failure == "write") { Directory.Delete(heartbeat); }
        Volatile.Write(ref blocked, false);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(4), await time.NextWait());
        Assert.True(File.Exists(heartbeat));
    }

    [Fact]
    public async Task A_missed_watcher_event_is_found_within_the_idle_ceiling_and_resets_backoff()
    {
        using var fixture = new Fixture();
        var time = new CountingTime(fixture);
        await using var intake = Intake(fixture, time);
        var heard = intake.Subscribe();
        intake.Start();
        await ReachIdleCeiling(time);

        SpoolContract.Write(fixture.Runtime, "copilot", "ask_user",
            "{\"sessionId\":\"missed\",\"toolName\":\"ask_user\"}"u8, [], time.GetUtcNow(), 1);
        time.Advance(Ceiling);

        Assert.Equal(TimeSpan.FromSeconds(2), await time.NextWait());
        Assert.True(heard.TryRead(out var notice));
        Assert.Equal("missed", notice.SessionId);
    }

    [Fact]
    public async Task A_watcher_signal_interrupts_the_idle_wait_and_resets_backoff_without_advancing_time()
    {
        using var fixture = new Fixture();
        var time = new CountingTime(fixture);
        await using var intake = Intake(fixture, time, watch: true);
        var heard = intake.Subscribe();
        intake.Start();
        await ReachIdleCeiling(time);
        var before = time.GetUtcNow();

        SpoolContract.Write(fixture.Runtime, "copilot", "ask_user",
            "{\"sessionId\":\"watched\",\"toolName\":\"ask_user\"}"u8, [], before, 1);

        Assert.Equal(TimeSpan.FromSeconds(2), await time.NextWait());
        Assert.Equal(before, time.GetUtcNow());
        Assert.True(heard.TryRead(out var notice));
        Assert.Equal("watched", notice.SessionId);
    }

    [Fact]
    public async Task A_watcher_signal_without_an_event_still_resets_the_idle_backoff()
    {
        using var fixture = new Fixture();
        var time = new CountingTime(fixture);
        var passes = 0;
        await using var intake = Intake(fixture, time, beat: () => Interlocked.Increment(ref passes));
        intake.Start();
        await ReachIdleCeiling(time);
        var before = time.GetUtcNow();
        var previousPasses = Volatile.Read(ref passes);

        intake.Wake();

        Assert.Equal(TimeSpan.FromSeconds(2), await time.NextWait());
        Assert.Equal(previousPasses + 1, Volatile.Read(ref passes));
        Assert.Equal(0, intake.Handled);
        Assert.Equal(before, time.GetUtcNow());
        Assert.Equal(1, time.Active);
    }

    [Fact]
    public async Task An_unheard_Codex_log_at_the_idle_ceiling_settles_without_waiting_for_the_safety_sweep()
    {
        using var fixture = new Fixture();
        var time = new CountingTime(fixture);
        await using var intake = Intake(fixture, time);
        intake.Start();
        await ReachIdleCeiling(time);
        Assert.Null(intake.ApprovalsUnsettled);
        var before = time.GetUtcNow();
        var marker = fixture.CodexApprovalMarker("unheard", "decision", created: before);
        SpoolContract.Record(fixture.Runtime, "codex", "hook", "reply", "spool failed", before, 1);

        Assert.NotNull(intake.ApprovalsUnsettled);

        Assert.Equal(TimeSpan.FromSeconds(2), await time.NextWait());
        Assert.Equal(before, time.GetUtcNow());
        Assert.False(File.Exists(marker));
        Assert.Null(intake.ApprovalsUnsettled);
    }

    [Fact]
    public async Task An_unreadable_hook_log_keeps_reconciliation_retries_at_the_active_interval()
    {
        using var fixture = new Fixture();
        var time = new CountingTime(fixture);
        await using var intake = Intake(fixture, time);
        Directory.CreateDirectory(SpoolContract.LogPath(fixture.Runtime));
        intake.Start();

        for (var pass = 0; pass < 3; pass++)
        {
            Assert.Equal(TimeSpan.FromSeconds(2), await time.NextWait());
            time.Advance(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Cancellation_during_a_pass_creates_no_safety_timer()
    {
        using var fixture = new Fixture();
        var time = new CountingTime(fixture);
        using var stop = new CancellationTokenSource();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var intake = Intake(fixture, time, beat: () => { stop.Cancel(); reached.TrySetResult(); });
        intake.Start(stop.Token);
        try { await reached.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { await intake.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }

        Assert.Equal(0, time.Created);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stopping_an_idle_intake_disarms_its_timer_without_advancing_time(bool cancel)
    {
        using var fixture = new Fixture();
        var time = new CountingTime(fixture);
        var intake = Intake(fixture, time);
        using var stop = new CancellationTokenSource();
        var disposed = false;
        try
        {
            Assert.Equal(0, time.Created);
            time.Advance(TimeSpan.FromMinutes(5));
            Assert.Equal(0, time.Created);
            intake.Start(stop.Token);
            await ReachIdleCeiling(time);

            if (cancel) { await stop.CancelAsync(); }
            await intake.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            disposed = true;

            Assert.Equal(0, time.Active);
            var fired = time.Fired;
            var created = time.Created;
            time.Advance(TimeSpan.FromMinutes(5));
            Assert.Equal(fired, time.Fired);
            Assert.Equal(created, time.Created);
        }
        finally { if (!disposed) { await intake.DisposeAsync(); } }
    }

    static HookIntake Intake(Fixture fixture, TimeProvider time, bool watch = false, Action? beat = null, TimeSpan? interval = null) => new(new HookIntakeOptions
    {
        RuntimeRoot = fixture.Runtime,
        ClaudeHome = fixture.ClaudeHome,
        UseFileSystemWatcher = watch,
        SweepInterval = interval ?? TimeSpan.FromSeconds(2),
        SpaceProbe = _ => { beat?.Invoke(); return null; },
    }, fixture.Processes, time);

    static async Task ReachIdleCeiling(CountingTime time)
    {
        foreach (var seconds in new[] { 2, 4, 8, 16, 30 })
        {
            var wait = await time.NextWait();
            Assert.Equal(TimeSpan.FromSeconds(seconds), wait);
            if (seconds < 30) { time.Advance(wait); }
        }
    }

    sealed class CountingTime(Fixture fixture) : TimeProvider
    {
        readonly Channel<TimeSpan> waits = Channel.CreateUnbounded<TimeSpan>();
        int created, active, fired;
        public int Created => Volatile.Read(ref created);
        public int Active => Volatile.Read(ref active);
        public int Fired => Volatile.Read(ref fired);
        public override DateTimeOffset GetUtcNow() => fixture.Time.GetUtcNow();
        public void Advance(TimeSpan by) => fixture.Time.Advance(by);
        public async Task<TimeSpan> NextWait()
        {
            try { return await waits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { throw new Xunit.Sdk.XunitException("The intake did not schedule its safety wait through the injected clock."); }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref created);
            Interlocked.Increment(ref active);
            var timer = fixture.Time.CreateTimer(s =>
            {
                Interlocked.Increment(ref fired);
                callback(s);
            }, state, dueTime, period);
            var tracked = new TrackedTimer(timer, () => Interlocked.Decrement(ref active));
            waits.Writer.TryWrite(dueTime);
            return tracked;
        }

        sealed class TrackedTimer(ITimer timer, Action disposed) : ITimer
        {
            int done;
            public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);
            public void Dispose()
            {
                timer.Dispose();
                if (Interlocked.Exchange(ref done, 1) == 0) { disposed(); }
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
