using AgentBridge.Host;
using AgentBridge.Host.Delivery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class DepartureWatchdogTests
{
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    static async Task<(LifecycleCoordinator Coordinator, DeliveryGate Gate)> Committed(FakeTimeProvider time, DepartureWatchdog watchdog)
    {
        var gate = new DeliveryGate(time);
        var conditions = new DepartureConditions();
        conditions.Register(DeparturePurpose.RemoteRestart);
        var coordinator = new LifecycleCoordinator(gate, conditions, time: time, watchdog: watchdog);
        var lease = (await coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.RemoteRestart)).Value!;
        Assert.True((await coordinator.AnnouncePlanned(lease).WaitAsync(Bound)).Succeeded);
        Assert.True(coordinator.CommitDeparture(lease).Committed);
        return (coordinator, gate);
    }

    [Fact]
    public async Task A_committed_departure_is_not_stuck_before_the_bound()
    {
        var time = new FakeTimeProvider();
        var handler = new Handler();
        using var watchdog = new DepartureWatchdog(handler, time);
        var committedAt = time.GetUtcNow();
        var (coordinator, _) = await Committed(time, watchdog);
        time.Advance(DepartureWatchdog.DefaultBound - TimeSpan.FromTicks(1));
        Assert.Equal(new DepartureStatus(DepartureState.Committed, committedAt), coordinator.Departure);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Past_the_bound_it_is_stuck_once_and_the_default_handler_restarts_through_the_host_once()
    {
        var time = new FakeTimeProvider();
        var lines = new List<string>();
        var restarts = 0;
        using var watchdog = new DepartureWatchdog(new RestartingDepartureStuckHandler(lines.Add, () => restarts++), time, TimeSpan.FromMinutes(3));
        var committedAt = time.GetUtcNow();
        var (coordinator, gate) = await Committed(time, watchdog);
        time.Advance(TimeSpan.FromMinutes(3));
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(new DepartureStatus(DepartureState.Stuck, committedAt, DepartureWatchdog.StuckReason), coordinator.Departure);
        Assert.Equal(1, restarts);
        Assert.Equal(["departure stuck: committed departure did not exit; restarting the host"], lines);
        // Stuck reports the failure; it never reopens deliveries in this process.
        Assert.Null(gate.TryEnterDelivery());
    }

    [Fact]
    public async Task A_throwing_handler_does_not_crash_and_the_state_is_still_stuck()
    {
        var time = new FakeTimeProvider();
        var handler = new Handler { Throw = true };
        using var watchdog = new DepartureWatchdog(handler, time);
        var (coordinator, _) = await Committed(time, watchdog);
        time.Advance(DepartureWatchdog.DefaultBound);
        Assert.Equal(1, handler.Calls);
        Assert.Equal(DepartureState.Stuck, coordinator.Departure.State);
    }

    [Fact]
    public async Task Without_a_commit_there_is_no_watchdog()
    {
        var time = new FakeTimeProvider();
        var handler = new Handler();
        using var watchdog = new DepartureWatchdog(handler, time);
        var conditions = new DepartureConditions();
        conditions.Register(DeparturePurpose.RemoteRestart, new Blocking());
        var coordinator = new LifecycleCoordinator(new DeliveryGate(time), conditions, time: time, watchdog: watchdog);
        using var lease = (await coordinator.TryAcquireQuiet(TimeSpan.Zero, DeparturePurpose.RemoteRestart)).Value!;
        await coordinator.AnnouncePlanned(lease).WaitAsync(Bound);
        Assert.False(coordinator.CommitDeparture(lease).Committed);
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(DepartureStatus.None, coordinator.Departure);
        Assert.Equal(0, handler.Calls);
    }

    sealed class Handler : IDepartureStuckHandler
    {
        public bool Throw { get; init; }
        public int Calls { get; private set; }
        public void DepartureStuck(DepartureStatus status)
        {
            Calls++;
            if (Throw) { throw new InvalidOperationException("apply failed"); }
        }
    }

    sealed class Blocking : IDepartureCondition
    {
        public DepartureBlocker Kind => DepartureBlocker.AppRunning;
        public bool IsBlocking() => true;
    }
}
