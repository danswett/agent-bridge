using System.Security.Cryptography;
using AgentBridge.AcceptRelay;
using AgentBridge.Relay;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

// accept-relay serve [--port <n>]: the product relay beside an auto-approving fake GitHub, both
// on 127.0.0.1, for the clean-install acceptance script (#736). It holds no real secret: the
// client id and secret are fixed fakes and the token key is random and in memory.
if (args.Length == 0 || args[0] != "serve") { return Usage(); }
var port = 0;
for (var i = 1; i < args.Length; i++)
{
    if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[++i], out port) && port is >= 0 and <= 65535) { continue; }
    return Usage();
}

var github = new FakeGitHub();
await using var ghApp = await github.StartAsync();
var relay = RelayApp.Build([], o =>
{
    o.GitHubBaseUrl = o.GitHubApiUrl = github.Url;
    o.GitHubClientId = FakeGitHub.ClientId;
    o.GitHubClientSecret = FakeGitHub.ClientSecret;
    o.TokenKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}, b =>
{
    // Loopback only, by construction: the address is not configurable.
    b.WebHost.UseUrls($"http://127.0.0.1:{port}");
    b.Logging.ClearProviders();
});
await relay.StartAsync();
var relayUrl = relay.Urls.First().TrimEnd('/');

// READY only once both answer, so a script can wait for the line and then sign in.
using var gate = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
var healthy = (await gate.GetAsync($"{relayUrl}/healthz")).IsSuccessStatusCode;
var fakeUp = (await gate.PostAsync($"{github.Url}/login/device/code", null)).IsSuccessStatusCode;
if (!healthy || !fakeUp) { Console.Error.WriteLine("accept-relay: the relay or the fake GitHub did not answer"); return 1; }
Console.WriteLine($"READY relay={relayUrl}");

var stop = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.TrySetResult();
await stop.Task;
await relay.StopAsync();
return 0;

static int Usage()
{
    Console.Error.WriteLine("usage: accept-relay serve [--port <0-65535>]   (always binds 127.0.0.1; 0 picks a free port)");
    return 2;
}
