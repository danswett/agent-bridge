using System.Text.Json;
using AgentBridge.Host.Lite;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class AgentLiteKillTests : IDisposable
{
    const string Pinned = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string Stranger = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    readonly string root = Path.Combine(Path.GetTempPath(), "ablite-" + Guid.NewGuid().ToString("N")[..8]);

    sealed class Peers(params string[] ids) : ILitePeers
    {
        public bool TryFind(string deviceId, out LitePeer peer)
        {
            peer = new LitePeer(deviceId, [], "peer");
            return ids.Contains(deviceId);
        }
        public IReadOnlyList<LitePeer> All() => [];
    }

    public AgentLiteKillTests() { Directory.CreateDirectory(root); }
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    string NewHost(string name)
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        TurnOn(folder);
        return folder;
    }

    static void TurnOn(string folder) =>
        Assert.Equal(0, ConfigCommands.Run(["config", "set", "agentMessaging.remote", "own-account"], folder, TextWriter.Null, TextWriter.Null));

    LiteKill Host(string folder, params string[] pinned) => new(folder, new Peers(pinned));

    static JsonElement Epoch(long n) => JsonDocument.Parse($"{{\"epoch\":{n}}}").RootElement;

    [Fact]
    public void Kill_turns_the_mode_off_with_no_restart()
    {
        var folder = NewHost("a");
        var lite = Host(folder);
        Assert.True(lite.On);
        var epoch = lite.Kill();
        Assert.False(lite.On);
        Assert.Equal(epoch, lite.KillEpoch);
        Assert.False(Host(folder).On);
    }

    [Fact]
    public void A_higher_epoch_from_a_pinned_peer_turns_it_off()
    {
        var lite = Host(NewHost("a"), Pinned);
        Assert.True(lite.Observe(Pinned, 5));
        Assert.False(lite.On);
        Assert.Equal(5, lite.KillEpoch);
    }

    [Fact]
    public void An_unpinned_or_unknown_sender_does_nothing()
    {
        var logged = new List<string>();
        var lite = new LiteKill(NewHost("a"), new Peers(Pinned), logged.Add);
        Assert.False(lite.Observe(Stranger, 99));
        Assert.True(lite.On);
        Assert.Equal(0, lite.KillEpoch);
        Assert.Contains(logged, l => l.Contains("not a pinned"));
        var refused = JsonSerializer.Serialize(lite.HandleKill(Stranger, "host", Epoch(99)));
        Assert.Contains("false", refused);
        Assert.Contains("false", JsonSerializer.Serialize(lite.HandleKill(Pinned, "person", Epoch(99))));
        Assert.True(lite.On);
    }

    [Fact]
    public void A_lower_or_equal_epoch_does_nothing()
    {
        var folder = NewHost("a");
        var lite = Host(folder, Pinned);
        lite.RaiseEpoch(10);
        TurnOn(folder);
        Assert.False(lite.Observe(Pinned, 10));
        Assert.False(lite.Observe(Pinned, 3));
        Assert.True(lite.On);
        Assert.Equal(10, lite.KillEpoch);
    }

    [Fact]
    public void Nothing_a_peer_sends_can_turn_the_mode_on()
    {
        var folder = NewHost("a");
        var lite = Host(folder, Pinned);
        lite.Kill();
        lite.HandleKill(Pinned, "host", Epoch(0));
        lite.HandleKill(Pinned, "host", Epoch(-1));
        lite.Observe(Pinned, long.MaxValue);
        Assert.False(lite.On);
        // Only a person at the machine, with the setting, brings it back.
        TurnOn(folder);
        Assert.True(lite.On);
    }

    [Fact]
    public void An_epoch_far_ahead_of_the_clock_is_not_believed()
    {
        var lite = Host(NewHost("a"), Pinned);
        Assert.False(lite.Observe(Pinned, long.MaxValue));
        Assert.True(lite.On);
        Assert.Equal(0, lite.KillEpoch);
    }

    [Fact]
    public async Task An_in_flight_send_resolves_as_unconfirmed_when_killed()
    {
        var lite = Host(NewHost("a"));
        var started = new TaskCompletionSource();
        var send = lite.GuardAsync("r1", async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return new LiteOutcome(LiteKind.Delivered, null, "r1"); }, default);
        await started.Task;
        lite.Kill();
        var outcome = await send.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(LiteKind.Unconfirmed, outcome.Kind);
        Assert.Equal(LiteKind.Off, (await lite.GuardAsync("r2", _ => throw new InvalidOperationException("must not send"), default)).Kind);
    }

    [Fact]
    public async Task A_caller_cancelling_is_not_reported_as_a_kill()
    {
        var lite = Host(NewHost("a"));
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lite.GuardAsync("r", async ct => { await Task.Delay(Timeout.Infinite, ct); return null!; }, cts.Token));
    }

    [Fact]
    public void The_epoch_survives_a_restart_and_only_rises()
    {
        var folder = NewHost("a");
        var first = Host(folder, Pinned);
        first.RaiseEpoch(42);
        first.RaiseEpoch(7);
        Assert.Equal(42, Host(folder).KillEpoch);
        var again = Host(folder).Kill();
        Assert.True(again > 42);
    }

    [Fact]
    public void A_kill_on_one_host_reaches_another_pinned_host()
    {
        var a = Host(NewHost("a"));
        var b = Host(NewHost("b"), Pinned);
        var epoch = a.Kill();
        b.HandleKill(Pinned, "host", Epoch(epoch));
        Assert.False(b.On);
        Assert.Equal(epoch, b.KillEpoch);
    }

    [Fact]
    public void A_failed_epoch_write_still_turns_the_mode_off()
    {
        var folder = NewHost("a");
        Directory.CreateDirectory(Path.Combine(folder, LiteKill.FileName));
        var lite = Host(folder, Pinned);
        Assert.True(lite.Observe(Pinned, 5));
        Assert.False(lite.On);
        lite.Kill();
        Assert.False(lite.On);
    }

    [Fact]
    public void A_rejected_epoch_is_not_applied_and_does_not_silence_the_next_push()
    {
        var lite = Host(NewHost("a"), Pinned);
        var far = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 10 * 86400;
        Assert.Equal(false, ((dynamic)lite.HandleKill(Pinned, "host", Epoch(far))).applied);
        Assert.True(lite.On);
        Assert.Equal(true, ((dynamic)lite.HandleKill(Pinned, "host", Epoch(5))).applied);
    }

    [Fact]
    public void HandleKill_ignores_a_peer_that_is_not_a_pinned_host()
    {
        var lite = Host(NewHost("a"), Pinned);
        Assert.Equal(false, ((dynamic)lite.HandleKill(Pinned, "phone", Epoch(5))).applied);
        Assert.Equal(false, ((dynamic)lite.HandleKill(Stranger, "host", Epoch(5))).applied);
        Assert.True(lite.On);
    }

    [Fact]
    public void An_unreadable_epoch_file_stays_off_and_does_not_replay_a_lower_kill()
    {
        var folder = NewHost("a");
        var lite = Host(folder, Pinned);
        lite.RaiseEpoch(50);
        TurnOn(folder);
        File.WriteAllText(Path.Combine(folder, LiteKill.FileName), "garbage");
        Assert.False(lite.On);
        Assert.False(lite.Observe(Pinned, 40));
        Assert.Equal(50, lite.KillEpoch);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("5")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"epoch\":0}")]
    [InlineData("{\"epoch\":-1}")]
    [InlineData("{\"epoch\":\"7\"}")]
    public void A_malformed_epoch_file_fails_closed_and_keeps_the_high_water_mark(string content)
    {
        var folder = NewHost("a");
        var lite = Host(folder, Pinned);
        lite.RaiseEpoch(50);
        TurnOn(folder);
        File.WriteAllText(Path.Combine(folder, LiteKill.FileName), content);
        Assert.False(lite.On);
        Assert.Equal(50, lite.KillEpoch);
        Assert.False(lite.Observe(Pinned, 40));
        Assert.False(Host(folder).On);
    }

    [Fact]
    public void A_missing_epoch_file_after_a_kill_keeps_the_mark_and_stays_off()
    {
        var folder = NewHost("a");
        var lite = Host(folder, Pinned);
        lite.RaiseEpoch(50);
        TurnOn(folder);
        File.Delete(Path.Combine(folder, LiteKill.FileName));
        Assert.False(lite.On);
        Assert.Equal(50, lite.KillEpoch);
        Assert.False(lite.Observe(Pinned, 40));
    }

    [Fact]
    public async Task A_kill_by_another_process_cancels_the_sends_in_flight()
    {
        var folder = NewHost("a");
        var host = Host(folder);
        var started = new TaskCompletionSource();
        var send = host.GuardAsync("r1", async ct => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return new LiteOutcome(LiteKind.Delivered, null, "r1"); }, default);
        await started.Task;
        Host(folder).Kill();
        var outcome = await send.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(LiteKind.Unconfirmed, outcome.Kind);
    }

    [Fact]
    public void A_non_numeric_epoch_is_rejected()
    {
        var lite = Host(NewHost("a"), Pinned);
        Assert.Throws<ArgumentException>(() => lite.HandleKill(Pinned, "host", JsonDocument.Parse("{\"epoch\":\"x\"}").RootElement));
        Assert.True(lite.On);
    }

    [Fact]
    public async Task A_kill_by_another_process_does_not_block_turning_the_mode_on_again()
    {
        var folder = NewHost("a");
        var host = Host(folder);
        Host(folder).Kill();
        Assert.False(host.On);
        TurnOn(folder);
        Assert.True(host.On);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_watcher_of_a_finished_send_leaves_no_unobserved_exception()
    {
        var unobserved = new List<Exception>();
        // Host.Tests share one process, and a collection here finalizes other tests' abandoned tasks too:
        // keep only what the watcher itself leaked, which the lite kill's frames in its stack name.
        void Handler(object? s, UnobservedTaskExceptionEventArgs e)
        {
            var mine = e.Exception.Flatten().InnerExceptions.Any(x => x is OperationCanceledException or ObjectDisposedException
                && (x.StackTrace?.Contains(nameof(LiteKill), StringComparison.Ordinal) ?? false));
            if (mine) { lock (unobserved) { unobserved.Add(e.Exception); } }
        }
        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            var lite = Host(NewHost("a"));
            await lite.GuardAsync("r", _ => Task.FromResult(new LiteOutcome(LiteKind.Delivered, null, "r")), default);
            await Task.Delay(1500);
            GC.Collect(); GC.WaitForPendingFinalizers();
            Assert.Empty(unobserved);
        }
        finally { TaskScheduler.UnobservedTaskException -= Handler; }
    }

    [Fact]
    public async Task A_send_that_fails_on_its_own_is_not_reported_as_killed()
    {
        var lite = Host(NewHost("a"));
        await Assert.ThrowsAsync<OperationCanceledException>(() => lite.GuardAsync("r", _ => throw new OperationCanceledException(), CancellationToken.None));
    }
}
