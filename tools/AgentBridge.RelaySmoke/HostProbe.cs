using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.Relay;
using AgentBridge.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentBridge.RelaySmoke;

// Ends a host's connection to a relay in a chosen way, so the relay's disconnect log line
// (and the roster an app sees) can be read for each kind of end (#970 slice 1a):
//   kill       hold, then Environment.FailFast: the process dies with no close frame.
//   blackhole  connect, then never read again: the relay's pings go unanswered.
//   hold       hold, then close normally.
//   observe    an app of the same account that prints presence and roster changes.
// Tokens are minted in-process from RELAY_SMOKE_TOKEN_KEY, as the smoke test does. Neither the
// token nor the key is ever printed; the throwaway account and device ids are, with UTC times,
// so the lines can be matched against the relay's logs.
public static class HostProbe
{
    public const string Usage = "usage: relay-smoke host-probe --relay <https://...> --mode kill|blackhole|hold|observe [--hold <seconds>] [--count N] [--account <id>] [--allow-production]\n" +
        "  RELAY_SMOKE_TOKEN_KEY (base64) is required. A probe prints its account id; pass it as --account to observe to watch the same account.";

    const string Staging = "https://agent-bridge-relay-staging.icyflower-4f71155d.westus2.azurecontainerapps.io";

    // Killing or blackholing sockets is for staging; a production relay is refused unless the
    // person says so, and a cleartext relay is never used, since the token would cross it.
    static readonly string[] ProductionHosts =
    [
        "relay.bad-duck.com",
        "agent-bridge-relay.icyflower-4f71155d.westus2.azurecontainerapps.io",
        "relay.slipgates.com",
    ];

    public static string? CheckRelay(Uri relay, bool allowProduction)
    {
        if (relay.Scheme != Uri.UriSchemeHttps) { return $"the relay must be https (staging: {Staging})"; }
        if (!allowProduction && ProductionHosts.Contains(relay.IdnHost, StringComparer.OrdinalIgnoreCase))
        {
            return $"{relay.IdnHost} is a production relay; use staging ({Staging}), or pass --allow-production";
        }
        return null;
    }

