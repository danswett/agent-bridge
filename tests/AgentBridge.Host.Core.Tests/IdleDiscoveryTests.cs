using System.Reflection;
using System.Threading.Channels;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public class IdleDiscoveryTests
{
    const string Id = "88888888-8888-4888-8888-888888888888";

    [Fact]
    public async Task Empty_source_does_not_repeat_one_second_discovery_between_recovery_deadlines()
    {
        using var f = new Fixture();
        var time = new CountingClock();
        var queries = 0;
        f.Processes.Queried = _ => Interlocked.Increment(ref queries);
        await using var source = Source(f, time);
        await source.StartAsync();
        var startup = queries;
        Assert.True(startup > 0);
        Assert.Equal(TimeSpan.FromSeconds(15), await time.NextTimer());

        for (var pass = 0; pass < 2; pass++)
        {
            time.Advance(TimeSpan.FromSeconds(14));
            Assert.Equal(startup * (pass + 1), Volatile.Read(ref queries));
            Assert.Equal(pass, time.Callbacks);
            time.Advance(TimeSpan.FromSeconds(1));
            // The next timer is armed only after reconciliation has finished.
            Assert.Equal(TimeSpan.FromSeconds(15), await time.NextTimer());
            Assert.Equal(startup * (pass + 2), Volatile.Read(ref queries));
        }
        Assert.Equal(2, time.Callbacks);
    }

    [Fact]
    public async Task Watcher_signal_wakes_idle_source_and_missed_signal_recovers_at_bound()
    {
        using var f = new Fixture();
        var time = new CountingClock();
        await using var source = Source(f, time);
        await source.StartAsync();
        Assert.Equal(TimeSpan.FromSeconds(15), await time.NextTimer());
        var transcript = f.CopilotSession(Id, 100);
        Fixture.Append(transcript, Copilot.User("go", 1));
        f.Processes.Run(AgentKind.Copilot, 100);

        Signal(source, transcript);
        Assert.Equal(TimeSpan.FromMilliseconds(50), await time.NextTimer());
        time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Equal(TimeSpan.FromSeconds(1), await time.NextTimer());
        Assert.NotNull(source.Get(Id));

        f.Processes.Exit(AgentKind.Copilot, 100);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), await time.NextTimer());
        Assert.Null(source.Get(Id));
        for (var second = 1; second < 30; second++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(TimeSpan.FromSeconds(1), await time.NextTimer());
        }
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(14), await time.NextTimer());

        f.Processes.Run(AgentKind.Copilot, 100);
        time.Advance(TimeSpan.FromSeconds(13));
        Assert.Null(source.Get(Id));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), await time.NextTimer());
        Assert.NotNull(source.Get(Id));
    }

    [Fact]
    public async Task Advancing_wall_clock_does_not_spin_idle_discovery()
    {
        using var f = new Fixture();
        var time = new CountingClock { StepUtc = true };
        var queries = 0;
        f.Processes.Queried = _ => Interlocked.Increment(ref queries);
        var source = Source(f, time);
        try
        {
            await source.StartAsync();
            Assert.Equal(TimeSpan.FromSeconds(15), await time.NextTimer());
            var startup = queries;
            time.UtcOffset = TimeSpan.FromDays(2);
            time.Advance(TimeSpan.FromSeconds(14));
            Assert.Equal(startup, Volatile.Read(ref queries));
            Assert.Equal(0, time.Callbacks);
            time.UtcOffset = TimeSpan.FromDays(-2);
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(TimeSpan.FromSeconds(15), await time.NextTimer());
            Assert.Equal(startup * 2, Volatile.Read(ref queries));
            Assert.Equal(1, time.Callbacks);
        }
        finally { await source.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)); }
    }

    [Fact]
    public async Task Held_discovery_retries_at_active_cadence_without_sessions()
    {
        using var f = new Fixture();
        f.Processes.Unreadable(AgentKind.Copilot);
        var time = new CountingClock();
        await using var source = Source(f, time);
        await source.StartAsync();
        Assert.NotNull(source.Held);
        Assert.Equal(TimeSpan.FromSeconds(1), await time.NextTimer());
        f.Processes.Unreadable(AgentKind.Copilot, false);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(15), await time.NextTimer());
        Assert.Null(source.Held);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Explicit_refresh_shortens_an_armed_idle_wait_for_sessions_and_holds(bool held)
    {
        using var f = new Fixture();
        var time = new CountingClock();
        await using var source = Source(f, time);
        await source.StartAsync();
        Assert.Equal(TimeSpan.FromSeconds(15), await time.NextTimer());
        time.Advance(TimeSpan.FromSeconds(1));
        if (held) { f.Processes.Unreadable(AgentKind.Copilot); }
        else
        {
            Fixture.Append(f.CopilotSession(Id, 100), Copilot.User("go", 3601));
            f.Processes.Run(AgentKind.Copilot, 100);
        }

        await source.RefreshAsync();

        Assert.Equal(TimeSpan.FromSeconds(1), await time.NextTimer());
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), await time.NextTimer());
        if (held)
        {
            Assert.NotNull(source.Held);
            f.Processes.Unreadable(AgentKind.Copilot, false);
            await source.RefreshAsync();
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(TimeSpan.FromSeconds(14), await time.NextTimer());
            Assert.Null(source.Held);
        }
        else { Assert.NotNull(source.Get(Id)); }
    }

    [Fact]
    public async Task A_data_wake_coalesced_behind_a_reschedule_still_keeps_active_recovery()
    {
        using var f = new Fixture();
        var time = new CountingClock();
        await using var source = Source(f, time);
        var wake = (Channel<bool>)typeof(SessionSource)
            .GetField("_wake", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
        Assert.True(wake.Writer.TryWrite(false));
        Signal(source, Path.Combine(f.Root, "events.jsonl"));

        await source.StartAsync();

        Assert.Equal(TimeSpan.FromSeconds(1), await time.NextTimer());
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), await time.NextTimer());
    }

    [Fact]
    public async Task A_silent_working_session_still_reaches_its_watchdog_deadline()
    {
        using var f = new Fixture();
        Fixture.Append(f.CopilotSession(Id, 100), Copilot.User("go", 3600), Copilot.TurnStart(3600));
        f.Processes.Run(AgentKind.Copilot, 100);
        var time = new CountingClock();
        await using var source = Source(f, time, TimeSpan.FromSeconds(5));
        await source.StartAsync();
        Assert.Equal(TimeSpan.FromSeconds(1), await time.NextTimer());
        for (var second = 1; second <= 5; second++)
        {
            Assert.Null(source.Get(Id)!.Attention);
            time.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(TimeSpan.FromSeconds(1), await time.NextTimer());
            if (second < 5) { Assert.Null(source.Get(Id)!.Attention); }
        }
        Assert.Equal(AttentionKind.NotResponding, source.Get(Id)!.Attention?.Kind);
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("active")]
    [InlineData("debouncing")]
    public async Task Shutdown_cancels_each_discovery_wait_without_advancing_time(string state)
    {
        using var f = new Fixture();
        if (state == "active")
        {
            Fixture.Append(f.CopilotSession(Id, 100), Copilot.User("go", 1));
            f.Processes.Run(AgentKind.Copilot, 100);
        }
        var time = new CountingClock();
        var source = Source(f, time);
        try
        {
            await source.StartAsync();
            await time.NextTimer();
            if (state == "debouncing")
            {
                Signal(source, Path.Combine(f.Root, "events.jsonl"));
                Assert.Equal(TimeSpan.FromMilliseconds(50), await time.NextTimer());
            }
        }
        finally { await source.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)); }
        var callbacks = time.Callbacks;
        time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(callbacks, time.Callbacks);
    }

    static SessionSource Source(Fixture f, CountingClock time, TimeSpan? watchdog = null) => new(new SessionSourceOptions
    {
        CopilotHome = f.CopilotHome,
        ClaudeHome = f.ClaudeHome,
        CodexHome = f.CodexHome,
        CoalesceInterval = TimeSpan.Zero,
        UseFileSystemWatcher = false,
        NotRespondingAfter = watchdog ?? TimeSpan.MaxValue,
        NotRespondingBusyAfter = TimeSpan.MaxValue,
    }, f.Processes, time);

    static void Signal(SessionSource source, string transcript) =>
        typeof(SessionSource).GetMethod("OnFileEvent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(source, [source, new FileSystemEventArgs(WatcherChangeTypes.Changed,
                Path.GetDirectoryName(transcript)!, Path.GetFileName(transcript))]);

    sealed class CountingClock : TimeProvider
    {
        readonly FakeTimeProvider _clock = new(Fixture.T0.AddHours(1));
        readonly Channel<TimeSpan> _timers = Channel.CreateUnbounded<TimeSpan>();
        long _reads;
        int _callbacks;
        public bool StepUtc { get; init; }
        public TimeSpan UtcOffset { get; set; }
        public int Callbacks => Volatile.Read(ref _callbacks);
        public override long TimestampFrequency => _clock.TimestampFrequency;
        public override long GetTimestamp() => _clock.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => _clock.GetUtcNow() + UtcOffset +
            (StepUtc ? TimeSpan.FromMilliseconds(Interlocked.Increment(ref _reads)) : TimeSpan.Zero);
        public void Advance(TimeSpan by) => _clock.Advance(by);
        public async Task<TimeSpan> NextTimer() =>
            await _timers.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = _clock.CreateTimer(s =>
            {
                Interlocked.Increment(ref _callbacks);
                callback(s);
            }, state, dueTime, period);
            _timers.Writer.TryWrite(dueTime);
            return timer;
        }
    }
}
