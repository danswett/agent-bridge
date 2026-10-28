using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentBridge.Transport.Tests;

// End-to-end channels through the relay, and what a device does about peers and relays
// that misbehave.
public class ChannelTests
{
    const string Marker = "AGENT-BRIDGE-PLAINTEXT-MARKER";

    static async Task<(TestRelay Relay, Device Host, Device App)> PairAsync(Action<AgentBridge.Relay.RelayOptions>? configure = null)
    {
        var relay = await TestRelay.StartAsync(configure);
        var token = relay.TokenFor(1, "octo");
        var host = await relay.ConnectAsync(token, "host-a", "host");
        var app = await relay.ConnectAsync(token, "app-b", "app");
        await app.Relay.WaitForPeerAsync(p => p.Id == host.Id, TimeSpan.FromSeconds(10));
        return (relay, host, app);
    }

    [Fact]
    public async Task A_channel_authenticates_both_ends_each_key_hashing_to_the_id_it_was_reached_at()
    {
        var (relay, host, app) = await PairAsync();
        await using var _ = relay;
        var accepted = new TaskCompletionSource<SecureChannel>();
        host.Hub.Accepted += ch => accepted.TrySetResult(ch);

        var ch = await app.Hub.OpenAsync(host.Id);
        var other = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(host.Id, Wire.DeviceId(ch.PeerStatic!));
        Assert.Equal(app.Id, Wire.DeviceId(other.PeerStatic!));
        Assert.Equal(("host-a", "host"), (ch.PeerName, ch.PeerKind));
        Assert.Equal(("app-b", "app"), (other.PeerName, other.PeerKind));
        Assert.Equal(ch.HandshakeHash, other.HandshakeHash);
        Assert.True(ch.IsOpen && other.IsOpen);
    }

    [Fact]
    public async Task A_request_crosses_the_relay_end_to_end_and_the_relay_sees_and_logs_only_ciphertext()
    {
        await using var relay = await TestRelay.StartAsync();
        // Signed in through the device flow, so the relay has handled every credential there is.
        var account = await relay.SignInAsync(new GitHubUser(1, "octo"));
        var host = await relay.ConnectAsync(account.Token, "host-a", "host");
        var app = await relay.ConnectAsync(account.Token, "app-b", "app");
        await app.Relay.WaitForPeerAsync(p => p.Id == host.Id, TimeSpan.FromSeconds(10));
        var ch = await app.Hub.OpenAsync(host.Id);
        relay.Taps.Clear();
        var text = $"{Marker} café – 世界 ✓";

        var reply = await ch.RequestAsync("echo", new { text });

        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(text, reply.GetProperty("result").GetProperty("text").GetString());
        Assert.True(relay.Taps.Count >= 2, $"{relay.Taps.Count} frames tapped");
        var markerBytes = Encoding.UTF8.GetBytes(Marker);
        Assert.DoesNotContain(relay.Taps, p => p.AsSpan().IndexOf(markerBytes) >= 0);
        var logs = string.Join("\n", relay.Logs);
        Assert.DoesNotContain(Marker, logs);
        Assert.DoesNotContain(account.Token, logs);
        Assert.DoesNotContain("gho_", logs);
    }

    [Fact]
    public async Task A_60000_byte_message_crosses_the_relay()
    {
        var (relay, host, app) = await PairAsync();
        await using var _ = relay;
        var ch = await app.Hub.OpenAsync(host.Id);
        var big = new string('x', 60_000);

        var reply = await ch.RequestAsync("echo", new { big });

        Assert.Equal(big, reply.GetProperty("result").GetProperty("big").GetString());
    }

    [Fact]
    public async Task A_response_too_large_for_one_Noise_message_comes_back_as_an_error_and_the_channel_keeps_working()
    {
        var (relay, host, app) = await PairAsync();
        await using var _ = relay;
        host.Hub.Handler = (_, op, data) => op == "huge" ? Task.FromResult<object?>(new { text = new string('y', 70_000) }) : TestRelay.Demo(op, data);
        var ch = await app.Hub.OpenAsync(host.Id);

        var huge = await ch.RequestAsync("huge");
        var after = await ch.RequestAsync("echo", new { n = 1 });

        Assert.False(huge.GetProperty("ok").GetBoolean());
        Assert.StartsWith("response too large", huge.GetProperty("error").GetString());
        Assert.Equal(1, after.GetProperty("result").GetProperty("n").GetInt32());
        Assert.Null(host.Relay.CloseStatus);
        await Assert.ThrowsAsync<MessageTooLargeException>(() => ch.RequestAsync("echo", new { text = new string('z', 70_000) }));
        Assert.True(ch.IsOpen);
    }

