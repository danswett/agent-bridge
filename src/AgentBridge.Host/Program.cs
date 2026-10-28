using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using AgentBridge.Transport;

// agent-bridge-host: the Bridge Host. A console front for the read-only core, and the
// server that shows its sessions to the user's other devices through the relay:
//
//   agent-bridge-host sessions [--compact]      one scan, the SessionList as JSON
//   agent-bridge-host get <id>                  one session with its tool history
//   agent-bridge-host watch [--json]            a line per upsert and removal, until Ctrl+C
//   agent-bridge-host sign-in --relay <url>     GitHub sign-in through the relay
//   agent-bridge-host serve [--relay <url>] [--name <name>]
//                                               serve Protocol v1 sessions, reconnecting
//   agent-bridge-host whoami                    this device and the account it signed in to
//
// Roots default to the current user's; --copilot-home, --claude-home and --codex-home
// point elsewhere. Nothing under them is ever written. sign-in, serve and whoami take
// --profile <name> (default "default"): the device key and account live in the product's
// own folder (Identity.DefaultRoot), never agent-ha-bridge's. --relay defaults to
// AGENT_BRIDGE_RELAY, then to the relay the profile signed in to.

var arguments = args.ToList();
var command = arguments.Count > 0 ? arguments[0] : "help";
string? Option(string name)
{
    var i = arguments.IndexOf(name);
    return i >= 0 && i + 1 < arguments.Count ? arguments[i + 1] : null;
}
bool Flag(string name) => arguments.Contains(name);

var defaults = SessionSourceOptions.ForCurrentUser();
var options = new SessionSourceOptions
{
    CopilotHome = Option("--copilot-home") ?? defaults.CopilotHome,
    ClaudeHome = Option("--claude-home") ?? defaults.ClaudeHome,
    CodexHome = Option("--codex-home") ?? defaults.CodexHome,
    ClaudeRegistrationRoots = defaults.ClaudeRegistrationRoots,
    CodexRegistrationRoots = defaults.CodexRegistrationRoots,
};
var pretty = new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = !Flag("--compact") };

switch (command)
{
    case "sessions":
        {
            await using var source = new SessionSource(options);
            await source.RefreshAsync();
            Console.WriteLine(JsonSerializer.Serialize(new SessionList(source.Snapshot(), source.Revision), pretty));
            return 0;
        }
    case "get" when arguments.Count > 1:
        {
            await using var source = new SessionSource(options);
            await source.RefreshAsync();
            var session = source.Get(arguments[1]);
            Console.WriteLine(session is null ? "{\"missing\":true}" : JsonSerializer.Serialize(session, pretty));
            return session is null ? 1 : 0;
        }
    case "watch":
        {
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            await using var source = new SessionSource(options);
            await source.StartAsync(stop.Token);
            using var subscription = source.Subscribe();
            var json = Flag("--json");
            try
            {
                await foreach (var ev in subscription.Events.ReadAllAsync(stop.Token))
                {
                    Console.WriteLine(json ? JsonSerializer.Serialize(new { ev = ev.Name, data = ev.Data }, ProtocolJson.Options) : Describe(ev));
                }
            }
            catch (OperationCanceledException) { }
            return 0;
        }
    case "sign-in":
        {
            var identity = LoadIdentity();
            var relay = RelayUri(identity) ?? throw new ArgumentException("--relay is required");
            using var http = new HttpClient();
            var result = await SignIn.DeviceFlowAsync(http, relay, (code, uri) => Console.WriteLine($"Open {uri} and enter the code {code}"));
            identity.Save(result);
            File.WriteAllText(Path.Combine(identity.Folder, "relay.txt"), relay.ToString());
            Console.WriteLine($"signed in as {result.Login}; device {Wire.DeviceId(identity.Key.Public)}, key in {identity.Storage}");
            return 0;
        }
    case "whoami":
        {
            var identity = LoadIdentity();
            Console.WriteLine($"device {Wire.DeviceId(identity.Key.Public)}  account {identity.Account?.Login ?? "(not signed in)"}  relay {RelayUri(identity)?.ToString() ?? "(none)"}  key in {identity.Storage}  folder {identity.Folder}");
            return 0;
        }
    case "serve":
        return await ServeAsync();
    default:
        Console.Error.WriteLine("usage: agent-bridge-host sessions [--compact] | get <id> | watch [--json] | sign-in --relay <url> | serve [--relay <url>] [--name <name>] | whoami");
        Console.Error.WriteLine("       [--copilot-home <dir>] [--claude-home <dir>] [--codex-home <dir>] [--profile <name>]");
        return command == "help" ? 0 : 2;
}

