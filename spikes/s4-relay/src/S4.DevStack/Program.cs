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
    var hub = new ChannelHub(host, key, "demo-host", "host") { Handler = (ch, op, data) => DemoService.HandleAsync("demo-host", op, data) };
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
    var taps = new ConcurrentQueue<byte[]>();
    var logs = new ConcurrentQueue<string>();
    Func<string, string, (string, string)?>? route = null;
    var relay = RelayApp.Build([], o =>
    {
        o.GitHubBaseUrl = o.GitHubApiUrl = fake.Url;
        o.GitHubClientId = "fake-client";
        o.GitHubClientSecret = "fake-secret";
        o.MaxFramesPerSecond = 200;
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

    await Check("the relay's logs carry no tokens or payloads", () =>
    {
        var all = string.Join("\n", logs);
        var ok = !all.Contains(octoAccount.Token) && !all.Contains("gho_") && !all.Contains(Marker);
        return Task.FromResult((ok, $"{logs.Count} log lines"));
    });

    await other.Relay.DisposeAsync();
    await relay.StopAsync();
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
