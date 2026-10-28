// Spike S4 dev stack.
//   serve --port N   a fake GitHub, the relay on 127.0.0.1:N and one demo host, for the
//                    PWA tests and for trying the PWA locally
//   check            the relay's security and routing checks, offline; nonzero on failure
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using S2.Noise;
using S4;
using S4.DevStack;
using S4.Relay;

var mode = args.Length > 0 ? args[0] : "";
const string Marker = "S4-PLAINTEXT-MARKER";
var markerBytes = Encoding.UTF8.GetBytes(Marker);

if (mode == "serve")
{
    var port = int.Parse(Arg("--port") ?? "8780");
    var fake = new FakeGitHub { AutoApprove = new GitHubUser(1, "octo") };
    await using var gh = await fake.StartAsync();
    var relay = RelayApp.Build([], o =>
    {
        o.GitHubBaseUrl = o.GitHubApiUrl = fake.Url;
        o.GitHubClientId = "fake-client";
        o.GitHubClientSecret = "fake-secret";
        o.Tap = (from, to, payload) =>
        {
            if (payload.AsSpan().IndexOf(markerBytes) >= 0) { Console.WriteLine($"LEAK: plaintext in a frame from {from[..8]} to {to[..8]}"); }
        };
    }, b =>
    {
        b.WebHost.UseUrls($"http://127.0.0.1:{port}");
        b.Logging.AddFilter("Microsoft", LogLevel.Warning);
    });
    await relay.StartAsync();
    var url = new Uri(relay.Urls.First());

    using var http = new HttpClient();
    using var key = P256KeyPair.Generate();
    var account = await SignIn.DeviceFlowAsync(http, url, (_, _) => { });
    await using var host = await RelayClient.ConnectAsync(url, account.Token, key, "demo-host", "host");
    var questions = new QuestionService(host, "demo-host", Console.WriteLine);
    var hub = new ChannelHub(host, key, "demo-host", "host")
    {
        Handler = async (ch, op, data) => await questions.HandleAsync(ch, op, data) ?? await DemoService.HandleAsync("demo-host", op, data),
    };
    hub.Accepted += ch => Console.WriteLine($"demo-host: channel from {ch.PeerName} ({ch.PeerKind})");
    Console.WriteLine($"ready {url} demo-host {host.DeviceId}");
    await relay.WaitForShutdownAsync();
    return 0;
}

