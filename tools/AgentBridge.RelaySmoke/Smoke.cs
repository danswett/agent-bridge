using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.Relay;
using AgentBridge.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentBridge.RelaySmoke;

// Checks a deployed relay from outside, as a device sees it: the HTTP surface, admission and
// refusals, and with a token key, two devices that find each other and talk over a Noise
// channel. Run by the deploy workflow against staging before production is offered, and
// against production after a promotion (without a key: nothing there is signed by CI).
public sealed class Smoke(Uri relay, TextWriter output)
{
    public string? TokenKey { get; init; }
    public string? AllowedOrigin { get; init; }
    public string? ExpectVersion { get; init; }
    public bool ExpectPush { get; init; }
    // The relay must have a blob store, and a multi-MB file must round-trip through it.
    public bool ExpectBlobs { get; init; }
    public int BlobBytes { get; init; } = 5 * 1024 * 1024 + 123;
    // A staging replica scaled to zero takes a while to start; production should answer at once.
    public TimeSpan StartupWait { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan StepTimeout { get; init; } = TimeSpan.FromSeconds(30);

    readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };
    int failures;

    public static async Task<int> Main(string[] args)
    {
        string? Arg(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        if (Arg("--relay") is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            Console.Error.WriteLine("usage: relay-smoke --relay <https://...> [--origin <allowed web origin>] [--expect-version <v>] [--expect-push] [--expect-blobs] [--wait <seconds>]");
            Console.Error.WriteLine("  RELAY_SMOKE_TOKEN_KEY: the relay's token key (base64). With it, two devices are connected and talk.");
            return 2;
        }
        var smoke = new Smoke(uri, Console.Out)
        {
            TokenKey = Environment.GetEnvironmentVariable("RELAY_SMOKE_TOKEN_KEY") is { Length: > 0 } k ? k : null,
            AllowedOrigin = Arg("--origin"),
            ExpectVersion = Arg("--expect-version"),
            ExpectPush = args.Contains("--expect-push"),
            ExpectBlobs = args.Contains("--expect-blobs"),
            StartupWait = Arg("--wait") is { } w ? TimeSpan.FromSeconds(int.Parse(w)) : TimeSpan.FromMinutes(3),
        };
        return await smoke.RunAsync() ? 0 : 1;
    }

    public async Task<bool> RunAsync()
    {
        await Step("health and version", HealthAsync);
        if (failures > 0) { return false; }
        await Step("ready", async () =>
        {
            using var r = await http.GetAsync(new Uri(relay, "/readyz"));
            Expect(r.StatusCode == HttpStatusCode.OK, $"/readyz answered {(int)r.StatusCode}");
        });
        await Step("serves no app code", async () =>
            Expect((await http.GetStringAsync(relay)).Contains("serves no app code"), "the root serves something else"));
        await Step(ExpectPush ? "push key" : "push disabled", async () =>
        {
            using var r = await http.GetAsync(new Uri(relay, "/v1/push/key"));
            if (!ExpectPush) { Expect(r.StatusCode == HttpStatusCode.NotFound, $"/v1/push/key answered {(int)r.StatusCode}"); return; }
            Expect(r.IsSuccessStatusCode, $"/v1/push/key answered {(int)r.StatusCode}");
            var key = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString();
            // An uncompressed P-256 point, base64url: what pushManager.subscribe takes.
            Expect(key is { Length: 87 }, "the push key is not a P-256 public key");
        });
        if (AllowedOrigin is { } origin)
        {
            await Step("CORS for the PWA's origin", async () =>
            {
                Expect(await PreflightAsync(origin) == origin, $"no CORS grant for {origin}");
                Expect(await PreflightAsync("https://evil.example") is null, "CORS granted to a foreign origin");
            });
            await Step("socket from the PWA's origin opens", async () => Expect(await OpensAsync(origin), $"a socket from {origin} was refused"));
        }
        await Step("socket from a foreign origin is refused", async () => Expect(!await OpensAsync("https://evil.example"), "a foreign origin opened a socket"));
        await Step("bad token is refused with 4001", async () =>
        {
            using var key = P256KeyPair.Generate();
            try
            {
                await using var c = await RelayClient.ConnectAsync(relay, "not.atoken", key, "relay-smoke", "test", Timeout());
                Expect(false, "a bad token was admitted");
            }
            catch (RelayRefusedException e) { Expect(e.Code == CloseCodes.AuthFailed, $"refused with {e.Code}, not 4001"); }
        });
        if (TokenKey is null)
        {
            output.WriteLine("SKIP two devices over a channel (no RELAY_SMOKE_TOKEN_KEY)");
        }
        else
        {
            await Step("two devices over a Noise channel", TwoDevicesAsync);
            await Step("a multi-MB file through the blob store", BlobsAsync);
        }
        output.WriteLine(failures == 0 ? "smoke: all checks passed" : $"smoke: {failures} check(s) failed");
        return failures == 0;
    }

    async Task HealthAsync()
    {
        var deadline = Stopwatch.StartNew();
        string last = "no answer";
        while (deadline.Elapsed < StartupWait)
        {
            try
            {
                var j = await http.GetFromJsonAsync<JsonElement>(new Uri(relay, "/healthz"));
                var version = j.TryGetProperty("version", out var v) ? v.GetString() : null;
                if (j.GetProperty("ok").GetBoolean() && (ExpectVersion is null || version == ExpectVersion)) { return; }
                last = $"version {version ?? "(none)"}";
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException) { last = e.Message; }
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
        Expect(false, $"not healthy{(ExpectVersion is null ? "" : $" at version {ExpectVersion}")} after {StartupWait.TotalSeconds:F0} s: {last}");
    }

    async Task TwoDevicesAsync()
    {
        // A fresh account per run: nothing real is ever in it, and runs cannot see each other.
        var account = new Account("smoke-" + RandomNumberGenerator.GetHexString(12, lowercase: true), "relay-smoke");
        var tokens = new Tokens(new RelayOptions { TokenKey = TokenKey, TokenLifetime = TimeSpan.FromMinutes(10) }, NullLogger<Tokens>.Instance);
        var token = tokens.Issue(account).Token;
        using var hostKey = P256KeyPair.Generate();
        using var appKey = P256KeyPair.Generate();
        await using var host = await RelayClient.ConnectAsync(relay, token, hostKey, "smoke host", "host", Timeout());
        var hostHub = new ChannelHub(host, hostKey, "smoke host", "host")
        {
            Handler = (_, op, data) => Task.FromResult<object?>(op == "echo" ? data : null),
        };
        // An app, a person's kind, so it may forget the host once it has gone (the roster, below).
        await using var app = await RelayClient.ConnectAsync(relay, token, appKey, "smoke app", "app", Timeout());
        var appHub = new ChannelHub(app, appKey, "smoke app", "app");
        await app.WaitForPeerAsync(p => p.Id == host.DeviceId, StepTimeout);

        var sw = Stopwatch.StartNew();
        var ch = await appHub.OpenAsync(host.DeviceId, StepTimeout);
        var handshake = sw.Elapsed;
        var text = "smoke " + RandomNumberGenerator.GetHexString(16);
        sw.Restart();
        var reply = await ch.RequestAsync("echo", new { text }, StepTimeout);
        var roundTrip = sw.Elapsed;
        Expect(reply.GetProperty("ok").GetBoolean() && reply.GetProperty("result").GetProperty("text").GetString() == text, "the echo came back different");
        output.WriteLine($"     handshake {handshake.TotalMilliseconds:F0} ms, round trip {roundTrip.TotalMilliseconds:F0} ms");

        var undeliverable = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Undeliverable += to => undeliverable.TrySetResult(to);
        var nobody = RandomNumberGenerator.GetHexString(Wire.IdBytes * 2, lowercase: true);
        await app.SendAsync(nobody, [1, 2, 3]);
        Expect(await undeliverable.Task.WaitAsync(StepTimeout) == nobody, "no undeliverable notice for an absent device");

        // Proves the push path end to end inside the relay without contacting a push service:
        // an endpoint off the allowlist is refused before any request is made.
        var pushed = await host.PushAsync("https://push.example/x", [1]);
        Expect(pushed.Error == (ExpectPush ? "endpoint-not-allowed" : "push-disabled"), $"push answered {pushed.Status} {pushed.Error}");

        await host.DisposeAsync();
        var deadline = Stopwatch.StartNew();
        while (app.Presence.Any(p => p.Id == host.DeviceId))
        {
            Expect(deadline.Elapsed < StepTimeout, "the host never left presence");
            await Task.Delay(50);
        }

        // The roster (docs/protocol/roster.md): the host that left is listed as offline, with when
        // it was last seen, and the app forgets it, which leaves the throwaway account empty.
        Expect(app.Known is not null, "presence carries no roster (known)");
        deadline.Restart();
        while (app.Known?.Any(k => k.Id == host.DeviceId) != true)
        {
            Expect(deadline.Elapsed < StepTimeout, "the host that left was never listed as offline");
            await Task.Delay(50);
        }
        Expect(await app.ForgetAsync(host.DeviceId, Timeout()) is null, "the app could not forget the host that left");
        deadline.Restart();
        while (app.Known?.Any(k => k.Id == host.DeviceId) != false)
        {
            Expect(deadline.Elapsed < StepTimeout, "the forgotten host is still listed");
            await Task.Delay(50);
        }
    }

    // As a phone sends a photo to a host: sealed, uploaded straight to storage on a write-once
    // URL, fetched by the receiver alone, opened, checked and deleted. Another device of the
    // account and a device of another account are refused. Skipped, not failed, on a relay
    // without a blob store unless --expect-blobs says it must have one.
    async Task BlobsAsync()
    {
        var tokens = new Tokens(new RelayOptions { TokenKey = TokenKey, TokenLifetime = TimeSpan.FromMinutes(10) }, NullLogger<Tokens>.Instance);
        string Token(string who) => tokens.Issue(new Account("smoke-" + RandomNumberGenerator.GetHexString(12, lowercase: true), who)).Token;
        var token = Token("relay-smoke");
        using var sendKey = P256KeyPair.Generate();
        using var receiveKey = P256KeyPair.Generate();
        using var otherKey = P256KeyPair.Generate();
        using var strangerKey = P256KeyPair.Generate();
        await using var sender = await RelayClient.ConnectAsync(relay, token, sendKey, "smoke phone", "test", Timeout());
        if (sender.BlobMaxBytes == 0)
        {
            Expect(!ExpectBlobs, "the relay has no blob store");
            output.WriteLine("     no blob store on this relay; skipped");
            return;
        }
        await using var receiver = await RelayClient.ConnectAsync(relay, token, receiveKey, "smoke host", "test", Timeout());
        await using var other = await RelayClient.ConnectAsync(relay, token, otherKey, "smoke laptop", "test", Timeout());
        await using var stranger = await RelayClient.ConnectAsync(relay, Token("relay-smoke-other"), strangerKey, "smoke stranger", "test", Timeout());
        using var http = BlobHttp.CreateClient();
        var file = RandomNumberGenerator.GetBytes(BlobBytes);

        var sw = Stopwatch.StartNew();
        var sent = await BlobTransfer.SendAsync(sender, http, receiver.DeviceId, file, Timeout(TimeSpan.FromMinutes(2)));
        var up = sw.Elapsed;
        foreach (var (who, device) in new[] { ("another device of the account", other), ("a device of another account", stranger) })
        {
            try
            {
                await device.BlobGetAsync(sent.Blob, Timeout());
                Expect(false, $"{who} could read the blob");
            }
            catch (BlobRefusedException e) { Expect(e.Reason == "not-found", $"{who} was refused with {e.Reason}, not not-found"); }
        }
        sw.Restart();
        var received = new MemoryStream();
        var hash = await BlobTransfer.ReceiveAsync(receiver, http, sent.Blob, sent.Key, file.Length, received, ct: Timeout(TimeSpan.FromMinutes(2)));
        var down = sw.Elapsed;
        Expect(received.ToArray().AsSpan().SequenceEqual(file) && hash.AsSpan().SequenceEqual(sent.Sha256), "the file came back different");
        Expect(await receiver.BlobDeleteAsync(sent.Blob, Timeout()), "the receiver could not delete the blob");
        try
        {
            await receiver.BlobGetAsync(sent.Blob, Timeout());
            Expect(false, "a deleted blob could still be read");
        }
        catch (BlobRefusedException e) { Expect(e.Reason == "not-found", $"a deleted blob was refused with {e.Reason}, not not-found"); }
        output.WriteLine($"     {file.Length / 1048576.0:F1} MiB: up {up.TotalMilliseconds:F0} ms, down {down.TotalMilliseconds:F0} ms");

        // As one host sends another a session (docs/host-transfer.md): from a file, sealed as it
        // streams into storage, never whole in memory.
        var bundle = Path.Combine(Environment.CurrentDirectory, $"relay-smoke-{RandomNumberGenerator.GetHexString(16, lowercase: true)}.bin");
        try
        {
            var bytes = RandomNumberGenerator.GetBytes(BlobBytes * 2);
            await File.WriteAllBytesAsync(bundle, bytes);
            sw.Restart();
            var streamed = await BlobTransfer.SendFileAsync(sender, http, receiver.DeviceId, bundle, Timeout(TimeSpan.FromMinutes(2)));
            up = sw.Elapsed;
            Expect(streamed.Size == bytes.Length, $"the streamed file was sent as {streamed.Size} bytes, not {bytes.Length}");
            var back = new MemoryStream();
            sw.Restart();
            var streamedHash = await BlobTransfer.ReceiveAsync(receiver, http, streamed.Blob, streamed.Key, bytes.Length, back, ct: Timeout(TimeSpan.FromMinutes(2)));
            down = sw.Elapsed;
            Expect(back.ToArray().AsSpan().SequenceEqual(bytes) && streamedHash.AsSpan().SequenceEqual(streamed.Sha256), "the streamed file came back different");
            Expect(await receiver.BlobDeleteAsync(streamed.Blob, Timeout()), "the receiver could not delete the streamed blob");
            output.WriteLine($"     {bytes.Length / 1048576.0:F1} MiB streamed from a file: up {up.TotalMilliseconds:F0} ms, down {down.TotalMilliseconds:F0} ms");
        }
        finally { File.Delete(bundle); }
    }

    async Task<string?> PreflightAsync(string origin)
    {
        using var req = new HttpRequestMessage(HttpMethod.Options, new Uri(relay, "/v1/auth/token"));
        req.Headers.Add("Origin", origin);
        req.Headers.Add("Access-Control-Request-Method", "POST");
        req.Headers.Add("Access-Control-Request-Headers", "content-type");
        using var r = await http.SendAsync(req);
        return r.Headers.TryGetValues("Access-Control-Allow-Origin", out var v) ? v.First() : null;
    }

    async Task<bool> OpensAsync(string origin)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", origin);
        try
        {
            await ws.ConnectAsync(RelayClient.SocketUri(relay), Timeout());
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", default);
            return true;
        }
        catch (WebSocketException) { return false; }
    }

    CancellationToken Timeout(TimeSpan? limit = null) => new CancellationTokenSource(limit ?? StepTimeout).Token;

    sealed class CheckFailed(string message) : Exception(message);

    static void Expect(bool condition, string failure)
    {
        if (!condition) { throw new CheckFailed(failure); }
    }

    async Task Step(string name, Func<Task> check)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await check();
            output.WriteLine($"PASS {name} ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception e)
        {
            failures++;
            output.WriteLine($"FAIL {name}: {(e is CheckFailed ? "" : e.GetType().Name + ": ")}{e.Message}");
        }
    }
}
