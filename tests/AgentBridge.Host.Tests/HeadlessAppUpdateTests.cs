using AgentBridge.Host;
using AgentBridge.Host.Core.Updates;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// The host's half of the headless fleet update (docs/protocol/v1-update.md, "The update-only run", #3810 is the app's half): when a
// release is offered and the app is closed, start AgentBridge.exe --update-only --profile <p> once, as this host's own user. It never
// starts an app that predates the flag (that would open a full visible app), never while the app or an earlier update-only run holds the
// app's single-instance lock, and never faster than its interval.
public sealed class HeadlessAppUpdateTests
{
    static readonly UpdateRelease Offered = new("0.1.8000", null, null);
    const string Exe = @"C:\fixture\AgentBridge.exe";

    sealed class Starter : IAppStarter
    {
        public readonly List<(string Exe, string[] Arguments)> Started = [];
        public Exception? Fails;
        public void Start(string exe, IReadOnlyList<string> arguments)
        {
            if (Fails is not null) { throw Fails; }
            Started.Add((exe, [.. arguments]));
        }
    }

    // Every condition holds: the setting is on, the app is new enough and not running, a release is offered.
    static HeadlessAppUpdateOptions Ready() => new()
    {
        Profile = "default", Enabled = true, Windows = true, MinimumApp = "0.1.7600",
        InstalledApp = () => "0.1.7700", AppExe = () => Exe, AppLocked = () => false,
    };