    public static async Task<int> RunAsync(string[] args, TextWriter output)
    {
        string? Arg(string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
        var key = Environment.GetEnvironmentVariable("RELAY_SMOKE_TOKEN_KEY");
        var mode = Arg("--mode");
        if (Arg("--relay") is not { } url || !Uri.TryCreate(url, UriKind.Absolute, out var relay)
            || mode is not ("kill" or "blackhole" or "hold" or "observe") || string.IsNullOrEmpty(key)
            || !int.TryParse(Arg("--hold") ?? (mode == "observe" ? "300" : "10"), out var hold) || hold < 0
            || !int.TryParse(Arg("--count") ?? "1", out var count) || count is < 1 or > 20)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        if (CheckRelay(relay, args.Contains("--allow-production")) is { } refused)
        {
            Console.Error.WriteLine(refused);
            return 2;
        }
        var account = Arg("--account") ?? "smoke-" + RandomNumberGenerator.GetHexString(12, lowercase: true);
        if (mode == "observe" && Arg("--account") is null)
        {
            Console.Error.WriteLine("observe needs --account <id> from a probe's output");
            return 2;
        }
        var tokens = new Tokens(new RelayOptions { TokenKey = key, TokenLifetime = TimeSpan.FromMinutes(10) }, NullLogger<Tokens>.Instance);
        var token = tokens.Issue(new Account(account, "relay-smoke")).Token;
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        Say(output, $"mode {mode} account {account} hold {hold}s count {count}");
        try
        {
            return mode switch
            {
                "observe" => await ObserveAsync(relay, token, hold, output, cts.Token),
                "blackhole" => await BlackholeAsync(relay, token, hold, count, output, cts.Token),
                _ => await HoldAsync(relay, token, hold, count, kill: mode == "kill", output, cts.Token),
            };
        }
        catch (OperationCanceledException) { return 0; }
    }

    static void Say(TextWriter output, string line) => output.WriteLine($"{DateTime.UtcNow:O} {line}");

    static async Task<int> HoldAsync(Uri relay, string token, int hold, int count, bool kill, TextWriter output, CancellationToken ct)
    {
        var clients = new List<RelayClient>();
        var keys = new List<P256KeyPair>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                var k = P256KeyPair.Generate();
                keys.Add(k);
                var c = await RelayClient.ConnectAsync(relay, token, k, "host probe", "host", ct);
                clients.Add(c);
                Say(output, $"connected device {c.DeviceId}");
            }
            await Task.Delay(TimeSpan.FromSeconds(hold), ct);
            if (kill)
            {
                Say(output, "killing now: FailFast, no close frame");
                await output.FlushAsync();
                Environment.FailFast("host-probe kill");
            }
            Say(output, "closing normally");
            foreach (var c in clients) { await c.DisposeAsync(); }
            Say(output, "closed");
            return 0;
        }
        finally
        {
            foreach (var k in keys) { k.Dispose(); }
        }
    }

    // The relay's pings are answered only by a client that is reading, so a socket that never
    // reads again is one the relay must time out on its own. RelayClient always reads, so this
    // speaks the admission by hand and stops after "ready".
    static async Task<int> BlackholeAsync(Uri relay, string token, int hold, int count, TextWriter output, CancellationToken ct)
    {
        var sockets = new List<ClientWebSocket>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                using var key = P256KeyPair.Generate();
                var ws = new ClientWebSocket();
                sockets.Add(ws);
                ws.Options.KeepAliveInterval = TimeSpan.Zero;
                await ws.ConnectAsync(RelayClient.SocketUri(relay), ct);
                await SendAsync(ws, new { t = "hello", token, pub = Wire.Hex(key.Public), name = "host probe", kind = "host" }, ct);
                var challenge = await ControlAsync(ws, "challenge", ct);
                var mac = Wire.ProofMac(key.Dh(Wire.Unhex(challenge.GetProperty("eph").GetString()!)), Wire.Unhex(challenge.GetProperty("nonce").GetString()!), challenge.GetProperty("account").GetString()!);
                await SendAsync(ws, new { t = "proof", mac = Wire.Hex(mac) }, ct);
                await ControlAsync(ws, "ready", ct);
                Say(output, $"connected device {Wire.DeviceId(key.Public)}; not reading from now on");
            }
            await Task.Delay(TimeSpan.FromSeconds(hold), ct);
            Say(output, "hold over; dropping the sockets");
            return 0;
        }
        finally
        {
            foreach (var ws in sockets) { ws.Dispose(); }
        }
    }

    static Task SendAsync(ClientWebSocket ws, object message, CancellationToken ct) =>
        ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, ct);

    static async Task<JsonElement> ControlAsync(ClientWebSocket ws, string type, CancellationToken ct)
    {
        while (true)
        {
            var frame = await Wire.ReceiveAsync(ws, Wire.MaxFrame, ct) ?? throw new InvalidOperationException($"the relay closed before {type}");
            if (frame.Type != WebSocketMessageType.Text) { continue; }
            var j = JsonDocument.Parse(frame.Data).RootElement.Clone();
            if (j.GetProperty("t").GetString() == type) { return j; }
        }
    }

    static async Task<int> ObserveAsync(Uri relay, string token, int seconds, TextWriter output, CancellationToken ct)
    {
        using var key = P256KeyPair.Generate();
        await using var app = await RelayClient.ConnectAsync(relay, token, key, "host probe observer", "app", ct);
        Say(output, $"observing as device {app.DeviceId}");
        var seen = new HashSet<string>();
        var known = new Dictionary<string, DateTimeOffset>();
        var gate = new object();
        void Report(string what) => Say(output, what);
        void Diff()
        {
            lock (gate)
            {
                var snap = app.Snapshot;
                var now = snap.Presence.Select(p => p.Id).ToHashSet();
                foreach (var p in snap.Presence.Where(e => seen.Add(e.Id))) { Report($"presence + {p.Id} {p.Kind}"); }
                foreach (var id in seen.Where(id => !now.Contains(id)).ToList()) { seen.Remove(id); Report($"presence - {id}"); }
                if (snap.Known is { } list)
                {
                    foreach (var m in list.Where(m => !known.TryGetValue(m.Id, out var at) || at != m.LastSeen))
                    {
                        Report($"roster {m.Id} {m.Kind} lastSeen {m.LastSeen.UtcDateTime:O}");
                        known[m.Id] = m.LastSeen;
                    }
                    foreach (var id in known.Keys.Where(id => list.All(m => m.Id != id)).ToList()) { known.Remove(id); Report($"roster - {id}"); }
                }
            }
        }
        app.PresenceChanged += _ => Diff();
        Diff();
        var end = Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        if (await Task.WhenAny(end, app.Completion) == app.Completion) { Say(output, $"observer's own socket ended (close {(app.CloseStatus is { } s ? ((int)s).ToString() : "none")})"); }
        return 0;
    }
}