if (mode == "check")
{
    int failed = 0;
    void Expect(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? $"  ({detail})" : "")}");
        if (!ok) { failed++; }
    }
    async Task Check(string name, Func<Task<(bool, string)>> body)
    {
        try { var (ok, detail) = await body().WaitAsync(TimeSpan.FromSeconds(30)); Expect(name, ok, detail); }
        catch (Exception e) { Expect(name, false, $"{e.GetType().Name}: {e.Message}"); }
    }

    var fake = new FakeGitHub();
    await using var gh = await fake.StartAsync();
    var pushService = new FakePushService();
    await using var pushApp = await pushService.StartAsync();
    using var vapidKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
    var taps = new ConcurrentQueue<byte[]>();
    var logs = new ConcurrentQueue<string>();
    Func<string, string, (string, string)?>? route = null;
    var relay = RelayApp.Build([], o =>
    {
        o.GitHubBaseUrl = o.GitHubApiUrl = fake.Url;
        o.GitHubClientId = "fake-client";
        o.GitHubClientSecret = "fake-secret";
        o.MaxFramesPerSecond = 200;
        o.AllowedOrigins = ["https://pwa.example"];
        o.VapidPrivateKey = Convert.ToBase64String(vapidKey.ExportPkcs8PrivateKey());
        o.PushHosts = ["127.0.0.1", "web.push.apple.com"];
        o.AllowInsecurePush = true;
        o.MaxPushesPerMinute = 20;
        o.Tap = (_, _, payload) => taps.Enqueue(payload);
        o.Route = (f, t) => route?.Invoke(f, t) ?? (f, t);
    }, b =>
    {
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Logging.ClearProviders();
        b.Logging.AddProvider(new CaptureLogger(logs));
    });
    await relay.StartAsync();
    var url = new Uri(relay.Urls.First());
    using var http = new HttpClient();

    var octo = new GitHubUser(1, "octo");
    var mallory = new GitHubUser(2, "mallory");
    Task<SignInResult> SignInAs(GitHubUser u) => SignIn.DeviceFlowAsync(http, url, (code, _) => fake.Approve(code, u));
    async Task<(P256KeyPair Key, RelayClient Relay, ChannelHub Hub)> Connect(SignInResult a, string name, string kind = "test", P256KeyPair? key = null)
    {
        key ??= P256KeyPair.Generate();
        var r = await RelayClient.ConnectAsync(url, a.Token, key, name, kind);
        var hub = new ChannelHub(r, key, name, kind) { Handler = (ch, op, data) => DemoService.HandleAsync(name, op, data) };
        return (key, r, hub);
    }

    SignInResult octoAccount = null!, malloryAccount = null!;
    await Check("sign-in issues a relay token, and the GitHub token is revoked, not returned", async () =>
    {
        var revokedBefore = fake.Revoked.Count;
        octoAccount = await SignInAs(octo);
        malloryAccount = await SignInAs(mallory);
        return (!octoAccount.Token.StartsWith("gho_") && octoAccount.Login == "octo" && fake.Revoked.Count == revokedBefore + 2, $"login {octoAccount.Login}, revoked {fake.Revoked.Count - revokedBefore}");
    });
    await Check("a sign-in denied at GitHub fails", async () =>
    {
        try { await SignIn.DeviceFlowAsync(http, url, (code, _) => fake.Deny(code)); return (false, "signed in"); }
        catch (InvalidOperationException e) { return (e.Message.Contains("access_denied"), e.Message); }
    });

    var host = await Connect(octoAccount, "host-a", "host");
    var app = await Connect(octoAccount, "app-b", "app");
    var accepted = new TaskCompletionSource<SecureChannel>();
    host.Hub.Accepted += ch => accepted.TrySetResult(ch);

    await Check("two devices of one account see each other", async () =>
    {
        var seen = await app.Relay.WaitForPeerAsync(p => p.Id == host.Relay.DeviceId, TimeSpan.FromSeconds(5));
        return (seen.Name == "host-a" && seen.Kind == "host", $"{seen.Name} {seen.Id[..8]}");
    });

    SecureChannel ch = null!;
    await Check("a channel authenticates both ends: each key hashes to the id it was reached at", async () =>
    {
        ch = await app.Hub.OpenAsync(host.Relay.DeviceId);
        var other = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var ok = Wire.DeviceId(ch.PeerStatic!) == host.Relay.DeviceId && Wire.DeviceId(other.PeerStatic!) == app.Relay.DeviceId
            && ch.PeerName == "host-a" && other.PeerName == "app-b" && ch.HandshakeHash.AsSpan().SequenceEqual(other.HandshakeHash);
        return (ok, $"handshake hash {Wire.Hex(ch.HandshakeHash)[..12]}");
    });

    await Check("a request crosses the relay end to end, and the relay sees only ciphertext", async () =>
    {
        taps.Clear();
        var text = $"{Marker} café – 世界 ✓";
        var reply = await ch.RequestAsync("echo", new { text });
        var echoed = reply.GetProperty("result").GetProperty("text").GetString();
        var leakedInFrames = taps.Any(p => p.AsSpan().IndexOf(markerBytes) >= 0);
        var leakedInLogs = logs.Any(l => l.Contains(Marker));
        return (echoed == text && taps.Count >= 2 && !leakedInFrames && !leakedInLogs, $"{taps.Count} frames tapped, marker in frames {leakedInFrames}, in logs {leakedInLogs}");
    });

    await Check("a large message (60,000 bytes) crosses the relay", async () =>
    {
        var big = new string('x', 60_000);
        var reply = await ch.RequestAsync("echo", new { big });
        return (reply.GetProperty("result").GetProperty("big").GetString() == big, "");
    });

    var other = await Connect(malloryAccount, "mallory-host", "host");
    await Check("another account's device is neither listed nor reachable", async () =>
    {
        var undeliverable = new TaskCompletionSource<string>();
        other.Relay.Undeliverable += to => undeliverable.TrySetResult(to);
        var listed = other.Relay.Presence.Any(p => p.Id == host.Relay.DeviceId) || app.Relay.Presence.Any(p => p.Id == other.Relay.DeviceId);
        var openTask = other.Hub.OpenAsync(host.Relay.DeviceId, TimeSpan.FromSeconds(2));
        var refused = await undeliverable.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var opened = false;
        try { await openTask; opened = true; } catch (TimeoutException) { }
        return (!listed && refused == host.Relay.DeviceId && !opened, $"listed {listed}, opened {opened}");
    });

    await Check("a relay that redirects a channel to another device is detected", async () =>
    {
        // Plays a hostile relay: frames from the app to host-a go to host-d instead, and
        // host-d's answers are relabelled as coming from host-a.
        var d = await Connect(octoAccount, "host-d", "host");
        string a = host.Relay.DeviceId, b = app.Relay.DeviceId, dd = d.Relay.DeviceId;
        route = (f, t) => f == b && t == a ? (b, dd) : f == dd && t == b ? (a, b) : null;
        try
        {
            await app.Hub.OpenAsync(a, TimeSpan.FromSeconds(5));
            return (false, "the channel opened");
        }
        // The prologue binds both routing ids, so the handshake fails authentication; a
        // relay that also forged the prologue would meet the key-to-id check instead.
        catch (System.Security.Cryptography.CryptographicException e) { return (true, $"{e.GetType().Name}: {e.Message}"); }
        finally { route = null; await d.Relay.DisposeAsync(); }
    });

    await Check("a bad token is refused (4001)", async () =>
    {
        try { await RelayClient.ConnectAsync(url, octoAccount.Token + "x", P256KeyPair.Generate(), "x", "test"); return (false, "connected"); }
        catch (RelayRefusedException e) { return (e.Code == CloseCodes.AuthFailed, e.Message); }
    });

    await Check("a device that cannot prove it holds the key it claims is refused (4003)", async () =>
    {
        using var claimed = P256KeyPair.Generate();
        using var actual = P256KeyPair.Generate();
        var (code, _) = await RawHello(octoAccount.Token, claimed.Public, actual);
        return (code == CloseCodes.ProofFailed, $"close {code}");
    });

    await Check("a key that is not on the curve is refused at hello", async () =>
    {
        using var k = P256KeyPair.Generate();
        var bad = (byte[])k.Public.Clone();
        bad[64] ^= 1;
        var (code, reason) = await RawHello(octoAccount.Token, bad, k);
        return (code == CloseCodes.Protocol && reason == "bad-key", $"close {code} {reason}");
    });

    await Check("a frame over the size limit closes the connection (4013)", async () =>
    {
        var c = await Connect(octoAccount, "big", "test");
        await c.Relay.SendRawAsync(new byte[Wire.MaxFrame + 1], WebSocketMessageType.Binary);
        await c.Relay.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        return ((int?)c.Relay.CloseStatus == CloseCodes.TooLarge, $"close {(int?)c.Relay.CloseStatus}");
    });

    await Check("flooding frames closes the connection (4008)", async () =>
    {
        var c = await Connect(octoAccount, "flood", "test");
        var frame = Wire.DataFrameBytes(new string('0', 32), new byte[16]);
        for (int i = 0; i < 300 && c.Relay.CloseStatus is null; i++)
        {
            try { await c.Relay.SendRawAsync(frame, WebSocketMessageType.Binary); }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException) { break; }
        }
        await c.Relay.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        return ((int?)c.Relay.CloseStatus == CloseCodes.RateLimited, $"close {(int?)c.Relay.CloseStatus}");
    });

    await Check("a second connection with the same device key replaces the first (4009)", async () =>
    {
        var again = await Connect(octoAccount, "host-a", "host", host.Key);
        await host.Relay.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var stillListed = app.Relay.Presence.Count(p => p.Id == host.Relay.DeviceId);
        await again.Relay.DisposeAsync();
        return ((int?)host.Relay.CloseStatus == CloseCodes.Replaced && stillListed == 1, $"close {(int?)host.Relay.CloseStatus}, listed {stillListed}x");
    });

    await Check("a device that leaves drops out of presence", async () =>
    {
        var watcher = await Connect(octoAccount, "watcher", "test");
        var gone = app.Relay.DeviceId;
        var listedBefore = watcher.Relay.Presence.Any(p => p.Id == gone);
        await app.Relay.DisposeAsync();
        var sw = Stopwatch.StartNew();
        while (watcher.Relay.Presence.Any(p => p.Id == gone) && sw.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(50); }
        var listedAfter = watcher.Relay.Presence.Any(p => p.Id == gone);
        await watcher.Relay.DisposeAsync();
        return (listedBefore && !listedAfter, $"before {listedBefore}, after {listedAfter}, {sw.ElapsedMilliseconds} ms");
    });

    await Check("a socket from a web origin that is not allowed is refused; an allowed one opens", async () =>
    {
        async Task<string> Try(string origin)
        {
            using var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Origin", origin);
            try { await ws.ConnectAsync(RelayClient.SocketUri(url), default); return "open"; }
            catch (WebSocketException) { return "refused"; }
        }
        var evil = await Try("https://evil.example");
        var good = await Try("https://pwa.example");
        return (evil == "refused" && good == "open", $"evil {evil}, allowed {good}");
    });

    await Check("the sign-in API answers CORS preflight for allowed origins only", async () =>
    {
        async Task<string?> Preflight(string origin)
        {
            using var req = new HttpRequestMessage(HttpMethod.Options, new Uri(url, "/v1/auth/token"));
            req.Headers.Add("Origin", origin);
            req.Headers.Add("Access-Control-Request-Method", "POST");
            req.Headers.Add("Access-Control-Request-Headers", "content-type");
            using var r = await http.SendAsync(req);
            return r.Headers.TryGetValues("Access-Control-Allow-Origin", out var v) ? v.First() : null;
        }
        var good = await Preflight("https://pwa.example");
        var evil = await Preflight("https://evil.example");
        return (good == "https://pwa.example" && evil is null, $"allowed {good ?? "none"}, evil {evil ?? "none"}");
    });

    await Check("a host bounds the channels it keeps: a full table refuses more, unfinished handshakes expire, and a departed peer's channels go", async () =>
    {
        var bounded = await Connect(octoAccount, "bounded-host", "host");
        bounded.Hub.MaxChannels = 4;
        bounded.Hub.HandshakeTimeout = TimeSpan.FromSeconds(1);
        var attacker = await Connect(octoAccount, "half-open", "test");
        await attacker.Relay.WaitForPeerAsync(p => p.Id == bounded.Relay.DeviceId, TimeSpan.FromSeconds(5));
        async Task SendMsg1()
        {
            // A valid first handshake message on a fresh channel, never followed up.
            var hs = new XXHandshake(true, attacker.Key, []);
            byte[] payload = [(byte)ChannelKind.Msg1, .. System.Security.Cryptography.RandomNumberGenerator.GetBytes(8), .. hs.WriteMessage([])];
            await attacker.Relay.SendAsync(bounded.Relay.DeviceId, payload);
        }
        for (int i = 0; i < 10; i++) { await SendMsg1(); }
        await Task.Delay(500);
        var whenFull = bounded.Hub.ChannelCount;
        await Task.Delay(1200);
        await SendMsg1();
        await Task.Delay(300);
        var afterExpiry = bounded.Hub.ChannelCount;
        var peer = await Connect(octoAccount, "short-lived", "app");
        await peer.Hub.OpenAsync(bounded.Relay.DeviceId);
        var withPeer = bounded.Hub.ChannelCount;
        await Task.Delay(5500);
        await peer.Relay.DisposeAsync();
        await attacker.Relay.DisposeAsync();
        var sw = Stopwatch.StartNew();
        while (bounded.Hub.ChannelCount > 0 && sw.Elapsed < TimeSpan.FromSeconds(5)) { await Task.Delay(50); }
        var afterLeave = bounded.Hub.ChannelCount;
        await bounded.Relay.DisposeAsync();
        return (whenFull == 4 && afterExpiry == 1 && withPeer == 2 && afterLeave == 0, $"full {whenFull}, after expiry {afterExpiry}, with peer {withPeer}, after both left {afterLeave}");
    });

    await Check("a channel opened while the target is still attaching its handler is not lost", async () =>
    {
        using var lateKey = P256KeyPair.Generate();
        // Connected, but with no hub yet: the window between ConnectAsync and attaching one.
        await using var late = await RelayClient.ConnectAsync(url, octoAccount.Token, lateKey, "late-host", "host");
        var caller = await Connect(octoAccount, "early-caller", "app");
        await caller.Relay.WaitForPeerAsync(p => p.Id == late.DeviceId, TimeSpan.FromSeconds(5));
        var open = caller.Hub.OpenAsync(late.DeviceId, TimeSpan.FromSeconds(5));
        await Task.Delay(500);
        _ = new ChannelHub(late, lateKey, "late-host", "host") { Handler = (ch, op, data) => DemoService.HandleAsync("late-host", op, data) };
        var ch = await open;
        var reply = await ch.RequestAsync("ping");
        await caller.Relay.DisposeAsync();
        return (reply.GetProperty("ok").GetBoolean(), "Msg1 was held until the hub attached");
    });

    await Check("a relay that sends an oversized frame ends the connection cleanly, so the device reconnects", async () =>
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
        Exception? fault = null;
        try { await victim.Completion.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception e) { fault = e; }
        await hostile.StopAsync();
        return (fault is null, fault is null ? "Completion ended normally" : $"Completion faulted: {fault.GetType().Name}");
    });

    // ---- S5: Web Push ----

    await Check("RFC 8291 encryption reproduces the RFC's example byte for byte, and decrypts", () =>
    {
        var asPub = B64Url.Decode("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8");
        using var asKey = P256KeyPair.FromParameters(B64Url.Decode("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw"), asPub[1..33], asPub[33..65]);
        var uaPub = B64Url.Decode("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4");
        using var uaKey = P256KeyPair.FromParameters(B64Url.Decode("q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94"), uaPub[1..33], uaPub[33..65]);
        var auth = B64Url.Decode("BTBZMqHH6r4Tts7J_aSIgg");
        var plaintext = B64Url.Decode("V2hlbiBJIGdyb3cgdXAsIEkgd2FudCB0byBiZSBhIHdhdGVybWVsb24");
        byte[] expected = [.. B64Url.Decode("DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8"),
            .. B64Url.Decode("8pfeW0KbunFT06SuDKoJH9Ql87S1QUrdirN6GcG7sFz1y1sqLgVi1VhjVkHsUoEsbI_0LpXMuGvnzQ")];
        var body = WebPushCrypto.Encrypt(plaintext, uaPub, auth, asKey, B64Url.Decode("DGv6ra1nlYgDCS1FRnbzlw"));
        var back = WebPushCrypto.Decrypt(body, uaKey, auth);
        return Task.FromResult((body.AsSpan().SequenceEqual(expected) && back.AsSpan().SequenceEqual(plaintext), $"{body.Length} bytes, \"{Encoding.UTF8.GetString(back)}\""));
    });

    await Check("the relay's VAPID header verifies against its published key, for the endpoint's origin, and expires within the hour", async () =>
    {
        var published = JsonDocument.Parse(await http.GetStringAsync(new Uri(url, "/v1/push/key"))).RootElement.GetProperty("key").GetString()!;
        var header = Vapid.FromPkcs8(Convert.ToBase64String(vapidKey.ExportPkcs8PrivateKey()), "https://example.test").Header(new Uri("https://web.push.apple.com/QGuQ"), DateTimeOffset.UtcNow);
        var jwt = header.Split("t=")[1].Split(',')[0];
        var k = header.Split("k=")[1];
        var parts = jwt.Split('.');
        using var verify = System.Security.Cryptography.ECDsa.Create(new System.Security.Cryptography.ECParameters
        {
            Curve = System.Security.Cryptography.ECCurve.NamedCurves.nistP256,
            Q = new System.Security.Cryptography.ECPoint { X = B64Url.Decode(k)[1..33], Y = B64Url.Decode(k)[33..65] },
        });
        var ok = verify.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), B64Url.Decode(parts[2]), System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var claims = JsonDocument.Parse(B64Url.Decode(parts[1])).RootElement;
        var exp = DateTimeOffset.FromUnixTimeSeconds(claims.GetProperty("exp").GetInt64()) - DateTimeOffset.UtcNow;
        return (ok && k == published && claims.GetProperty("aud").GetString() == "https://web.push.apple.com" && exp > TimeSpan.Zero && exp <= TimeSpan.FromHours(1),
            $"aud {claims.GetProperty("aud").GetString()}, expires in {exp.TotalMinutes:F0} min");
    });

    await Check("a question reaches a phone as an encrypted push through the relay, and its answer comes back over the channel", async () =>
    {
        var hostKey = P256KeyPair.Generate();
        var hostRelay = await RelayClient.ConnectAsync(url, octoAccount.Token, hostKey, "push-host", "host");
        var questions = new QuestionService(hostRelay, "push-host");
        var answered = new TaskCompletionSource<QuestionService.Question>();
        questions.Answered += q => answered.TrySetResult(q);
        _ = new ChannelHub(hostRelay, hostKey, "push-host", "host") { Handler = (c, op, d) => questions.HandleAsync(c, op, d) };

        // The phone: a browser push key pair and auth secret, as PushManager.subscribe makes.
        using var ua = P256KeyPair.Generate();
        var auth = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var phone = await Connect(octoAccount, "phone", "pwa");
        await phone.Relay.WaitForPeerAsync(p => p.Id == hostRelay.DeviceId, TimeSpan.FromSeconds(5));
        var toHost = await phone.Hub.OpenAsync(hostRelay.DeviceId);
        var endpoint = $"{pushService.Url}/push/phone-1";
        var sub = await toHost.RequestAsync("push-subscribe", new { endpoint, p256dh = B64Url.Encode(ua.Public), auth = B64Url.Encode(auth), appUrl = "https://pwa.example/s4/" });
        pushService.Received.Clear();
        await toHost.RequestAsync("ask-me", new { delaySeconds = 0 });
        var sw = Stopwatch.StartNew();
        while (pushService.Received.IsEmpty && sw.Elapsed < TimeSpan.FromSeconds(10)) { await Task.Delay(50); }
        var push = pushService.Received.Single();

        var notice = JsonDocument.Parse(WebPushCrypto.Decrypt(push.Body, ua, auth)).RootElement.GetProperty("notification");
        var navigate = new Uri(notice.GetProperty("navigate").GetString()!);
        var qid = System.Web.HttpUtility.ParseQueryString(navigate.Query)["q"]!;
        var hostInUrl = System.Web.HttpUtility.ParseQueryString(navigate.Query)["host"];
        var question = await toHost.RequestAsync("question", new { q = qid });
        var text = question.GetProperty("result").GetProperty("text").GetString()!;
        var first = await toHost.RequestAsync("answer", new { q = qid, answer = "SQLite" });
        var second = await toHost.RequestAsync("answer", new { q = qid, answer = "DuckDB" });
        var got = await answered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var headersOk = push.Headers["Content-Encoding"] == "aes128gcm" && push.Headers["Urgency"] == "high" && push.Headers["TTL"] == "3600"
            && push.Headers["Authorization"].StartsWith("vapid t=") && push.Headers["Topic"] == PushTopic.From(Convert.FromHexString(qid)) && PushTopic.IsValid(push.Headers["Topic"]);
        var plaintextHidden = !Encoding.UTF8.GetString(push.Body).Contains("database") && !logs.Any(l => l.Contains(text) || l.Contains(endpoint));
        await phone.Relay.DisposeAsync();
        await hostRelay.DisposeAsync();
        return (sub.GetProperty("ok").GetBoolean() && headersOk && plaintextHidden && hostInUrl == hostRelay.DeviceId
            && first.GetProperty("result").GetProperty("accepted").GetBoolean() && !second.GetProperty("result").GetProperty("accepted").GetBoolean() && got.Answer == "SQLite",
            $"{push.Body.Length}-byte push, title \"{notice.GetProperty("title").GetString()}\", answer {got.Answer}, second answer refused, question and endpoint absent from body and relay logs {plaintextHidden}");
    });

    await Check("the relay refuses push endpoints off its allowlist without making a request", async () =>
    {
        var c = await Connect(octoAccount, "pusher", "host");
        pushService.Received.Clear();
        var results = new List<string>();
        foreach (var e in new[] { "http://169.254.169.254/latest/meta-data", "https://evil.example/push", "https://web.push.apple.com.evil.example/x", "ftp://web.push.apple.com/x", "not a url" })
        {
            var r = await c.Relay.PushAsync(e, new byte[100]);
            results.Add($"{r.Status}:{r.Error}");
        }
        var big = await c.Relay.PushAsync($"{pushService.Url}/push/x", new byte[WebPushCrypto.RecordSize + 1]);
        await c.Relay.DisposeAsync();
        var refusedAll = results.All(r => r.StartsWith("0:endpoint") || r.StartsWith("0:bad-endpoint"));
        return (refusedAll && big.Error == "bad-body-size" && pushService.Received.IsEmpty, string.Join(", ", results) + $", oversized {big.Error}");
    });

    await Check("a push topic Apple would refuse (a length base64 cannot have) is dropped, and the push still goes", async () =>
    {
        var c = await Connect(octoAccount, "topic-pusher", "host");
        pushService.Received.Clear();
        var bad = await c.Relay.PushAsync($"{pushService.Url}/push/topic", new byte[64], topic: "q7fe69a91e0b1");
        var good = await c.Relay.PushAsync($"{pushService.Url}/push/topic", new byte[64], topic: "q7fe69a91e0b");
        await c.Relay.DisposeAsync();
        var sent = pushService.Received.ToArray();
        var badHasTopic = sent.Length > 0 && sent[0].Headers.ContainsKey("Topic");
        var goodTopic = sent.Length > 1 && sent[1].Headers.TryGetValue("Topic", out var gt) ? gt : null;
        return (bad.Status == 201 && good.Status == 201 && sent.Length == 2 && !badHasTopic && goodTopic == "q7fe69a91e0b",
            $"13-character topic sent {badHasTopic}, 12-character topic {goodTopic}");
    });

    await Check("pushes are rate-limited per account, and a gone subscription (410) is dropped", async () =>
    {
        var c = await Connect(octoAccount, "flood-pusher", "host");
        var statuses = new List<string>();
        for (int i = 0; i < 25; i++) { var r = await c.Relay.PushAsync($"{pushService.Url}/push/ok", new byte[64]); statuses.Add(r.Error ?? r.Status.ToString()); }
        var limited = statuses.Count(s => s == "rate-limited");
        var acceptedFirst = statuses.TakeWhile(s => s == "201").Count() == statuses.Count - limited;

        // A host whose only subscriber has gone: one push, then it stops trying.
        var hostKey = P256KeyPair.Generate();
        var hostRelay = await RelayClient.ConnectAsync(url, malloryAccount.Token, hostKey, "gone-host", "host");
        var questions = new QuestionService(hostRelay, "gone-host");
        _ = new ChannelHub(hostRelay, hostKey, "gone-host", "host") { Handler = (ch2, op, d) => questions.HandleAsync(ch2, op, d) };
        using var ua = P256KeyPair.Generate();
        var phone = await Connect(malloryAccount, "old-phone", "pwa");
        await phone.Relay.WaitForPeerAsync(p => p.Id == hostRelay.DeviceId, TimeSpan.FromSeconds(5));
        var toHost = await phone.Hub.OpenAsync(hostRelay.DeviceId);
        await toHost.RequestAsync("push-subscribe", new { endpoint = $"{pushService.Url}/gone/1", p256dh = B64Url.Encode(ua.Public), auth = B64Url.Encode(new byte[16]), appUrl = "https://pwa.example/" });
        var before = questions.Subscribers;
        await questions.AskAsync("still there?", ["yes"]);
        var after = questions.Subscribers;
        await phone.Relay.DisposeAsync();
        await hostRelay.DisposeAsync();
        await c.Relay.DisposeAsync();
        return (limited > 0 && acceptedFirst && before == 1 && after == 0, $"{limited} of 25 rate-limited (account limit 20/min), subscribers {before} -> {after}");
    });

    await Check("a host's saved push subscriptions belong to one account: another account neither loads nor inherits them", async () =>
    {
        var store = Path.Combine(Path.GetTempPath(), $"s5-subs-{Guid.NewGuid():N}.json");
        var hostKey = P256KeyPair.Generate();
        var octoHost = await RelayClient.ConnectAsync(url, octoAccount.Token, hostKey, "store-host", "host");
        var questions = new QuestionService(octoHost, "store-host", null, store);
        _ = new ChannelHub(octoHost, hostKey, "store-host", "host") { Handler = (c, op, d) => questions.HandleAsync(c, op, d) };
        using var ua = P256KeyPair.Generate();
        var phone = await Connect(octoAccount, "store-phone", "pwa");
        await phone.Relay.WaitForPeerAsync(p => p.Id == octoHost.DeviceId, TimeSpan.FromSeconds(5));
        var toHost = await phone.Hub.OpenAsync(octoHost.DeviceId);
        await toHost.RequestAsync("push-subscribe", new { endpoint = $"{pushService.Url}/push/store-1", p256dh = B64Url.Encode(ua.Public), auth = B64Url.Encode(new byte[16]), appUrl = "https://pwa.example/" });
        await phone.Relay.DisposeAsync();
        await octoHost.DisposeAsync();

        // The same profile, restarted: once as the same account, once signed in as another.
        var sameKey = P256KeyPair.Generate();
        var same = await RelayClient.ConnectAsync(url, octoAccount.Token, sameKey, "store-host", "host");
        var reloadedSame = new QuestionService(same, "store-host", null, store).Subscribers;
        var otherKey = P256KeyPair.Generate();
        var other = await RelayClient.ConnectAsync(url, malloryAccount.Token, otherKey, "store-host", "host");
        var reloadedOther = new QuestionService(other, "store-host", null, store).Subscribers;

        // A running host whose reconnect arrives under another account drops them too.
        var running = new QuestionService(same, "store-host", null, store);
        var beforeSwitch = running.Subscribers;
        running.Relay = other;
        var afterSwitch = running.Subscribers;
        var afterSwitchReload = new QuestionService(same, "store-host", null, store).Subscribers;
        await same.DisposeAsync();
        await other.DisposeAsync();
        File.Delete(store);
        return (reloadedSame == 1 && reloadedOther == 0 && beforeSwitch == 1 && afterSwitch == 0 && afterSwitchReload == 0,
            $"same account reloads {reloadedSame}, other account {reloadedOther}; switch while running {beforeSwitch} -> {afterSwitch}, then reloaded as the first account {afterSwitchReload}");
    });

    await Check("the relay's logs carry no tokens or payloads", () =>
    {
        var all = string.Join("\n", logs);
        var ok = !all.Contains(octoAccount.Token) && !all.Contains("gho_") && !all.Contains(Marker);
        return Task.FromResult((ok, $"{logs.Count} log lines"));
    });

    await other.Relay.DisposeAsync();
    var last = await Connect(octoAccount, "last", "test");
    var stopwatch = Stopwatch.StartNew();
    await relay.StopAsync();
    await Check("a relay that is stopping tells connected devices to reconnect at once (1012)", async () =>
    {
        await last.Relay.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        return ((int?)last.Relay.CloseStatus == 1012, $"close {(int?)last.Relay.CloseStatus} {last.Relay.CloseReason} after {stopwatch.ElapsedMilliseconds} ms");
    });
    Console.WriteLine(failed == 0 ? "all checks passed" : $"{failed} check(s) failed");
    return failed == 0 ? 0 : 1;

    // Speaks the hello and proof by hand, so a test can claim one key and prove another.
    async Task<(int? Code, string? Reason)> RawHello(string token, byte[] claimedPub, P256KeyPair prover)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(RelayClient.SocketUri(url), default);
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

Console.Error.WriteLine("usage: serve --port N | check");
return 2;

string? Arg(string n)
{
    var i = Array.IndexOf(args, n);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

sealed class CaptureLogger(ConcurrentQueue<string> lines) : ILoggerProvider, ILogger
{
    public ILogger CreateLogger(string categoryName) => this;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        lines.Enqueue(formatter(state, exception));
    public void Dispose() { }
}
