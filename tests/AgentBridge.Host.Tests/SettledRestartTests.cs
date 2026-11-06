using AgentBridge.Host;
using Xunit;

// host.restart and the departure watchdog leave the same way: a usage probe tied to this host
// settles first (#238), then the handover, then the stop. A stop asked for before the handover
// wins, so it is never turned into a restart.
public sealed class SettledRestartTests
{
    // A failure bound only: every wait here is released by the test itself.
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    sealed class Steps
    {
        public readonly List<string> Taken = [];
        public readonly CancellationTokenSource Stopping = new();
        public void Add(string step) { lock (Taken) { Taken.Add(step); } }
        public string[] Seen() { lock (Taken) { return [.. Taken]; } }
        public SettledRestart Restart(Func<TimeSpan, CancellationToken, Task>? settle) =>
            new(settle, () => Add("handover"), () => { Add("stop"); Stopping.Cancel(); }, Stopping.Token);
    }

    [Fact]
    public async Task A_restart_hands_over_only_once_the_probes_have_settled_then_stops()
    {
        var steps = new Steps();
        var settling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan? asked = null;
        CancellationToken given = default;
        var restart = steps.Restart((t, ct) => { asked = t; given = ct; steps.Add("settle"); return settling.Task; });

        var running = restart.RunAsync();
        Assert.Equal(["settle"], steps.Seen());

        settling.SetResult();
        await running.WaitAsync(Bound);
        Assert.Equal(["settle", "handover", "stop"], steps.Seen());
        Assert.Equal(SettledRestart.SettleFor, asked);
        Assert.Equal(steps.Stopping.Token, given);
    }

    [Fact]
    public async Task A_stop_during_the_settle_ends_the_wait_and_never_hands_over()
    {
        var steps = new Steps();
        // Never completes, and ignores the token: only the stop can end the wait.
        var settling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restart = steps.Restart((_, _) => { steps.Add("settle"); return settling.Task; });

        var running = restart.RunAsync();
        Assert.Equal(["settle"], steps.Seen());

        steps.Stopping.Cancel();
        await running.WaitAsync(Bound);
        Assert.Equal(["settle"], steps.Seen());
    }

    [Fact]
    public async Task A_stop_just_after_the_settle_and_before_the_handover_never_hands_over()
    {
        var steps = new Steps();
        // The probes have settled, and the stop lands as the settle returns.
        var restart = steps.Restart((_, _) => { steps.Add("settle"); steps.Stopping.Cancel(); return Task.CompletedTask; });

        await restart.RunAsync().WaitAsync(Bound);
        Assert.Equal(["settle"], steps.Seen());
    }

    [Fact]
    public async Task Without_a_usage_monitor_a_restart_hands_over_then_stops_unless_already_stopping()
    {
        var steps = new Steps();
        await steps.Restart(null).RunAsync().WaitAsync(Bound);
        Assert.Equal(["handover", "stop"], steps.Seen());

        var stopped = new Steps();
        stopped.Stopping.Cancel();
        await stopped.Restart(null).RunAsync().WaitAsync(Bound);
        Assert.Empty(stopped.Seen());
    }

    // With the real HostHandover (sr2-886d's probe saw a replacement here): the stop that cut the
    // settle short ends the host, and no replacement is started after it.
    [Fact]
    public async Task A_host_stopped_while_its_restart_is_settling_stops_without_starting_a_replacement()
    {
        var replacements = 0;
        var settling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var code = await HostHandover.RunAsync(async handover =>
        {
            using var stop = new CancellationTokenSource();
            var running = new SettledRestart((_, _) => settling.Task, handover.Request, stop.Cancel, stop.Token).RunAsync();

            // Ctrl+C, or the OS stopping it, while a probe is still in flight.
            stop.Cancel();
            await running.WaitAsync(Bound);
            // The host's shutdown then waits the probe out (UsageMonitor's disposal), as it did
            // when the restart went on to hand over.
            settling.SetResult();
            return 0;
        }, () => Interlocked.Increment(ref replacements));

        Assert.Equal(0, code);
        Assert.Equal(0, replacements);
    }
}