    [Fact]
    public async Task A_relay_that_redirects_a_channel_to_another_device_is_detected()
    {
        var (relay, host, app) = await PairAsync();
        await using var _ = relay;
        var decoy = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "host-d", "host");
        await app.Relay.WaitForPeerAsync(p => p.Id == decoy.Id, TimeSpan.FromSeconds(10));
        string a = host.Id, b = app.Id, d = decoy.Id;
        // Frames from the app to host-a go to host-d instead, and host-d's answers are
        // relabelled as coming from host-a.
        relay.Route = (f, t) => f == b && t == a ? (b, d) : f == d && t == b ? (a, b) : null;

        // The prologue binds both routing ids, so the handshake fails authentication; a
        // relay that also forged the prologue would meet the key-to-id check instead.
        await Assert.ThrowsAnyAsync<CryptographicException>(() => app.Hub.OpenAsync(a, TimeSpan.FromSeconds(10)));
    }

    // A valid first handshake message on a fresh channel, never followed up.
    static async Task<ulong> SendMsg1Async(Device from, string to)
    {
        var id = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        var hs = new XXHandshake(true, from.Key, []);
        var header = new byte[Wire.ChannelHeader];
        header[0] = (byte)ChannelKind.Msg1;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(1), id);
        await from.Relay.SendAsync(to, [.. header, .. hs.WriteMessage([])]);
        return id;
    }

    static async Task SendGarbageMsg3Async(Device from, string to, ulong id)
    {
        var header = new byte[Wire.ChannelHeader];
        header[0] = (byte)ChannelKind.Msg3;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(1), id);
        await from.Relay.SendAsync(to, [.. header, .. new byte[100]]);
    }

    // A raw device: no hub, just the Msg2 replies it is sent, by channel id.
    static async Task<(Device Device, System.Collections.Concurrent.ConcurrentBag<ulong> Msg2)> HalfOpenerAsync(TestRelay relay, Device target)
    {
        var k = P256KeyPair.Generate();
        var r = await RelayClient.ConnectAsync(relay.Url, relay.TokenFor(1, "octo"), k, "half-open", "test");
        var replies = new System.Collections.Concurrent.ConcurrentBag<ulong>();
        r.OnData = (_, payload) =>
        {
            if (payload[0] == (byte)ChannelKind.Msg2) { replies.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(1, 8))); }
            return Task.CompletedTask;
        };
        await r.WaitForPeerAsync(p => p.Id == target.Id, TimeSpan.FromSeconds(10));
        return (new Device(k, r, null!), replies);
    }

    [Fact]
    public async Task A_host_with_a_full_channel_table_ignores_further_handshakes()
    {
        await using var relay = await TestRelay.StartAsync();
        var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "bounded-host", "host");
        host.Hub.MaxChannels = 4;
        var (attacker, msg2) = await HalfOpenerAsync(relay, host);
        await using var _ = attacker;
        var rejected = new TaskCompletionSource<Exception>();
        host.Hub.Rejected += (_, e) => rejected.TrySetResult(e);

        var ids = new List<ulong>();
        for (int i = 0; i < 10; i++) { ids.Add(await SendMsg1Async(attacker, host.Id)); }
        await Wait.UntilAsync(() => msg2.Count == 4, "four handshakes to be answered");
        // Frames from one sender are handled in order, so once this one has been, so have
        // all ten before it. It fails authentication and ends the first channel.
        await SendGarbageMsg3Async(attacker, host.Id, ids[0]);
        await rejected.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ids.Take(4).Order(), msg2.Order());
        Assert.Equal(3, host.Hub.ChannelCount);
    }

    [Fact]
    public async Task A_host_lets_unfinished_handshakes_expire_to_make_room()
    {
        await using var relay = await TestRelay.StartAsync();
        var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "bounded-host", "host");
        host.Hub.MaxChannels = 4;
        var (attacker, msg2) = await HalfOpenerAsync(relay, host);
        await using var _ = attacker;
        for (int i = 0; i < 4; i++) { await SendMsg1Async(attacker, host.Id); }
        await Wait.UntilAsync(() => msg2.Count == 4, "the table to fill");
        var filled = Environment.TickCount64;

        host.Hub.HandshakeTimeout = TimeSpan.FromMilliseconds(50);
        // The clock the hub reads ticks every 15.6 ms on Windows: wait for it, not a guess.
        await Wait.UntilAsync(() => Environment.TickCount64 - filled > 100, "the handshakes to be older than the timeout");
        // Expiry is checked when a handshake arrives, so this one makes room for itself.
        var late = await SendMsg1Async(attacker, host.Id);
        await Wait.UntilAsync(() => msg2.Contains(late), "the new handshake to be admitted");

        Assert.Equal(1, host.Hub.ChannelCount);
    }

    [Fact]
    public async Task A_host_drops_a_peers_channels_when_the_peer_leaves_and_tells_whoever_is_waiting_on_them()
    {
        await using var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(1, "octo");
        var host = await relay.ConnectAsync(token, "host", "host");
        host.Hub.PresenceGrace = TimeSpan.Zero;
        var accepted = new TaskCompletionSource<SecureChannel>();
        host.Hub.Accepted += ch => accepted.TrySetResult(ch);
        var peer = await relay.ConnectAsync(token, "short-lived", "app");
        await peer.Relay.WaitForPeerAsync(p => p.Id == host.Id, TimeSpan.FromSeconds(10));
        await peer.Hub.OpenAsync(host.Id);
        var atHost = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var opened = Environment.TickCount64;
        // With no grace, a channel is still spared within the clock tick it was made in
        // (15.6 ms on Windows), so let one pass.
        await Wait.UntilAsync(() => Environment.TickCount64 - opened > 20, "the clock to move past the channel's creation");

        await peer.DisposeAsync();

        await atHost.Closed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, host.Hub.ChannelCount);
        Assert.IsType<IOException>(atHost.CloseReason);
        await Assert.ThrowsAsync<IOException>(() => atHost.SendEventAsync("session.upsert", new { }));
    }

    [Fact]
    public async Task A_channels_requests_fail_at_once_when_its_relay_connection_ends()
    {
        var (relay, host, app) = await PairAsync();
        await using var _ = relay;
        var never = new TaskCompletionSource<object?>();
        host.Hub.Handler = (_, _, _) => never.Task;
        var ch = await app.Hub.OpenAsync(host.Id);
        var waiting = ch.RequestAsync("hang", timeout: TimeSpan.FromMinutes(5));

        await app.Relay.DisposeAsync();

        await Assert.ThrowsAsync<IOException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.False(ch.IsOpen);
        Assert.Equal(0, app.Hub.ChannelCount);
    }

    [Fact]
    public async Task A_channel_opened_while_the_target_is_still_attaching_its_handler_is_not_lost()
    {
        await using var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(1, "octo");
        using var lateKey = P256KeyPair.Generate();
        // Connected, but with no hub yet: the window between ConnectAsync and attaching one.
        await using var late = await RelayClient.ConnectAsync(relay.Url, token, lateKey, "late-host", "host");
        var caller = await relay.ConnectAsync(token, "early-caller", "app");
        await caller.Relay.WaitForPeerAsync(p => p.Id == late.DeviceId, TimeSpan.FromSeconds(10));

        var open = caller.Hub.OpenAsync(late.DeviceId, TimeSpan.FromSeconds(10));
        await Wait.UntilAsync(() => late.QueuedFrames > 0, "the first handshake message to be queued");
        _ = new ChannelHub(late, lateKey, "late-host", "host") { Handler = (_, op, data) => TestRelay.Demo(op, data) };
        var ch = await open;
        var reply = await ch.RequestAsync("ping");

        Assert.True(reply.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task A_relay_that_sends_an_oversized_frame_ends_the_connection_cleanly_so_the_device_reconnects()
    {
        // A hostile relay: admits the device without checking anything, then sends one frame
        // over the size limit.
        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Logging.ClearProviders();
        await using var hostile = b.Build();
        hostile.UseWebSockets();
        hostile.Map("/v1/ws", async (HttpContext ctx) =>
        {
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var hello = JsonDocument.Parse((await Wire.ReceiveAsync(ws, Wire.MaxControl, default))!.Value.Data).RootElement;
            using var eph = P256KeyPair.Generate();
            await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { t = "challenge", eph = Wire.Hex(eph.Public), nonce = Wire.Hex(new byte[32]), account = "1" }), WebSocketMessageType.Text, true, default);
            await Wire.ReceiveAsync(ws, Wire.MaxControl, default);
            var device = Wire.DeviceId(Wire.Unhex(hello.GetProperty("pub").GetString()!));
            await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { t = "ready", device, account = "1", login = "x" }), WebSocketMessageType.Text, true, default);
            try
            {
                await ws.SendAsync(new byte[Wire.MaxFrame + 1], WebSocketMessageType.Binary, true, default);
                await Wire.ReceiveAsync(ws, Wire.MaxFrame, default);
            }
            catch (WebSocketException) { }
        });
        await hostile.StartAsync();
        await using var victim = await RelayClient.ConnectAsync(new Uri(hostile.Urls.First()), "t", P256KeyPair.Generate(), "victim", "host");

        // Ends normally: a fault here would have taken the host process down.
        await victim.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(victim.Completion.IsCompletedSuccessfully);
    }
}
