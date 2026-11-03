using AgentBridge.HostLauncher;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// The logon task's launcher starting a crashed host again (#106), with a fake host and clock:
// a few times with growing waits, never after a stop someone meant.
public sealed class HostSupervisorTests
{
    const int UnhandledException = unchecked((int)0xE0434352);

    readonly FakeTimeProvider clock = new(Make.T0);
    readonly List<TimeSpan> waits = [];
    readonly List<string> logged = [];
    // The app's stop marker, as the launcher sees it; and what the next wait or run does to it.
    bool stopped;
    Action? duringWait;
    Action? duringRun;

    // Each run of the host lasts `ran`, then exits with the next code.
    HostSupervisor Supervising(Queue<int> codes, TimeSpan? ran = null, Func<int, TimeSpan>? lasted = null, bool windows = true)
    {
        var runs = 0;
        return new HostSupervisor
        {
            RunHost = _ =>
            {
                clock.Advance(lasted?.Invoke(runs) ?? ran ?? TimeSpan.FromSeconds(5));
                runs++;
                duringRun?.Invoke();
                return Task.FromResult(codes.Dequeue());
            },
            Time = clock,
            Delay = (d, _) => { waits.Add(d); clock.Advance(d); duringWait?.Invoke(); return Task.CompletedTask; },
            Log = logged.Add,
            Windows = windows,
            Stopped = () => stopped,
            ClearStopped = () => stopped = false,
        };
    }

    [Fact]
    public async Task A_stop_from_the_app_during_the_wait_after_a_crash_starts_no_host_again()
    {
        var codes = new Queue<int>([UnhandledException, 0]);
        duringWait = () => stopped = true;

        var exit = await Supervising(codes).RunAsync();

        Assert.Equal(UnhandledException, exit);
        Assert.Equal([0], codes.ToArray());
        Assert.Contains("the host was stopped from the Agent Bridge app; not starting it again", logged);
    }

    [Fact]
    public async Task A_stop_from_the_app_while_the_host_crashed_or_asked_to_restart_starts_no_host_again()
    {
        foreach (var code in new[] { UnhandledException, AgentBridge.Local.HostLauncherContract.Restart })
        {
            var codes = new Queue<int>([code, 0]);
            duringRun = () => stopped = true;

            var exit = await Supervising(codes).RunAsync();

            Assert.Equal(code, exit);
            Assert.Equal([0], codes.ToArray());
            Assert.Empty(waits);
        }
    }

    [Fact]
    public async Task A_launcher_that_starts_clears_an_earlier_stop_so_a_crash_is_still_recovered()
    {
        stopped = true;
        var codes = new Queue<int>([UnhandledException, 0]);

        var exit = await Supervising(codes).RunAsync();

        Assert.Equal(0, exit);
        Assert.Empty(codes);
        Assert.False(stopped);
    }

    [Fact]
    public async Task A_host_that_crashes_is_started_again_after_a_wait_that_grows_with_each_crash()
    {
        var codes = new Queue<int>([UnhandledException, UnhandledException, 0]);

        var exit = await Supervising(codes).RunAsync();

        Assert.Equal(0, exit);
        Assert.Empty(codes);
        Assert.Equal([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6)], waits);
        Assert.Contains("the host crashed (exit code 0xE0434352); starting it again in 3 s (attempt 1 of 3)", logged);
    }

    [Fact]
    public async Task A_host_that_keeps_crashing_is_given_up_on_after_the_last_attempt_and_the_log_says_so()
    {
        var codes = new Queue<int>([UnhandledException, UnhandledException, UnhandledException, UnhandledException, 0]);

        var exit = await Supervising(codes).RunAsync();

        Assert.Equal(UnhandledException, exit);
        Assert.Equal([0], codes.ToArray());
        Assert.Equal(HostSupervisor.DefaultBackoff, waits);
        Assert.Contains("not starting it again", logged[^1]);
    }

    [Fact]
    public async Task A_host_that_ran_a_long_while_before_crashing_gets_its_attempts_back()
    {
        var codes = new Queue<int>([UnhandledException, UnhandledException, UnhandledException, UnhandledException, 0]);

        // The fourth run lasts an hour; the rest seconds.
        var exit = await Supervising(codes, lasted: run => run == 3 ? TimeSpan.FromHours(1) : TimeSpan.FromSeconds(5)).RunAsync();

        Assert.Equal(0, exit);
        Assert.Equal([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(3)], waits);
    }

    [Fact]
    public async Task A_host_that_asks_to_be_restarted_is_started_again_at_once_and_its_crashes_counted_afresh()
    {
        var codes = new Queue<int>([UnhandledException, UnhandledException, AgentBridge.Local.HostLauncherContract.Restart, UnhandledException, 0]);

        var exit = await Supervising(codes).RunAsync();

        Assert.Equal(0, exit);
        Assert.Empty(codes);
        Assert.Equal([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(3)], waits);
    }

    // 0: stopped by the app, host.stop, an update or an uninstall; 2: usage; 3: another
    // host has the profile; 4: refused; -1: ended by the app's Process.Kill when it would not stop;
    // 1: Task Manager; 0xC000013A: Ctrl+C, closed or logged off.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(-1)]
    [InlineData(unchecked((int)0xC000013A))]
    public async Task A_host_that_was_stopped_on_purpose_is_never_started_again(int code)
    {
        var codes = new Queue<int>([code, 0]);

        var exit = await Supervising(codes).RunAsync();

        Assert.Equal(code, exit);
        Assert.Single(codes);
        Assert.Empty(waits);
        Assert.Empty(logged);
    }

    [Theory]
    [InlineData(134, true)]
    [InlineData(139, true)]
    [InlineData(143, false)]
    [InlineData(137, false)]
    [InlineData(130, false)]
    [InlineData(0, false)]
    [InlineData(3, false)]
    public async Task Off_windows_only_a_fatal_signal_counts_as_a_crash(int code, bool crash)
    {
        var codes = new Queue<int>([code, 0]);

        await Supervising(codes, windows: false).RunAsync();

        Assert.Equal(crash ? 1 : 0, waits.Count);
    }
}
