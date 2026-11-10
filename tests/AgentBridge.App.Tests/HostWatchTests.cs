using AgentBridge.App.Core;
using AgentBridge.Local;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.App.Tests;

// HostWatch (#1308, #581 S1): what the watch after an update decides, on a clock the test steps and
// a fake host (its relay state, its start failures, the relay's /healthz).
public sealed class HostWatchTests
{
    readonly FakeTimeProvider time = new(Make.T0);
    RelayState? relay;
    int failures;
    bool reachable = true;
    bool probeThrows;
    int probes;

    HostWatch Watch() => new(() => relay, () => failures,
        _ => { probes++; return probeThrows ? throw new HttpRequestException("no route") : Task.FromResult(reachable); }, time);

    // Steps the clock a poll at a time until the watch ends; a watch that never ends fails the test.
    async Task<HostWatchResult> RunAsync(HostWatch watch, Action<TimeSpan>? each = null)
    {
        var run = watch.RunAsync();
        for (var i = 0; i < 400 && !run.IsCompleted; i++)
        {
            time.Advance(watch.Poll);
            each?.Invoke(time.GetUtcNow() - Make.T0);
            await Task.Delay(1);
        }
        Assert.True(run.IsCompleted, "the watch never ended");
        return await run;
    }

    [Fact]
    public async Task A_host_that_crashes_on_three_starts_ends_the_watch_as_failed_before_the_ten_minutes_are_up()
    {
        relay = null;
        var result = await RunAsync(Watch(), elapsed => failures = elapsed >= TimeSpan.FromSeconds(30) ? 3 : elapsed >= TimeSpan.FromSeconds(15) ? 2 : 0);

        Assert.Equal(HostWatchEnd.Failed, result.End);
        Assert.Contains("3", result.Reason);
        Assert.True(time.GetUtcNow() - Make.T0 < HostWatch.Window);
        Assert.Equal(0, probes);
    }

    [Fact]
    public async Task A_host_whose_relay_link_hangs_in_connecting_while_the_relay_answers_fails_after_ten_minutes()
    {
        relay = RelayState.Connecting;
        var result = await RunAsync(Watch());

        Assert.Equal(HostWatchEnd.Failed, result.End);
        Assert.True(time.GetUtcNow() - Make.T0 >= HostWatch.Window);
        Assert.Contains("10 minutes", result.Reason);
    }

    [Fact]
    public async Task A_host_that_never_serves_while_the_relay_answers_fails_after_ten_minutes()
    {
        relay = null;
        var result = await RunAsync(Watch());

        Assert.Equal(HostWatchEnd.Failed, result.End);
        Assert.True(time.GetUtcNow() - Make.T0 >= HostWatch.Window);
    }

    [Fact]
    public async Task A_host_that_connects_ends_the_watch_without_blame_even_after_two_failed_starts()
    {
        failures = 2;
        relay = RelayState.Connecting;
        var result = await RunAsync(Watch(), elapsed => { if (elapsed >= TimeSpan.FromMinutes(2)) { relay = RelayState.Connected; } });

        Assert.Equal(HostWatchEnd.Connected, result.End);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task A_relay_that_does_not_answer_is_not_blamed_on_the_host()
    {
        relay = RelayState.Connecting;
        reachable = false;
        var result = await RunAsync(Watch());

        Assert.Equal(HostWatchEnd.Quiet, result.End);
        Assert.True(probes > 0);
    }

    [Fact]
    public async Task A_probe_that_throws_counts_as_the_relay_not_answering()
    {
        relay = null;
        probeThrows = true;
        var result = await RunAsync(Watch());

        Assert.Equal(HostWatchEnd.Quiet, result.End);
    }

    [Theory]
    [InlineData(RelayState.SignedOut)]
    [InlineData(RelayState.Off)]
    public async Task A_host_with_no_relay_to_reach_ends_the_watch_quietly_at_once(RelayState state)
    {
        relay = state;
        var result = await RunAsync(Watch());

        Assert.Equal(HostWatchEnd.Quiet, result.End);
        Assert.True(time.GetUtcNow() - Make.T0 < HostWatch.Window);
        Assert.Equal(0, probes);
    }
}
