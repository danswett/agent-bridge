using AgentBridge.Host;
using AgentBridge.Host.Delivery;
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
    public async Task A_stop_interrupts_a_restart_waiting_for_a_delivery_without_handing_over()
    {
        var gate = new DeliveryGate();
        var delivery = gate.TryEnterDelivery()!;
        var stops = new PlannedStops(gate, Bound, _ => { });
        var handovers = 0;
        using var ending = new CancellationTokenSource();
        var restart = new SettledRestart(null, () => handovers++, ending.Cancel, ending.Token,
            ct => gate.PrepareStopAsync(Bound, ct), stops.TryCommitRestart);
        var running = restart.RunAsync(stops.RestartToken());
        Assert.Null(gate.TryEnterDelivery());

        var stopping = stops.PrepareStopAsync();
        Assert.False(stopping.IsCompleted);
        delivery.Dispose();
        using var lease = await stopping.WaitAsync(Bound);
        Assert.NotNull(lease);
        await running.WaitAsync(Bound);
        Assert.Equal(0, handovers);
        Assert.False(ending.IsCancellationRequested);
        Assert.Null(gate.TryEnterDelivery());
    }

    [Fact]
    public async Task A_stop_refused_for_a_delivery_outlasting_its_timeout_leaves_a_waiting_restart_to_hand_over()
    {
        var gate = new DeliveryGate();
        var delivery = gate.TryEnterDelivery()!;
        var stops = new PlannedStops(gate, TimeSpan.FromMilliseconds(50), _ => { });
        var handovers = 0;
        using var ending = new CancellationTokenSource();
        var restart = new SettledRestart(null, () => handovers++, ending.Cancel, ending.Token,
            ct => gate.PrepareStopAsync(Bound, ct), stops.TryCommitRestart);
        var interrupted = stops.RestartToken();
        var running = restart.RunAsync(interrupted);

        Assert.Null(await stops.PrepareStopAsync().WaitAsync(Bound));
        Assert.False(interrupted.IsCancellationRequested);
        Assert.False(running.IsCompleted);
        delivery.Dispose();
        await running.WaitAsync(Bound);
        Assert.Equal(1, handovers);
        Assert.True(ending.IsCancellationRequested);
    }

    [Fact]
    public async Task A_stop_before_the_restart_commits_wins_even_after_the_restart_has_the_fence()
    {
        var gate = new DeliveryGate();
        var stops = new PlannedStops(gate, Bound, _ => { });
        var atCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var proceed = new ManualResetEventSlim();
        using var ending = new CancellationTokenSource();
        var handovers = 0;
        var restart = new SettledRestart(null, () => handovers++, ending.Cancel, ending.Token,
            ct => gate.PrepareStopAsync(Bound, ct),
            (ct, handover) =>
            {
                atCommit.SetResult();
                if (!proceed.Wait(Bound)) { throw new TimeoutException("restart commit was not released"); }
                return stops.TryCommitRestart(ct, handover);
            });
        var running = Task.Run(() => restart.RunAsync(stops.RestartToken()));
        try
        {
            await atCommit.Task.WaitAsync(Bound);
            var stopping = stops.PrepareStopAsync();
            proceed.Set();
            await running.WaitAsync(Bound);
            using var lease = await stopping.WaitAsync(Bound);
            Assert.NotNull(lease);
            Assert.Equal(0, handovers);
            Assert.False(ending.IsCancellationRequested);
        }
        finally { proceed.Set(); }
    }

    [Fact]
    public async Task Releasing_an_old_stop_lease_cannot_release_a_new_one()
    {
        var gate = new DeliveryGate();
        var stops = new PlannedStops(gate, Bound, _ => { });
        var first = await stops.PrepareStopAsync();
        Assert.NotNull(first);
        first.Dispose();
        using var second = await stops.PrepareStopAsync();
        Assert.NotNull(second);

        first.Dispose();
        Assert.Null(gate.TryEnterDelivery());
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