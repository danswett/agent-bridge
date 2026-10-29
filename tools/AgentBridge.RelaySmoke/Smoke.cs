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
            Console.Error.WriteLine("usage: relay-smoke --relay <https://...> [--origin <allowed web origin>] [--expect-version <v>] [--expect-push] [--wait <seconds>]");
            Console.Error.WriteLine("  RELAY_SMOKE_TOKEN_KEY: the relay's token key (base64). With it, two devices are connected and talk.");
            return 2;
        }
        var smoke = new Smoke(uri, Console.Out)
        {
            TokenKey = Environment.GetEnvironmentVariable("RELAY_SMOKE_TOKEN_KEY") is { Length: > 0 } k ? k : null,
            AllowedOrigin = Arg("--origin"),
            ExpectVersion = Arg("--expect-version"),
            ExpectPush = args.Contains("--expect-push"),
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
        await using var app = await RelayClient.ConnectAsync(relay, token, appKey, "smoke app", "test", Timeout());
        var appHub = new ChannelHub(app, appKey, "smoke app", "test");
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

    CancellationToken Timeout() => new CancellationTokenSource(StepTimeout).Token;

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