Identity LoadIdentity() => Identity.Load(Option("--profile") ?? "default");

Uri? RelayUri(Identity identity)
{
    var saved = Path.Combine(identity.Folder, "relay.txt");
    var url = Option("--relay") ?? Environment.GetEnvironmentVariable("AGENT_BRIDGE_RELAY") ?? (File.Exists(saved) ? File.ReadAllText(saved).Trim() : null);
    return string.IsNullOrEmpty(url) ? null : new Uri(url);
}

async Task<int> ServeAsync()
{
    var identity = LoadIdentity();
    var account = identity.Account ?? throw new InvalidOperationException("not signed in: run agent-bridge-host sign-in --relay <url> first");
    var relayUri = RelayUri(identity) ?? throw new ArgumentException("--relay is required");
    var profile = Option("--profile") ?? "default";
    var name = Option("--name") ?? $"{Environment.MachineName}{(profile == "default" ? "" : $"/{profile}")}";
    var machine = new MachineInfo(Wire.DeviceId(identity.Key.Public), name, OsName(), HostVersion());

    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    // One source for the life of the process: reconnecting to the relay neither rescans the
    // machine nor loses a change, and every new channel subscribes to the same state.
    await using var source = new SessionSource(options);
    await source.StartAsync(stop.Token);
    var service = new SessionService(source, machine, Log);
    Log($"watching {source.Snapshot().Count} live session(s); serving as \"{name}\" ({machine.Id[..8]}) on {relayUri.Host}");

    var backoff = TimeSpan.FromSeconds(1);
    var reconnects = 0;
    var since = Stopwatch.StartNew();
    while (!stop.IsCancellationRequested)
    {
        try
        {
            await using var relay = await RelayClient.ConnectAsync(relayUri, account.Token, identity.Key, name, "host", stop.Token);
            var hub = new ChannelHub(relay, identity.Key, name, "host")
            {
                JsonOptions = ProtocolJson.Options,
                Handler = service.HandleAsync,
            };
            hub.Accepted += ch => Log($"channel from {ch.PeerName} ({ch.PeerKind}, {ch.PeerId[..8]})");
            hub.Rejected += (peer, e) => Log($"rejected a frame from {peer[..Math.Min(8, peer.Length)]}: {e.Message}");
            Log($"connected as {relay.Login}{(reconnects > 0 ? $", reconnect {reconnects}" : "")}");
            backoff = TimeSpan.FromSeconds(1);
            since.Restart();
            await relay.Completion.WaitAsync(stop.Token);
            Log($"connection ended after {since.Elapsed.TotalMinutes:F1} min: {(int?)relay.CloseStatus} {relay.CloseReason}");
            if ((int?)relay.CloseStatus == CloseCodes.Replaced) { Log("another process took over this device; stopping"); return 1; }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
        catch (RelayRefusedException e) when (e.Code is CloseCodes.AuthFailed or CloseCodes.ProofFailed)
        {
            Log($"refused: {e.Message}; run sign-in again");
            return 1;
        }
        // Anything else the relay or network does to one connection is a reason to
        // reconnect, never to exit: the relay is not trusted, and a host that one bad frame
        // can stop is a denial of service.
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log($"connection failed: {e.GetType().Name}: {e.Message}");
        }
        reconnects++;
        try { await Task.Delay(backoff, stop.Token); }
        catch (OperationCanceledException) { break; }
        backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
    }
    Log("stopped");
    return 0;
}

static string OsName() =>
    OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

static string HostVersion()
{
    var v = typeof(SessionService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    // The SDK appends "+<commit>"; the contract's hostVersion is the version alone.
    var plus = v.IndexOf('+');
    return plus < 0 ? v : v[..plus];
}

static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");

static string Describe(SessionEvent ev) => ev.Data switch
{
    Session s => $"{DateTimeOffset.UtcNow:HH:mm:ss.fff} upsert {s.Id} {s.Agent} {s.Status} since {s.StatusSince:HH:mm:ss} tool={s.Activity.CurrentTool?.Name ?? "-"} question={(s.Question is null ? "-" : s.Question.Kind.ToString())} \"{s.Title}\"",
    SessionRemoved r => $"{DateTimeOffset.UtcNow:HH:mm:ss.fff} remove {r.Id}",
    SessionsSynced y => $"{DateTimeOffset.UtcNow:HH:mm:ss.fff} synced revision {y.Revision}",
    _ => ev.Name,
};
