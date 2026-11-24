using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery.Launch;
using Xunit;

// #2770: a fleet launch is admitted against an hourly cap and a live-worker cap inside the launch record's own lock, so the count is atomic and
// survives a restart. Coordinators (agent or person launches) are never counted.
public sealed class FleetAdmissionTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("ab-fleet-admit-").FullName;
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-24T10:00:00Z");
    string Path => System.IO.Path.Combine(folder, "launch-provenance.json");
    LaunchProvenance Open() => new(Path, _ => { });
    public void Dispose() => Directory.Delete(folder, true);

    // Starts one fleet worker: admitted, bound to a process that started at `at`, and (unless it ends) still running.
    static string? Start(LaunchProvenance store, int n, FleetLimits limits, DateTimeOffset at, Dictionary<string, SessionProcess> running, bool keepRunning = true)
    {
        var request = Guid.NewGuid();
        var session = $"worker-{n}";
        var refused = store.ReserveFleet(request, session, null, limits, at, id => running.GetValueOrDefault(id));
        if (refused is not null) { return refused; }
        var process = new SessionProcess(1000 + n, at);
        store.Bound(request, session, process);
        if (keepRunning) { running[session] = process; }
        return null;
    }

    [Fact]
    public void A_script_credential_cannot_exceed_the_fleet_cap_of_8_an_hour_nor_6_live_workers()
    {
        var hourly = Open();
        var none = new Dictionary<string, SessionProcess>();
        for (var i = 1; i <= 8; i++) { Assert.Null(Start(hourly, i, new(8, 60), Now.AddMinutes(i), none, keepRunning: false)); }
        Assert.Contains("8 an hour", Start(hourly, 9, new(8, 60), Now.AddMinutes(9), none, keepRunning: false));
        Assert.Null(Start(hourly, 10, new(8, 60), Now.AddMinutes(61), none, keepRunning: false));

        var live = Open();
        var running = new Dictionary<string, SessionProcess>();
        var other = Directory.CreateTempSubdirectory("ab-fleet-admit-live-").FullName;
        try
        {
            var store = new LaunchProvenance(System.IO.Path.Combine(other, "p.json"), _ => { });
            for (var i = 1; i <= 6; i++) { Assert.Null(Start(store, i, new(60, 6), Now, running)); }
            Assert.Contains("6 live", Start(store, 7, new(60, 6), Now, running));
            running.Remove("worker-3");
            Assert.Null(Start(store, 8, new(60, 6), Now, running));
        }
        finally { Directory.Delete(other, true); }
    }

    [Fact]
    public void A_coordinator_can_launch_while_the_fleet_cap_is_full()
    {
        var store = Open();
        var running = new Dictionary<string, SessionProcess>();
        for (var i = 1; i <= 8; i++) { Assert.Null(Start(store, i, new(8, 60), Now, running)); }
        Assert.NotNull(Start(store, 9, new(8, 60), Now, running));
        var coordinator = Guid.NewGuid();
        store.Preparing(coordinator, "coordinator", null);
        store.Bound(coordinator, "coordinator", new SessionProcess(4242, Now));
        Assert.NotNull(Start(store, 10, new(8, 60), Now, running));
    }

    [Fact]
    public void A_restart_keeps_the_count_and_the_live_workers()
    {
        var store = Open();
        var running = new Dictionary<string, SessionProcess>();
        for (var i = 1; i <= 8; i++) { Assert.Null(Start(store, i, new(8, 60), Now, running)); }
        var reopened = Open();
        Assert.Contains("8 an hour", Start(reopened, 9, new(8, 60), Now.AddMinutes(5), running));
        Assert.Contains("6 live", Start(reopened, 9, new(60, 6), Now.AddMinutes(5), running));
    }

    [Fact]
    public void Two_launches_at_once_cannot_both_take_the_last_slot()
    {
        var store = Open();
        var running = new Dictionary<string, SessionProcess>();
        var admitted = 0;
        Parallel.For(0, 40, i =>
        {
            if (store.ReserveFleet(Guid.NewGuid(), $"racer-{i}", null, new(8, 60), Now, id => null) is null) { Interlocked.Increment(ref admitted); }
        });
        Assert.Equal(8, admitted);
        Assert.Empty(running);
    }

    [Fact]
    public void An_unfinished_reservation_counts_until_it_is_abandoned()
    {
        var store = Open();
        var request = Guid.NewGuid();
        Assert.Null(store.ReserveFleet(request, "pending", null, new(1, 1), Now, _ => null));
        Assert.NotNull(store.ReserveFleet(Guid.NewGuid(), "second", null, new(1, 1), Now, _ => null));
        store.Abandon(request);
        Assert.Null(store.ReserveFleet(Guid.NewGuid(), "third", null, new(1, 1), Now, _ => null));
    }

    // P1 of the #4459 read: a reservation whose worker never registers must not starve the dispatcher. It stops counting after its lifetime (also across a
    // restart, with no deletion: an unbound row stays so a still-running unregistered process is never adoptable), and a proven failure releases it at once.
    [Fact]
    public void An_unbound_reservation_stops_counting_after_its_lifetime_even_across_a_restart()
    {
        var store = Open();
        for (var i = 1; i <= 6; i++) { Assert.Null(store.ReserveFleet(Guid.NewGuid(), $"lost-{i}", null, new(60, 6), Now, _ => null)); }
        Assert.Contains("6 live", Open().ReserveFleet(Guid.NewGuid(), "seventh", null, new(60, 6), Now.AddMinutes(10), _ => null));
        var lifetime = LaunchProvenance.FleetReservationLifetime;
        Assert.Contains("6 live", Open().ReserveFleet(Guid.NewGuid(), "still-held", null, new(60, 6), Now + lifetime - TimeSpan.FromSeconds(1), _ => null));
        Assert.Null(Open().ReserveFleet(Guid.NewGuid(), "after-lifetime", null, new(60, 6), Now + lifetime + TimeSpan.FromSeconds(1), _ => null));
    }

    [Fact]
    public void A_proven_failure_releases_its_reservation_and_a_bound_launch_is_never_released()
    {
        var store = Open();
        var failed = Guid.NewGuid();
        Assert.Null(store.ReserveFleet(failed, "never-registered", null, new(1, 1), Now, _ => null));
        Assert.NotNull(store.ReserveFleet(Guid.NewGuid(), "blocked", null, new(1, 1), Now, _ => null));
        Assert.True(store.ReleaseFleetReservation(failed));
        var next = Guid.NewGuid();
        Assert.Null(store.ReserveFleet(next, "after-release", null, new(1, 1), Now, _ => null));
        store.Bound(next, "after-release", new SessionProcess(4000, Now));
        Assert.False(store.ReleaseFleetReservation(next));
        Assert.False(store.ReleaseFleetReservation(Guid.NewGuid()));
        Assert.True(Open().IsFleetWorker("after-release", new SessionProcess(4000, Now)));
    }

    [Fact]
    public void A_fleet_resume_row_without_a_reservation_does_not_count_against_the_caps()
    {
        var store = Open();
        store.Preparing(Guid.NewGuid(), "resumed", "fleet");
        Assert.Null(store.ReserveFleet(Guid.NewGuid(), "new-one", null, new(1, 1), Now, _ => null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_limit_below_one_is_refused_not_unlimited(int limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Open().ReserveFleet(Guid.NewGuid(), "s", null, new(limit, 6), Now, _ => null));
}
