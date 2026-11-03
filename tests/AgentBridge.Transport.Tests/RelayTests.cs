using System.Net.WebSockets;
using System.Text.Json;
using AgentBridge.Relay;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentBridge.Transport.Tests;

// The relay's admission, routing and refusal rules, as the S4 dev stack checked them.
public class RelayTests
{
    static readonly GitHubUser Octo = new(1, "octo");
    static readonly GitHubUser Mallory = new(2, "mallory");

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"device_code\":\"code\",\"interval\":false}")]
    [Trait("Category", "Network")]
    public async Task Malformed_upstream_device_responses_return_bad_gateway(string payload)
    {
        await using var relay = await TestRelay.StartAsync();
        relay.GitHub.DeviceResponseOverride = payload;
        using var response = await relay.Http.PostAsync(new Uri(relay.Url, "/v1/auth/device"), null);
        Assert.Equal(System.Net.HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Upstream_token_is_revoked_even_when_account_lookup_fails()
    {
        await using var relay = await TestRelay.StartAsync();
        relay.GitHub.UserStatusOverride = 503;
        await Assert.ThrowsAnyAsync<Exception>(() => relay.SignInAsync(Octo));
        Assert.Single(relay.GitHub.Issued);
        Assert.Equal(relay.GitHub.Issued.Single(), Assert.Single(relay.GitHub.Revoked));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Sign_in_issues_a_relay_token_and_the_GitHub_token_is_revoked_not_returned()
    {
        await using var relay = await TestRelay.StartAsync();

        var account = await relay.SignInAsync(Octo);

        Assert.Equal("octo", account.Login);
        Assert.Equal("1", account.AccountId);
        Assert.DoesNotContain(account.Token, relay.GitHub.Issued);
        Assert.Equal(relay.GitHub.Issued.OrderBy(t => t), relay.GitHub.Revoked.OrderBy(t => t));
        await using var device = await relay.ConnectAsync(account.Token, "signed-in");
        Assert.Equal("1", device.Relay.AccountId);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_sign_in_denied_at_GitHub_fails()
    {
        await using var relay = await TestRelay.StartAsync();

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            SignIn.DeviceFlowAsync(relay.Http, relay.Url, (code, _) => relay.GitHub.Deny(code)));

        Assert.Contains("access_denied", e.Message);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Two_devices_of_one_account_see_each_other()
    {
        await using var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(1, "octo");
        var host = await relay.ConnectAsync(token, "host-a", "host");
        var app = await relay.ConnectAsync(token, "app-b", "app");

        var seen = await app.Relay.WaitForPeerAsync(p => p.Id == host.Id, TimeSpan.FromSeconds(10));

        Assert.Equal(("host-a", "host"), (seen.Name, seen.Kind));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Another_accounts_device_is_neither_listed_nor_reachable()
    {
        await using var relay = await TestRelay.StartAsync();
        var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "host-a", "host");
        var other = await relay.ConnectAsync(relay.TokenFor(2, "mallory"), "mallory-host", "host");
        var undeliverable = new TaskCompletionSource<string>();
        other.Relay.Undeliverable += to => undeliverable.TrySetResult(to);

        var open = other.Hub.OpenAsync(host.Id, TimeSpan.FromSeconds(1));
        var refused = await undeliverable.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(host.Id, refused);
        // The relay's undeliverable notice ends the attempt at once, not at its timeout.
        var e = await Assert.ThrowsAsync<IOException>(() => open);
        Assert.Equal("peer is not connected", e.Message);
        Assert.DoesNotContain(other.Relay.Presence, p => p.Id == host.Id);
        Assert.DoesNotContain(host.Relay.Presence, p => p.Id == other.Id);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_bad_token_is_refused_with_4001()
    {
        await using var relay = await TestRelay.StartAsync();

        var e = await Assert.ThrowsAsync<RelayRefusedException>(() =>
            RelayClient.ConnectAsync(relay.Url, relay.TokenFor(1, "octo") + "x", P256KeyPair.Generate(), "x", "test"));

        Assert.Equal(CloseCodes.AuthFailed, e.Code);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_device_that_cannot_prove_it_holds_the_key_it_claims_is_refused_with_4003()
    {
        await using var relay = await TestRelay.StartAsync();
        using var claimed = P256KeyPair.Generate();
        using var actual = P256KeyPair.Generate();

        var (code, _) = await RawHelloAsync(relay, relay.TokenFor(1, "octo"), claimed.Public, actual);

        Assert.Equal(CloseCodes.ProofFailed, code);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_key_that_is_not_on_the_curve_is_refused_at_hello()
    {
        await using var relay = await TestRelay.StartAsync();
        using var k = P256KeyPair.Generate();
        var bad = (byte[])k.Public.Clone();
        bad[64] ^= 1;

        var (code, reason) = await RawHelloAsync(relay, relay.TokenFor(1, "octo"), bad, k);

        Assert.Equal((CloseCodes.Protocol, "bad-key"), (code, reason));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_frame_over_the_size_limit_closes_the_connection_with_4013()
    {
        await using var relay = await TestRelay.StartAsync();
        var c = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "big");

        await c.Relay.SendRawAsync(new byte[Wire.MaxFrame + 1], WebSocketMessageType.Binary);
        await c.Relay.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(CloseCodes.TooLarge, (int?)c.Relay.CloseStatus);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_socket_that_aborts_itself_ends_the_connection_without_faulting_it_or_throwing_on_dispose()
    {
        await using var relay = await TestRelay.StartAsync();
        var c = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "aborted");
        // Aborted before the loop's first receive, the loop never runs and the test proves nothing.
        await c.Relay.Receiving.WaitAsync(TimeSpan.FromSeconds(10));

        c.Relay.AbortSocket();
        await c.Relay.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(TaskStatus.RanToCompletion, c.Relay.Completion.Status);
        await c.Relay.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Flooding_frames_closes_the_connection_with_4008()
    {
        await using var relay = await TestRelay.StartAsync(o => o.MaxFramesPerSecond = 20);
        var c = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "flood");
        var frame = Wire.DataFrameBytes(new string('0', 32), new byte[16]);

        for (int i = 0; i < 300 && c.Relay.CloseStatus is null; i++)
        {
            try { await c.Relay.SendRawAsync(frame, WebSocketMessageType.Binary); }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { break; }
        }
        await c.Relay.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(CloseCodes.RateLimited, (int?)c.Relay.CloseStatus);
    }

    // The relay counts frames as RelayHub.WithinRate does: in fixed one-second windows of when
    // it reads them, each starting at the first frame read after the last one ended. A frame is
    // read in order, at most half a second after it went (a read loop held up on a busy
    // runner). Paced to a second of the sender's own, a burst read late shared a window with
    // the next burst, read on time, and was cut off with 4008 (EventTests on CI).
    [Theory]
    [InlineData(150, 200)]
    [InlineData(40, 50)]
    public void Paced_frames_fit_every_one_of_the_relays_windows_even_when_it_reads_some_of_them_late(int paced, int relayLimit)
    {
        var random = new Random(20261030);
        for (var trial = 0; trial < 200; trial++)
        {
            var sent = new Queue<long>();
            long now = 0, read = 0, windowStart = -1000;
            var inWindow = 0;
            for (var frame = 0; frame < paced * 6; frame++)
            {
                while (RelayClient.Pace(sent, now, paced) is { } wait) { now += (long)wait.TotalMilliseconds; }
                read = Math.Max(read, now + random.Next(500));
                if (read - windowStart >= 1000) { (windowStart, inWindow) = (read, 0); }
                Assert.True(++inWindow <= relayLimit, $"trial {trial}: {inWindow} frames read in the relay's second from {windowStart} ms");
            }
        }
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_second_connection_with_the_same_device_key_replaces_the_first_with_4009()
    {
        await using var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(1, "octo");
        var host = await relay.ConnectAsync(token, "host-a", "host");
        var watcher = await relay.ConnectAsync(token, "watcher");

        var again = await relay.ConnectAsync(token, "host-a again", "host", host.Key);
        await host.Relay.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(CloseCodes.Replaced, (int?)host.Relay.CloseStatus);
        await Wait.UntilAsync(() => watcher.Relay.Presence.Any(p => p.Id == host.Id && p.Name == "host-a again"), "the replacement in presence");
        Assert.Single(watcher.Relay.Presence, p => p.Id == host.Id);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_device_that_leaves_drops_out_of_presence()
    {
        await using var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(1, "octo");
        var leaving = await relay.ConnectAsync(token, "leaving");
        var watcher = await relay.ConnectAsync(token, "watcher");
        await watcher.Relay.WaitForPeerAsync(p => p.Id == leaving.Id, TimeSpan.FromSeconds(10));

        await leaving.DisposeAsync();

        await Wait.UntilAsync(() => watcher.Relay.Presence.All(p => p.Id != leaving.Id), "the device to leave presence");
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task After_many_devices_come_and_go_at_once_every_device_hears_the_account_as_it_now_is()
    {
        // Each change is announced with the account as it was then. Announcements once raced each
        // other to a device, so an older one could land last and stay: a machine forgotten a moment
        // earlier listed again (#151), or one gone listed as here.
        await using var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(1, "octo");
        var watcher = await relay.ConnectAsync(token, "watcher");
        var hub = relay.App.Services.GetRequiredService<RelayHub>();
        for (var round = 0; round < 5; round++)
        {
            var came = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => relay.ConnectAsync(token, $"d{i}")));
            await Wait.UntilAsync(() => came.All(d => watcher.Relay.Presence.Any(p => p.Id == d.Id)), "every device that came in presence");

            await Task.WhenAll(came.Select(d => d.DisposeAsync().AsTask()));
            await Wait.UntilAsync(() => hub.Online == 1, "the relay to see them go");
            await Wait.UntilAsync(() => watcher.Relay.Presence.Count == 1, "presence to be the watcher alone");
        }
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_socket_from_a_web_origin_that_is_not_allowed_is_refused_and_an_allowed_one_opens()
    {
        await using var relay = await TestRelay.StartAsync();
        async Task<bool> Opens(string origin)
        {
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Origin", origin);
            try { await ws.ConnectAsync(RelayClient.SocketUri(relay.Url), default); return true; }
            catch (WebSocketException) { return false; }
        }

        Assert.False(await Opens("https://evil.example"));
        Assert.True(await Opens("https://pwa.example"));
        // The relay's own origin, for a dev stack that serves the page itself.
        Assert.True(await Opens(relay.Url.GetLeftPart(UriPartial.Authority)));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task The_sign_in_API_answers_CORS_preflight_for_allowed_origins_only()
    {
        await using var relay = await TestRelay.StartAsync();
        async Task<string?> Preflight(string origin)
        {
            using var req = new HttpRequestMessage(HttpMethod.Options, new Uri(relay.Url, "/v1/auth/token"));
            req.Headers.Add("Origin", origin);
            req.Headers.Add("Access-Control-Request-Method", "POST");
            req.Headers.Add("Access-Control-Request-Headers", "content-type");
            using var r = await relay.Http.SendAsync(req);
            return r.Headers.TryGetValues("Access-Control-Allow-Origin", out var v) ? v.First() : null;
        }

        Assert.Equal("https://pwa.example", await Preflight("https://pwa.example"));
        Assert.Null(await Preflight("https://evil.example"));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task The_relay_serves_no_app_code_unless_asked_to()
    {
        await using var relay = await TestRelay.StartAsync();

        var root = await relay.Http.GetStringAsync(relay.Url);

        Assert.Contains("serves no app code", root);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_relay_that_is_stopping_tells_connected_devices_to_reconnect_at_once_with_1012()
    {
        await using var relay = await TestRelay.StartAsync();
        var device = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "last");

        await relay.App.StopAsync();
        await device.Relay.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(CloseCodes.Restarting, (int?)device.Relay.CloseStatus);
        Assert.Equal("restarting", device.Relay.CloseReason);
    }

    // #603: hello, challenge, proof, ready was two round trips after the socket opened. A device
    // that asks has the challenge as the socket opens, sends hello and proof together, and is
    // ready one round trip later. Each run of messages one way is a flight; two make a round trip.
    [Theory]
    [Trait("Category", "Network")]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    public async Task A_device_that_asks_for_the_challenge_with_the_opening_is_admitted_in_one_round_trip_after_it(bool early, int roundTrips)
    {
        await using var relay = await TestRelay.StartAsync();
        using var key = P256KeyPair.Generate();
        var flights = new List<string>();
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(early ? new UriBuilder(RelayClient.SocketUri(relay.Url)) { Query = "admit=1" }.Uri : RelayClient.SocketUri(relay.Url), default);

        async Task<JsonElement> ReceiveAsync()
        {
            var m = JsonDocument.Parse((await Wire.ReceiveAsync(ws, Wire.MaxControl, default))!.Value.Data).RootElement;
            flights.Add("relay");
            return m;
        }
        async Task SendAsync(object m)
        {
            await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(m), WebSocketMessageType.Text, true, default);
            flights.Add("device");
        }

        JsonElement challenge;
        if (early)
        {
            // Here before the device has said anything.
            challenge = await ReceiveAsync();
            flights.Clear();
            await SendAsync(new { t = "hello", token = relay.TokenFor(1, "octo"), pub = Wire.Hex(key.Public), name = "raw", kind = "test" });
        }
        else
        {
            await SendAsync(new { t = "hello", token = relay.TokenFor(1, "octo"), pub = Wire.Hex(key.Public), name = "raw", kind = "test" });
            challenge = await ReceiveAsync();
        }
        var secret = key.Dh(Wire.Unhex(challenge.GetProperty("eph").GetString()!));
        var nonce = Wire.Unhex(challenge.GetProperty("nonce").GetString()!);
        await SendAsync(new { t = "proof", mac = Wire.Hex(early ? Wire.EarlyProofMac(secret, nonce) : Wire.ProofMac(secret, nonce, challenge.GetProperty("account").GetString()!)) });
        JsonElement ready;
        do { ready = await ReceiveAsync(); } while (ready.GetProperty("t").GetString() != "ready");

        Assert.Equal(early, challenge.TryGetProperty("early", out var e) && e.GetBoolean());
        // Nothing about the account goes to a socket that has not shown a token.
        Assert.Equal(!early, challenge.TryGetProperty("account", out _));
        Assert.Equal(Wire.DeviceId(key.Public), ready.GetProperty("device").GetString());
        Assert.Equal("1", ready.GetProperty("account").GetString());
        var runs = flights.Where((f, i) => i == 0 || flights[i - 1] != f).Count();
        Assert.Equal(roundTrips, (runs + 1) / 2);
    }

    // Each proof names what it answers: an early challenge's is not taken for an ordinary one's, or
    // the other way, so neither can be passed off as the other.
    [Theory]
    [Trait("Category", "Network")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_proof_of_the_other_kind_is_refused(bool early)
    {
        await using var relay = await TestRelay.StartAsync();
        using var key = P256KeyPair.Generate();
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(early ? new UriBuilder(RelayClient.SocketUri(relay.Url)) { Query = "admit=1" }.Uri : RelayClient.SocketUri(relay.Url), default);
        await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { t = "hello", token = relay.TokenFor(1, "octo"), pub = Wire.Hex(key.Public), name = "raw", kind = "test" }), WebSocketMessageType.Text, true, default);
        var c = JsonDocument.Parse((await Wire.ReceiveAsync(ws, Wire.MaxControl, default))!.Value.Data).RootElement;
        var secret = key.Dh(Wire.Unhex(c.GetProperty("eph").GetString()!));
        var nonce = Wire.Unhex(c.GetProperty("nonce").GetString()!);

        var wrong = early ? Wire.ProofMac(secret, nonce, "1") : Wire.EarlyProofMac(secret, nonce);
        await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { t = "proof", mac = Wire.Hex(wrong) }), WebSocketMessageType.Text, true, default);
        while (await Wire.ReceiveAsync(ws, Wire.MaxControl, default) is not null) { }

        Assert.Equal(CloseCodes.ProofFailed, (int?)ws.CloseStatus);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_device_still_connects_to_a_relay_that_sends_the_challenge_after_the_hello()
    {
        await using var relay = await TestRelay.StartAsync(o => o.EarlyChallenge = false);

        await using var device = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "older relay");

        Assert.Equal("1", device.Relay.AccountId);
    }

    // Speaks the hello and proof by hand, so a test can claim one key and prove another.
    static async Task<(int? Code, string? Reason)> RawHelloAsync(TestRelay relay, string token, byte[] claimedPub, P256KeyPair prover)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(RelayClient.SocketUri(relay.Url), default);
        await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { t = "hello", token, pub = Wire.Hex(claimedPub), name = "raw", kind = "test" }), WebSocketMessageType.Text, true, default);
        var frame = await Wire.ReceiveAsync(ws, Wire.MaxControl, default);
        if (frame is { } f)
        {
            var c = JsonDocument.Parse(f.Data).RootElement;
            var mac = Wire.ProofMac(prover.Dh(Wire.Unhex(c.GetProperty("eph").GetString()!)), Wire.Unhex(c.GetProperty("nonce").GetString()!), c.GetProperty("account").GetString()!);
            await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { t = "proof", mac = Wire.Hex(mac) }), WebSocketMessageType.Text, true, default);
            await Wire.ReceiveAsync(ws, Wire.MaxControl, default);
        }
        return ((int?)ws.CloseStatus, ws.CloseStatusDescription);
    }
}
