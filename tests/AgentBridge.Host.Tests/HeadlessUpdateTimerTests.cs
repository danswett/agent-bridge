using AgentBridge.Host;
using AgentBridge.Host.Core.Updates;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// H2b: the host's small timer that feeds HeadlessAppUpdate (#2869). Every 30 minutes it asks the host's existing update check, the one
// Home Assistant's Check uses, in its background mode (so ReleaseDiscovery's shared budget, backoff and cache apply, and no extra GitHub
// call is made inside its interval), whether a release is offered, and hands the answer to the starter. No op, no endpoint.
public sealed class HeadlessUpdateTimerTests
{
    static readonly UpdateRelease Offered = new("0.1.8000", null, null);
    const string Exe = @"C:\fixture\AgentBridge.exe";

    sealed class Updates : IHostUpdates
    {
        public string? Installed { get; set; } = "0.1.7700";
        public string? ReleasesUrl => null;
        public UpdateRelease? Release = Offered;
        public Exception? Fails;
        public Func<Task>? Slow;
        public readonly List<bool> Pressed = [];
        public Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken) => CheckAsync(cancellationToken, false);
        public async Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken, bool pressed)
        {
            lock (Pressed) { Pressed.Add(pressed); }
            if (Slow is not null) { await Slow(); }
            return Fails is not null ? throw Fails : Release;
        }
        public Task DownloadAsync(UpdateRelease release, Action<int> progress, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void ApplyAndRestart(UpdateRelease release) => throw new NotSupportedException();
        public int Asked { get { lock (Pressed) { return Pressed.Count; } } }
    }

    sealed class Starter : IAppStarter
    {
        public int Started;
        public void Start(string exe, IReadOnlyList<string> arguments) => Started++;
    }

    static HeadlessAppUpdateOptions Ready() => new()
    {
        Profile = "default", Enabled = true, Windows = true, MinimumApp = "0.1.7600", InstalledApp = () => "0.1.7700", AppExe = () => Exe, AppLocked = () => false,
    };

    static (HeadlessUpdateTimer Timer, Updates Updates, Starter Starter, FakeTimeProvider Time, List<string> Logged) Rig(Func<UpdateRelease, bool>? held = null, HeadlessAppUpdateOptions? options = null)
    {
        var (updates, starter, time, logged) = (new Updates(), new Starter(), new FakeTimeProvider(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero)), new List<string>());
        var timer = new HeadlessUpdateTimer(updates, new HeadlessAppUpdate(options ?? Ready(), starter, time, logged.Add),
            (release, _) => Task.FromResult(held?.Invoke(release) ?? false), time, logged.Add);
        return (timer, updates, starter, time, logged);
    }

    [Fact]
    public async Task A_tick_asks_the_existing_check_in_its_background_mode_once_and_starts_the_app_for_the_release_it_offers()
    {
        var (timer, updates, starter, _, _) = Rig();

        await timer.TickAsync();

        Assert.Equal([false], updates.Pressed); // background, never "pressed": the shared cache and budget apply
        Assert.Equal(1, starter.Started);
    }

    [Fact]
    public async Task Nothing_offered_a_held_release_or_a_throwing_check_starts_nothing()
    {
        var (a, updatesA, starterA, _, _) = Rig();
        updatesA.Release = null;
        await a.TickAsync();

        var (b, _, starterB, _, _) = Rig(held: _ => true);
        await b.TickAsync();

        var (c, updatesC, starterC, _, logged) = Rig();
        updatesC.Fails = new HttpRequestException("GitHub is limiting this network");
        await c.TickAsync(); // never throws out of the timer

        Assert.Equal((0, 0, 0), (starterA.Started, starterB.Started, starterC.Started));
        Assert.Contains(logged, line => line.Contains("headless update", StringComparison.Ordinal) && line.Contains("HttpRequestException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tick_that_is_still_running_is_not_overlapped()
    {
        var (timer, updates, starter, _, _) = Rig();
        var release = new TaskCompletionSource();
        updates.Slow = () => release.Task;

        var first = timer.TickAsync();
        await timer.TickAsync(); // returns at once: one is already running
        release.SetResult();
        await first;

        Assert.Equal(1, updates.Asked);
        Assert.Equal(1, starter.Started);
    }

    [Fact]
    public void The_timer_first_asks_two_minutes_after_it_begins_and_then_every_thirty_and_stops_when_disposed()
    {
        var (timer, updates, _, time, _) = Rig();
        timer.Begin();

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, updates.Asked);
        time.Advance(TimeSpan.FromMinutes(1));
        WaitFor(() => updates.Asked == 1);
        time.Advance(TimeSpan.FromMinutes(30));
        WaitFor(() => updates.Asked == 2);

        timer.Dispose();
        time.Advance(TimeSpan.FromHours(2));
        Thread.Sleep(100);
        Assert.Equal(2, updates.Asked);
    }

    [Fact]
    public void A_release_is_held_unless_the_policy_is_fresh_and_does_not_name_it()
    {
        var halts = new VerifiedHaltPolicy(2349, "{}", ["0.1.2349"]);
        var now = DateTimeOffset.UnixEpoch;

        Assert.True(HeadlessUpdateTimer.Held(new HaltPolicySnapshot(null, null, false), "0.1.8000"));   // unknown
        Assert.True(HeadlessUpdateTimer.Held(new HaltPolicySnapshot(halts, now, false), "0.1.8000"));   // stale
        Assert.True(HeadlessUpdateTimer.Held(new HaltPolicySnapshot(halts, now, true), "0.1.2349"));    // halted
        Assert.False(HeadlessUpdateTimer.Held(new HaltPolicySnapshot(halts, now, true), "0.1.8000"));   // fresh and not halted
    }

    [Fact]
    public void It_is_not_started_at_all_when_the_setting_is_off_or_there_is_no_updater_or_this_is_not_windows()
    {
        var updates = new Updates();
        static Task<HaltPolicySnapshot> Policy(CancellationToken _) => Task.FromResult(new HaltPolicySnapshot(null, null, false));

        Assert.Null(HeadlessUpdateTimer.Start(false, updates, "default", Policy));
        Assert.Null(HeadlessUpdateTimer.Start(true, null, "default", Policy));
        Assert.Null(HeadlessUpdateTimer.Start(true, updates, "default", Policy, options: Ready() with { Windows = false }));
        using var running = HeadlessUpdateTimer.Start(true, updates, "default", Policy, options: Ready(), starter: new Starter(), time: new FakeTimeProvider());
        Assert.NotNull(running);
    }

    [Fact]
    public void The_minimum_app_is_unknown_until_a_release_carries_the_flag_so_the_shipped_timer_never_starts_the_app_yet()
    {
        Assert.Null(HeadlessUpdateTimer.MinimumApp);
    }

    static void WaitFor(Func<bool> done)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!done()) { Assert.True(DateTime.UtcNow < until, "timed out"); Thread.Sleep(10); }
    }
}
