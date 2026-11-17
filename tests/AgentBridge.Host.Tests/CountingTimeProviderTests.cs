using AgentBridge.Host.Core.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class CountingTimeProviderTests
{
    [Fact]
    public async Task Counting_time_provider_counts_timers_created_and_fired_per_owner()
    {
        var clock = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromTicks(1) };
        var meter = new CountingTimeProvider(clock);
        var discovery = meter.ForOwner("discovery");
        var keepAwake = meter.ForOwner("keep-awake");
        var state = new object();
        object? received = null;
        await using var first = discovery.CreateTimer(s => received = s, state, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        using var second = keepAwake.CreateTimer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Assert.True(second.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan));
        clock.Advance(TimeSpan.FromSeconds(3));

        var snapshot = meter.Snapshot();
        Assert.Same(state, received);
        Assert.Equal(new TimerCounts(1, 3), snapshot.Timers["discovery"]);
        Assert.Equal(new TimerCounts(1, 1), snapshot.Timers["keep-awake"]);
        using var third = meter.ForOwner("discovery").CreateTimer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Assert.Equal(2, meter.Snapshot().Timers["discovery"].Created);
        Assert.Equal(1, snapshot.Timers["discovery"].Created);
        await first.DisposeAsync();
        second.Dispose();
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(3, meter.Snapshot().Timers["discovery"].Fired);
    }

    [Fact]
    public async Task Counting_time_provider_does_not_change_timer_due_times()
    {
        var clock = new FakeTimeProvider { AutoAdvanceAmount = TimeSpan.FromTicks(1) };
        clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("fixture", TimeSpan.FromHours(2), "fixture", "fixture"));
        var meter = new CountingTimeProvider(clock).ForOwner("scheduler");
        Assert.Equal(clock.LocalTimeZone, meter.LocalTimeZone);
        Assert.Equal(clock.TimestampFrequency, meter.TimestampFrequency);
        Assert.InRange(meter.GetUtcNow() - clock.GetUtcNow(), TimeSpan.FromTicks(-1), TimeSpan.Zero);
        Assert.InRange(meter.GetTimestamp() - clock.GetTimestamp(), -1L, 0L);
        var calls = 0;
        using var timer = meter.CreateTimer(_ => calls++, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(0, calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, calls);
        Assert.True(timer.Change(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan));
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(1, calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, calls);
        using var stop = new CancellationTokenSource();
        var delay = Task.Delay(TimeSpan.FromHours(1), meter, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delay);
        Assert.Throws<ArgumentOutOfRangeException>(() => meter.CreateTimer(_ => { }, null, TimeSpan.FromMilliseconds(-2), Timeout.InfiniteTimeSpan));
        Assert.Equal(2, meter.Snapshot().Timers["scheduler"].Created);
    }
}
