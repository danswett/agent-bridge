// Spike S4 host: a stand-in for the Bridge Host's relay side.
//   sign-in --relay URL                    GitHub device flow through the relay
//   run     --relay URL [--name N]         stay connected and answer peers (reconnects)
//   call    --relay URL --to NAME|ID [--count N]
//                                          open an E2E channel to a peer, snapshot, N pings
//   whoami
// Every command takes --profile P (default "default") to keep several identities on one machine.
using System.Diagnostics;
using System.Text.Json;
using S4;

var mode = args.Length > 0 ? args[0] : "";
var profile = Arg("--profile") ?? "default";
var relayArg = Arg("--relay");
var id = Identity.Load(profile);
var name = Arg("--name") ?? $"{Environment.MachineName}{(profile == "default" ? "" : $"/{profile}")}";

switch (mode)
{
    case "whoami":
        Console.WriteLine($"device {Wire.DeviceId(id.Key.Public)}  account {id.Account?.Login ?? "(not signed in)"}  key file {id.Folder}");
        return 0;

    case "sign-in":
        {
            using var http = new HttpClient();
            var result = await SignIn.DeviceFlowAsync(http, Relay(), (code, uri) =>
                Console.WriteLine($"Open {uri} and enter the code {code}"));
            id.Save(result);
            Console.WriteLine($"signed in as {result.Login}; device {Wire.DeviceId(id.Key.Public)}");
            return 0;
        }

    case "run":
        {
            var account = id.Account ?? throw new InvalidOperationException("sign in first");
            var backoff = TimeSpan.FromSeconds(1);
            int reconnects = 0;
            var since = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    await using var relay = await RelayClient.ConnectAsync(Relay(), account.Token, id.Key, name, "host");
                    var hub = new ChannelHub(relay, id.Key, name, "host") { Handler = (ch, op, data) => DemoService.HandleAsync(name, op, data) };
                    hub.Accepted += ch => Log($"channel from {ch.PeerName} ({ch.PeerKind}, {ch.PeerId[..8]})");
                    hub.Rejected += (peer, e) => Log($"rejected a frame from {peer[..8]}: {e.Message}");
                    relay.PresenceChanged += p => Log($"online: {string.Join(", ", p.Select(d => $"{d.Name} ({d.Kind}, {d.Id[..8]})"))}");
                    Log($"connected as {relay.Login}, device {relay.DeviceId}{(reconnects > 0 ? $", reconnect {reconnects}" : "")}");
                    backoff = TimeSpan.FromSeconds(1);
                    since.Restart();
                    await relay.Completion;
                    Log($"connection ended after {since.Elapsed.TotalMinutes:F1} min: {(int?)relay.CloseStatus} {relay.CloseReason}");
                    if ((int?)relay.CloseStatus == CloseCodes.Replaced) { Log("another process took over this device; stopping"); return 1; }
                }
                catch (RelayRefusedException e) when (e.Code is CloseCodes.AuthFailed or CloseCodes.ProofFailed)
                {
                    Log($"refused: {e.Message}; sign in again");
                    return 1;
                }
                catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or HttpRequestException or RelayRefusedException or IOException)
                {
                    Log($"connect failed: {e.Message}");
                }
                reconnects++;
                await Task.Delay(backoff);
                backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
            }
        }

    case "call":
        {
            var account = id.Account ?? throw new InvalidOperationException("sign in first");
            var to = Arg("--to") ?? throw new ArgumentException("--to is required");
            var count = int.Parse(Arg("--count") ?? "20");
            var total = Stopwatch.StartNew();
            await using var relay = await RelayClient.ConnectAsync(Relay(), account.Token, id.Key, name, "app");
            var connected = total.Elapsed;
            var hub = new ChannelHub(relay, id.Key, name, "app");
            var peer = await relay.WaitForPeerAsync(p => p.Id.StartsWith(to, StringComparison.OrdinalIgnoreCase) || p.Name.Equals(to, StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(10));
            var hs = Stopwatch.StartNew();
            var ch = await hub.OpenAsync(peer.Id);
            var handshake = hs.Elapsed;
            Console.WriteLine($"channel to {ch.PeerName} ({ch.PeerKind}); its key hashes to {Wire.DeviceId(ch.PeerStatic!)} = the id it was reached at");
            Console.WriteLine($"handshake hash {Wire.Hex(ch.HandshakeHash)[..16]}...");
            var snapshot = await ch.RequestAsync("snapshot");
            Console.WriteLine($"snapshot: {snapshot.GetProperty("result").GetRawText()}");
            var rtts = new List<double>();
            for (int i = 0; i < count; i++)
            {
                var sw = Stopwatch.StartNew();
                await ch.RequestAsync("ping");
                rtts.Add(sw.Elapsed.TotalMilliseconds);
            }
            rtts.Sort();
            Console.WriteLine($"relay connect {connected.TotalMilliseconds:F0} ms, handshake {handshake.TotalMilliseconds:F0} ms, {count} pings: median {rtts[rtts.Count / 2]:F0} ms, p95 {rtts[(int)(rtts.Count * 0.95)]:F0} ms, max {rtts[^1]:F0} ms");
            return 0;
        }
}

Console.Error.WriteLine("usage: sign-in | run | call --to NAME|ID | whoami   (with --relay URL [--profile P] [--name N])");
return 2;

Uri Relay() => new(relayArg ?? throw new ArgumentException("--relay is required"));

string? Arg(string n)
{
    var i = Array.IndexOf(args, n);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