    (HeadlessAppUpdate Update, Starter Starter, FakeTimeProvider Time, List<string> Logged) Rig(HeadlessAppUpdateOptions? options = null)
    {
        var (starter, time, logged) = (new Starter(), new FakeTimeProvider(new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero)), new List<string>());
        return (new HeadlessAppUpdate(options ?? Ready(), starter, time, logged.Add), starter, time, logged);
    }

    [Fact]
    public void An_offered_release_with_the_app_closed_starts_the_update_only_run_once_with_exactly_its_arguments()
    {
        var (update, starter, _, _) = Rig(Ready() with { Profile = "work" });

        var result = update.TryStart(Offered);

        Assert.Equal(HeadlessStartState.Started, result.State);
        var started = Assert.Single(starter.Started);
        Assert.Equal(Exe, started.Exe);
        Assert.Equal(new[] { "--update-only", "--profile", "work" }, started.Arguments);
    }

    public static TheoryData<string> Refusals => new() { "disabled", "not windows", "nothing offered", "no minimum", "app too old", "app unknown", "held", "app locked", "app process", "no exe" };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void It_never_starts_the_app_when_any_condition_fails(string why)
    {
        var o = Ready();
        o = why switch
        {
            "disabled" => o with { Enabled = false },
            "not windows" => o with { Windows = false },
            "no minimum" => o with { MinimumApp = null },
            "app too old" => o with { InstalledApp = () => "0.1.7599" },
            "app unknown" => o with { InstalledApp = () => null },
            "held" => o with { Held = version => version == "0.1.8000" },
            "app locked" => o with { AppLocked = () => true },
            "app process" => o with { AppLocked = () => null, AppProcessRunning = () => true }, // lock unreadable: the scan decides
            "no exe" => o with { AppExe = () => null },
            _ => o,
        };
        var (update, starter, _, _) = Rig(o);

        var result = update.TryStart(why == "nothing offered" ? null : Offered);

        Assert.False(result.Started);
        Assert.Empty(starter.Started);
    }

    [Fact]
    public void The_refusal_says_why()
    {
        Assert.Equal(HeadlessStartState.Disabled, Rig(Ready() with { Enabled = false }).Update.TryStart(Offered).State);
        Assert.Equal(HeadlessStartState.NotWindows, Rig(Ready() with { Windows = false }).Update.TryStart(Offered).State);
        Assert.Equal(HeadlessStartState.NothingOffered, Rig().Update.TryStart(null).State);
        Assert.Equal(HeadlessStartState.NoMinimumApp, Rig(Ready() with { MinimumApp = null }).Update.TryStart(Offered).State);
        Assert.Equal(HeadlessStartState.AppTooOld, Rig(Ready() with { InstalledApp = () => "0.1.7599" }).Update.TryStart(Offered).State);
        Assert.Equal(HeadlessStartState.Held, Rig(Ready() with { Held = _ => true }).Update.TryStart(Offered).State);
        Assert.Equal(HeadlessStartState.AppRunning, Rig(Ready() with { AppLocked = () => true }).Update.TryStart(Offered).State);
        Assert.Equal(HeadlessStartState.NoApp, Rig(Ready() with { AppExe = () => null }).Update.TryStart(Offered).State);
    }

    [Fact]
    public void An_app_exactly_at_the_minimum_version_may_be_started_and_one_below_may_not()
    {
        Assert.True(Rig(Ready() with { InstalledApp = () => "0.1.7600" }).Update.TryStart(Offered).Started);
        Assert.False(Rig(Ready() with { InstalledApp = () => "0.1.7599" }).Update.TryStart(Offered).Started);
        Assert.False(Rig(Ready() with { InstalledApp = () => "not a version" }).Update.TryStart(Offered).Started);
    }

    [Fact]
    public void It_starts_no_more_often_than_its_interval()
    {
        var (update, starter, time, _) = Rig();

        Assert.True(update.TryStart(Offered).Started);
        time.Advance(TimeSpan.FromMinutes(29));
        var early = update.TryStart(Offered);
        Assert.Equal(HeadlessStartState.Backoff, early.State);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(update.TryStart(Offered).Started);

        Assert.Equal(2, starter.Started.Count);
    }

    [Fact]
    public void A_failed_start_is_not_retried_until_a_backoff_that_doubles_up_to_its_limit_and_never_throws()
    {
        var (update, starter, time, logged) = Rig(Ready() with { FirstBackoff = TimeSpan.FromMinutes(5), LongestBackoff = TimeSpan.FromMinutes(20) });
        starter.Fails = new IOException("cannot start");

        Assert.Equal(HeadlessStartState.Failed, update.TryStart(Offered).State);
        time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(HeadlessStartState.Backoff, update.TryStart(Offered).State);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HeadlessStartState.Failed, update.TryStart(Offered).State); // the second failure: 10 minutes next
        time.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(HeadlessStartState.Backoff, update.TryStart(Offered).State);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HeadlessStartState.Failed, update.TryStart(Offered).State); // the third: 20 minutes, the limit
        time.Advance(TimeSpan.FromMinutes(19));
        Assert.Equal(HeadlessStartState.Backoff, update.TryStart(Offered).State);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HeadlessStartState.Failed, update.TryStart(Offered).State); // the fourth: still 20 minutes
        time.Advance(TimeSpan.FromMinutes(20));

        starter.Fails = null;
        Assert.True(update.TryStart(Offered).Started); // a start that works clears the failures
        Assert.Contains(logged, line => line.Contains("could not start", StringComparison.Ordinal));
    }

    [Fact]
    public void The_existing_single_instance_lock_covers_the_app_and_an_update_only_run_and_a_process_scan_is_only_the_fallback()
    {
        // The lock the app and its update-only run both claim (LocalAgentBridgeApp.<profile>) is the one check: while it can be read, no
        // process is scanned. Only when it cannot be read (null) does the scan decide.
        var scanned = 0;
        var (readable, starter, _, _) = Rig(Ready() with { AppLocked = () => false, AppProcessRunning = () => { scanned++; return true; } });
        Assert.True(readable.TryStart(Offered).Started);
        Assert.Equal(0, scanned);

        var (unreadable, blocked, _, _) = Rig(Ready() with { AppLocked = () => null, AppProcessRunning = () => { scanned++; return true; } });
        Assert.Equal(HeadlessStartState.AppRunning, unreadable.TryStart(Offered).State);
        Assert.Equal(1, scanned);
        Assert.Empty(blocked.Started);
        Assert.Single(starter.Started);

        // Unreadable and no scan to fall back on: it does not start (it cannot tell).
        var (unknown, none, _, _) = Rig(Ready() with { AppLocked = () => null });
        Assert.Equal(HeadlessStartState.AppRunning, unknown.TryStart(Offered).State);
        Assert.Empty(none.Started);
    }

    [Fact]
    public void The_real_lock_probe_sees_the_apps_single_instance_mutex_by_its_name()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var profile = "h2a" + Guid.NewGuid().ToString("N")[..8];
        Assert.False(AppSingleInstance.Held(profile));
        using var mutex = new Mutex(true, $@"Local\AgentBridgeApp.{profile}", out var mine);
        Assert.True(mine);
        Assert.True(AppSingleInstance.Held(profile));
    }

    [Fact]
    public void The_real_starter_starts_a_process_with_its_arguments_and_throws_when_the_program_is_missing()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var comspec = Environment.GetEnvironmentVariable("ComSpec")!;
        new ProcessAppStarter().Start(comspec, ["/c", "exit", "0"]);
        Assert.ThrowsAny<Exception>(() => new ProcessAppStarter().Start(Path.Combine(Path.GetTempPath(), "no-such-app-" + Guid.NewGuid().ToString("N") + ".exe"), ["--update-only"]));
    }
}
