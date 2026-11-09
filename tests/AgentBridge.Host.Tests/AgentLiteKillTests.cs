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
}
