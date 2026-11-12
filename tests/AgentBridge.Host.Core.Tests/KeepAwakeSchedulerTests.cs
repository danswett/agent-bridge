using System.Diagnostics;
using System.Reflection;
using System.Threading.Channels;
using AgentBridge.Host.Core.DevBox;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Xunit.Abstractions;

public sealed class KeepAwakeSchedulerTests(ITestOutputHelper output)
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-30T22:00:00Z");
    static readonly DevBoxIdentity Box = new(new Uri("https://fake.devcenter.azure.com"), "fake-project", "fake-box");

    [Fact]
    public async Task Real_clock_movement_during_registration_does_not_create_retry_timers()
    {
        var clock = new RealClock();
        await using var service = Service(clock, called: clock.PassReached,
            firstPass: TimeSpan.FromMilliseconds(250));
        var watch = Stopwatch.StartNew();
        service.Start();

        var count = await clock.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        output.WriteLine($"Real UTC clock: first outcome {count} timers, {clock.Timers} observed in {watch.Elapsed.TotalMilliseconds:F3} ms.");
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Disabled_keep_awake_or_a_non_Dev_Box_starts_no_scheduler_or_timer(bool enabled, bool devBox)
    {
        var clock = new Clock();
        await using var service = Service(clock, enabled, devBox);

        service.Start();

        Assert.Null(typeof(KeepAwakeService).GetField("loop", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service));
        Assert.Null(service.State.NextAt);
        Assert.Equal(0, clock.Timers);
    }

    [Fact]
    public async Task A_continuously_moving_clock_uses_one_timer_and_shuts_down_in_under_a_second()
    {
        var clock = new Clock { AutoAdvanceAmount = TimeSpan.FromTicks(1) };
        var service = Service(clock);
        Task? shutdown = null;
        try
        {
            service.Start();
            await clock.Registered();

            shutdown = service.DisposeAsync().AsTask();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(1, clock.Timers);
        }
        finally
        {
            // Let the old spinning implementation finish too, so a red test leaves no busy thread.
            clock.AutoAdvanceAmount = TimeSpan.Zero;
            clock.Advance(TimeSpan.FromDays(1));
            await (shutdown ?? service.DisposeAsync().AsTask());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_does_not_wait_for_the_first_pass_or_the_interval_timer(bool interval)
    {
        var clock = new Clock();
        var service = Service(clock);
        service.Start();
        await clock.Registered();
        if (interval)
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            await clock.Registered();
        }

        await service.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Turning_keep_awake_off_releases_its_timer_and_turning_it_on_runs_a_pass_now()
    {
        var clock = new Clock();
        var calls = 0;
        await using var service = Service(clock, called: () => Interlocked.Increment(ref calls));
        service.Start();
        await clock.Registered();

        service.Enabled = false;
        await clock.Released();
        Assert.Equal(0, clock.ActiveTimers);
        Assert.Equal(0, calls);

        service.Enabled = true;
        await clock.Registered();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_clock_jump_before_registration_keeps_the_original_due_time_without_recreating_timers()
    {
        var clock = new Clock();
        clock.BeforeFirstTimer = () => clock.Advance(TimeSpan.FromSeconds(10));
        var passed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = Service(clock, called: () => passed.TrySetResult());
        service.Start();
        await clock.Registered();

        Assert.Equal(1, clock.Timers);
        clock.Advance(TimeSpan.FromSeconds(20));
        await passed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_timer_that_fires_before_the_wall_clock_due_time_waits_again_without_running_a_pass()
    {
        var clock = new Clock();
        var passed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var service = Service(clock, called: () => passed.TrySetResult());
        service.Start();
        await clock.Registered();

        clock.Offset = TimeSpan.FromSeconds(-10);
        clock.Advance(TimeSpan.FromSeconds(30));
        await clock.Registered();
        Assert.False(passed.Task.IsCompleted);
        Assert.Equal(2, clock.Timers);

        clock.Advance(TimeSpan.FromSeconds(10));
        await passed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    static KeepAwakeService Service(TimeProvider clock, bool enabled = true, bool devBox = true, Action? called = null, TimeSpan? firstPass = null) =>
        new(new KeepAwakeOptions
        {
            Settings = new DevBoxSettings { KeepAwake = enabled },
            FindIdentity = () => (devBox ? Box : null, null),
            Time = clock,
            FirstPass = firstPass ?? TimeSpan.FromSeconds(30),
            Token = new Token(),
            Http = new Http(called),
            Lock = new DelegateKeepAwakeLock(() => new MemoryStream()),
        });

    sealed class Token : IDevCenterToken
    {
        public Task<string> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult("fake-token");
    }

    sealed class Http(Action? called) : IDevCenterHttp
    {
        public Task<DevCenterResponse> SendAsync(HttpMethod method, Uri uri, string token, CancellationToken cancellationToken = default)
        {
            called?.Invoke();
            return Task.FromResult(new DevCenterResponse(200, """{"value":[]}"""));
        }
    }

    sealed class RealClock : TimeProvider
    {
        int timers;
        public int Timers => Volatile.Read(ref timers);
        public TaskCompletionSource<int> Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void PassReached() => Observed.TrySetResult(Timers);
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow();
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var before = System.GetUtcNow();
            var timer = System.CreateTimer(callback, state, dueTime, period);
            // Cross a real clock tick even on VMs with coarse clock resolution; never advance fake time.
            if (!SpinWait.SpinUntil(() => System.GetUtcNow() != before, TimeSpan.FromSeconds(1)))
            {
                timer.Dispose();
                throw new TimeoutException("The system UTC clock did not advance.");
            }
            var count = Interlocked.Increment(ref timers);
            if (count > 1) { Observed.TrySetResult(count); }
            return timer;
        }
    }

    sealed class Clock : FakeTimeProvider
    {
        readonly Channel<bool> registered = Channel.CreateUnbounded<bool>();
        readonly Channel<bool> released = Channel.CreateUnbounded<bool>();
        int timers;
        int active;
        public Clock() : base(Now) { }
        public int Timers => Volatile.Read(ref timers);
        public int ActiveTimers => Volatile.Read(ref active);
        public TimeSpan Offset { get; set; }
        public Action? BeforeFirstTimer { get; set; }
        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
        public Task Registered() => registered.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        public Task Released() => released.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (Interlocked.Increment(ref timers) == 1) { BeforeFirstTimer?.Invoke(); }
            var timer = new TrackedTimer(base.CreateTimer(callback, state, dueTime, period), this);
            Interlocked.Increment(ref active);
            if (dueTime != Timeout.InfiniteTimeSpan) { registered.Writer.TryWrite(true); }
            return timer;
        }

        sealed class TrackedTimer(ITimer timer, Clock clock) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                var changed = timer.Change(dueTime, period);
                clock.registered.Writer.TryWrite(true);
                return changed;
            }
            public void Dispose()
            {
                timer.Dispose();
                Interlocked.Decrement(ref clock.active);
                clock.released.Writer.TryWrite(true);
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
