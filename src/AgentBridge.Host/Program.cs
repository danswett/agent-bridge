using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Host.Delivery.Windows;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

// agent-bridge-host: the Bridge Host. A console front for the read-only core, and the
// server that shows its sessions to the user's other devices through the relay:
//
//   agent-bridge-host sessions [--compact]      one scan, the SessionList as JSON
//   agent-bridge-host get <id>                  one session with its tool history
//   agent-bridge-host watch [--json]            a line per upsert and removal, until Ctrl+C
//   agent-bridge-host sign-in --relay <url>     GitHub sign-in through the relay
//   agent-bridge-host serve [--relay <url>] [--name <name>] [--local-only]
//                                               serve Protocol v1 sessions to this machine's
//                                               apps on the local pipe (docs/protocol/
//                                               local-pipe.md, with the host's management) and,
//                                               once signed in, through the relay, reconnecting;
//                                               type answers to their questions and replies,
//                                               and end sessions (Windows; see
//                                               docs/host-answers.md, docs/host-replies.md and
//                                               docs/host-stop.md), and notify subscribed
//                                               phones of questions and finished turns. One per
//                                               profile; it logs to <profile>\logs\host.log too
//   agent-bridge-host whoami                    this device and the account it signed in to
//   agent-bridge-host usage                     ask each agent for its allowance once, as JSON
//                                               (serve does it on a timer; docs/protocol/v1-usage.md)
//   agent-bridge-host workspace list|add|remove the folders sessions may be launched in from
//                                               other devices, approved here at the machine
//   agent-bridge-host config show|set|unset     the rest of the profile's config.json
//                                               (docs/host-config.md, docs/host-launch.md)
//   agent-bridge-host ha status|token set|token clear|shadow [--json] [--window <seconds>]
//                                               the Home Assistant connector (docs/ha-connector.md):
//                                               whether serve would run it, and its token, which is
//                                               read from standard input and kept protected
//   agent-bridge-host shadow run|once|summary   compare what this host shows with what
//                                               agent-ha-bridge's daemon shows, read-only, and
//                                               report the week (docs/host-shadow.md)
//
// Roots default to the current user's; --copilot-home, --claude-home and --codex-home
// point elsewhere. Nothing under them is ever written. sign-in, serve and whoami take
// --profile <name> (default "default"): the device key, account and push subscriptions
// live in the product's own folder (Identity.DefaultRoot), never agent-ha-bridge's. --relay
// defaults to AGENT_BRIDGE_RELAY, then to the relay the profile signed in to.

// Device names carry characters outside the console's legacy code page ("iPhone · Agent Bridge"),
// which a redirected log otherwise records as replacement characters.
Console.OutputEncoding = System.Text.Encoding.UTF8;
var arguments = args.ToList();
var command = arguments.Count > 0 ? arguments[0] : "help";
string? Option(string name)
{
    var i = arguments.IndexOf(name);
    return i >= 0 && i + 1 < arguments.Count ? arguments[i + 1] : null;
}
bool Flag(string name) => arguments.Contains(name);

var defaults = SessionSourceOptions.ForCurrentUser();
// serve reads what it may do on this machine from the profile's config.json (HostSettings).
var (settings, settingsProblem) = command == "serve" ? HostSettings.Load(LoadIdentity().Folder) : (HostSettings.Default, null);
var options = new SessionSourceOptions
{
    CopilotHome = Option("--copilot-home") ?? defaults.CopilotHome,
    ClaudeHome = Option("--claude-home") ?? defaults.ClaudeHome,
    CodexHome = Option("--codex-home") ?? defaults.CodexHome,
    ClaudeRegistrationRoots = defaults.ClaudeRegistrationRoots,
    CodexRegistrationRoots = defaults.CodexRegistrationRoots,
    CopilotRemoteApproval = settings.RemoteApproval,
    Log = command == "serve" ? Log : null,
};
var pretty = new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = !Flag("--compact") };

switch (command)
{
    case "sessions":
        {
            await using var source = new SessionSource(options);
            await source.RefreshAsync();
            Console.WriteLine(JsonSerializer.Serialize(new SessionList(source.Snapshot(), source.Revision, null, source.Held), pretty));
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
            if (Refused("sign in")) { return 4; }
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
    case "usage":
        {
            // One ask of every agent, whatever the pace: what serve would report right now.
            await using var source = new SessionSource(options);
            await source.RefreshAsync();
            using var http = new UsageHttp(TimeSpan.FromSeconds(15));
            var usageOptions = UsageOptions.For(options);
            // What it notices goes to stderr: stdout is the report, for whatever parses it.
            Action<string> note = m => Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} {m}");
            await using var monitor = new UsageMonitor(UsageMonitor.Collectors(usageOptions, http, new CommandRunner(), log: note), source, usageOptions, log: note);
            await monitor.PassAsync();
            Console.WriteLine(JsonSerializer.Serialize(monitor.Current.Report, pretty));
            return 0;
        }
    case "serve":
        return await ServeAsync();
    case "ha" when arguments.ElementAtOrDefault(1) == "shadow":
        {
            await using var source = new SessionSource(options);
            await source.RefreshAsync();
            return await HaCommands.ShadowAsync(LoadIdentity().Folder, source, Flag("--json"), Option("--window"), HostVersion(), IsDevBuild(), null, Console.Out, Console.Error);
        }
    case "ha":
        {
            var rest = new List<string>(arguments);
            var p = rest.IndexOf("--profile");
            if (p >= 0) { rest.RemoveRange(p, Math.Min(2, rest.Count - p)); }
            if (HaCommands.Writes(rest) && Refused("change the host's Home Assistant token")) { return 4; }
            Func<string?>? active = OperatingSystem.IsWindows() ? new AgentHaBridgeProbe().Active : null;
            return HaCommands.Run(rest, LoadIdentity().Folder, () => HaCommands.ReadSecret(Console.Error), Console.Out, Console.Error, IsDevBuild(), active);
        }
    case "shadow":
        return await AgentBridge.Host.Shadow.ShadowCommand.RunAsync(arguments, options, HostVersion(), IsDevBuild(), Console.Out, Console.Error);
    case "workspace" or "config":
        {
            // At the machine only: this is how a person approves where sessions may be launched.
            var rest = new List<string>(arguments);
            var p = rest.IndexOf("--profile");
            if (p >= 0) { rest.RemoveRange(p, Math.Min(2, rest.Count - p)); }
            if (ConfigCommands.Writes(rest) && Refused("change the host's config")) { return 4; }
            return ConfigCommands.Run(rest, LoadIdentity().Folder, Console.Out, Console.Error);
        }
    default:
        Console.Error.WriteLine("usage: agent-bridge-host sessions [--compact] | get <id> | watch [--json] | usage | sign-in --relay <url> | serve [--relay <url>] [--name <name>] [--local-only] | whoami");
        Console.Error.WriteLine("       agent-bridge-host workspace list | workspace add <folder> [--label <label>] [--isolate] | workspace remove <label or folder> | config show | config set <key> <value> | config unset <key> | ha status | ha token set | ha token clear | ha shadow [--json] [--window <seconds>]");
        Console.Error.WriteLine("       " + AgentBridge.Host.Shadow.ShadowCommand.Usage);
        Console.Error.WriteLine("       [--copilot-home <dir>] [--claude-home <dir>] [--codex-home <dir>] [--profile <name>]");
        return command == "help" ? 0 : 2;
}

Identity LoadIdentity() => Identity.Load(Option("--profile") ?? "default");

// What may only be done by a person never runs inside an agent session (§6.3 invariant 13): the
// host's own command line refuses it there, as the pipe does.
static bool Refused(string what)
{
    var inside = AgentAncestry.Inside(Environment.GetEnvironmentVariable(Launchers.SessionVariable), ProcessChain.Read(Environment.ProcessId), () => KnownAgents.Running(new SystemProcessInspector()));
    if (inside is null) { return false; }
    Console.Error.WriteLine($"agent-bridge-host: refused to {what}: this is running {inside}. That is for a person to do, in a terminal of their own or from the Agent Bridge app.");
    return true;
}

Uri? RelayUri(Identity identity)
{
    var saved = Path.Combine(identity.Folder, "relay.txt");
    var url = Option("--relay") ?? Environment.GetEnvironmentVariable("AGENT_BRIDGE_RELAY") ?? (File.Exists(saved) ? File.ReadAllText(saved).Trim() : null);
    return string.IsNullOrEmpty(url) ? null : new Uri(url);
}

// The relay the profile last signed in to.
static Uri? SavedRelay(Identity identity)
{
    var saved = Path.Combine(identity.Folder, "relay.txt");
    return File.Exists(saved) && Uri.TryCreate(File.ReadAllText(saved).Trim(), UriKind.Absolute, out var u) ? u : null;
}

async Task<int> ServeAsync()
{
    var identity = LoadIdentity();
    var profile = Option("--profile") ?? "default";
    var localOnly = Flag("--local-only");
    // One host per profile on this machine. A host restarting itself (host.restart) starts its
    // replacement first, which waits here for it to go.
    using var hostLock = HostLock.TryAcquire(identity.Folder, TimeSpan.FromSeconds(15));
    if (hostLock is null)
    {
        Log($"another host is already serving profile \"{profile}\" ({identity.Folder})");
        return 3;
    }
    // Started at logon there is no console; the app shows the end of this file.
    HostLog.OpenFile(Path.Combine(identity.Folder, "logs", "host.log"));
    var name = Option("--name") ?? HostConfigFile.ReadName(identity.Folder) ?? $"{Environment.MachineName}{(profile == "default" ? "" : $"/{profile}")}";
    var machine = new MachineInfo(Wire.DeviceId(identity.Key.Public), name, OsName(), HostVersion(), Dev: IsDevBuild() ? true : null);

    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    // One source for the life of the process: reconnecting to the relay neither rescans the
    // machine nor loses a change, and every new channel subscribes to the same state.
    await using var source = new SessionSource(options);
    await source.StartAsync(stop.Token);
    // Files over 256 KiB come from the relay's blob store, through whichever connection is up.
    using var relayBlobs = new RelayBlobs();
    var (answers, replies, attachments, stops) = Typists(source, Path.Combine(identity.Folder, "attachments"), Path.Combine(identity.Folder, "answer-attempts.json"), relayBlobs);
    using var attachmentsLifetime = attachments;
    // Notifications: the account's phones hear of questions and finished turns even with the
    // app closed. Subscriptions live in the profile's own folder, for this account only.
    await using var push = new PushNotifier(source, machine,
        PushStore.Load(Path.Combine(identity.Folder, "push-subscriptions.json"), identity.Account?.AccountId, Log), log: Log);
    push.Start();
    // Each agent's allowance, asked at the pace its sessions set, and never more often than
    // the idle pace while agent-ha-bridge is asking the same vendors for this user.
    using var usageHttp = new UsageHttp(TimeSpan.FromSeconds(15));
    var usageOptions = UsageOptions.For(options);
    var haProbe = OperatingSystem.IsWindows() ? new AgentHaBridgeProbe() : null;
    await using var usageMonitor = new UsageMonitor(UsageMonitor.Collectors(usageOptions, usageHttp, new CommandRunner(), log: Log), source, usageOptions,
        agentHaBridge: () => OperatingSystem.IsWindows() ? haProbe!.Active() : null, log: Log);
    usageMonitor.Start();
    // The Home Assistant connector (docs/ha-connector.md): this machine's sessions on the dashboard
    // agent-ha-bridge's writer generates. Off unless config.json turns it on, and paused while
    // agent-ha-bridge runs for this user (looked at every few seconds, as the app's view of it is).
    Func<string?>? haActive = haProbe is null ? null : HostControl.Cached(() => OperatingSystem.IsWindows() ? haProbe.Active() : null, () => Environment.TickCount64);
    await using var homeAssistant = HaRunning.Start(settings, identity.Folder, source, usageMonitor, haActive, IsDevBuild(), HostVersion(), Log, answers);
    var launcher = Launcher(source, attachments);
    var service = new SessionService(source, machine, Log, answers, replies, push, attachments, stops, new UsageService(usageMonitor, Log), launcher);
    // Who is on the other end of the pipe: the app beside this host, or something inside an
    // agent session, which may not manage it (§6.3 invariant 13).
    var inspector = new SystemProcessInspector();
    var callers = new CallerGuard(() => KnownAgents.Of(source, launcher, inspector), OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, CallerGuard.AppExecutable) : null);

    // The desktop app's way in (docs/protocol/local-pipe.md): the same sessions ops, and the
    // host's management, which only the pipe reaches. It runs whether or not this profile has
    // signed in to the relay.
    var wake = new SemaphoreSlim(0);
    CancellationTokenSource? connection = null;
    // The relay that issued the account in use: a sign-in during this run (the app's, or the
    // CLI's in another process) decides it, over --relay and AGENT_BRIDGE_RELAY, so a token
    // is never sent to a relay other than the one that gave it.
    Uri? signedInRelay = null;
    Uri? ServeRelay() => signedInRelay ?? RelayUri(identity);
    var control = new HostControl(new HostControlOptions
    {
        Profile = profile,
        Folder = identity.Folder,
        Version = HostVersion(),
        Dev = IsDevBuild(),
        Elevated = PeerPolicy.SelfElevated(),
        Machine = machine,
        Running = new HostSettingsView(settings.RemoteApproval, HostConfigFile.ReadName(identity.Folder)),
        LocalOnly = localOnly,
        Log = HostLog.FilePath,
        Account = () => identity.Account is { } a ? new HostAccount(a.Login, a.AccountId) : null,
        Relay = ServeRelay,
        AgentHaBridge = haProbe is null ? () => null : HostControl.Cached(() => OperatingSystem.IsWindows() ? haProbe.Active() : null, () => Environment.TickCount64),
        SignIn = async (relay, show, ct) =>
        {
            using var http = new HttpClient();
            return await SignIn.DeviceFlowAsync(http, relay, show, ct);
        },
        SignedIn = (result, relay) =>
        {
            identity.Save(result);
            File.WriteAllText(Path.Combine(identity.Folder, "relay.txt"), relay.ToString());
            signedInRelay = relay;
            // Whatever connection there is belongs to the account before; start over.
            connection?.Cancel();
            wake.Release();
        },
        Stop = () => stop.Cancel(),
        Restart = () =>
        {
            RestartSelf();
            stop.Cancel();
        },
    }, Log);
    LocalServer? local = null;
    try { local = LocalServer.Start(LocalEndpoint.For(identity.Folder), LocalApi.Handler(service, control, callers, Log), Log); }
    catch (EndpointInUseException e)
    {
        Log(e.Message);
        return 3;
    }
    // Anything else that keeps it from listening (a socket path too long for the OS, say)
    // costs this machine's apps their way in, not the host its relay.
    catch (Exception e) when (e is System.Net.Sockets.SocketException or ArgumentException or IOException or UnauthorizedAccessException)
    {
        Log($"the local API is off, so apps on this machine cannot reach this host: {e.Message}");
    }
    await using var localLifetime = local;
    Log($"watching {source.Snapshot().Count} live session(s) as \"{name}\" ({machine.Id[..8]}); local {(local is null ? "API off" : $"pipe {local.Endpoint.Address}")}; {push.Subscribers} device(s) to notify");

    var backoff = TimeSpan.FromSeconds(1);
    var reconnects = 0;
    var since = Stopwatch.StartNew();
    while (!stop.IsCancellationRequested)
    {
        var account = identity.Account;
        var relayUri = ServeRelay();
        if (localOnly || account is null || relayUri is null)
        {
            if (localOnly) { control.SetRelay(RelayState.Off, "this host serves this machine's apps only (--local-only)"); }
            else
            {
                control.SetRelay(RelayState.SignedOut);
                Log("not signed in: serving this machine's apps only until someone signs in (the app, or agent-bridge-host sign-in)");
            }
            if (!await WaitForSignInAsync()) { break; }
            continue;
        }
        using var current = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        connection = current;
        try
        {
            control.SetRelay(RelayState.Connecting, reconnects > 0 ? $"reconnecting (attempt {reconnects})" : null);
            await using var relay = await RelayClient.ConnectAsync(relayUri, account.Token, identity.Key, name, "host", current.Token);
            var hub = new ChannelHub(relay, identity.Key, name, "host")
            {
                JsonOptions = ProtocolJson.Options,
                Handler = service.HandleAsync,
            };
            hub.Accepted += ch => Log($"channel from {ch.PeerName} ({ch.PeerKind}, {ch.PeerId[..8]})");
            hub.Rejected += (peer, e) => Log($"rejected a frame from {peer[..Math.Min(8, peer.Length)]}: {e.Message}");
            push.Attach(new RelayPushSender(relay));
            relayBlobs.Attach(relay);
            Log($"connected as {relay.Login} on {relayUri.Host}{(reconnects > 0 ? $", reconnect {reconnects}" : "")}");
            control.SetRelay(RelayState.Connected, $"as {relay.Login}");
            backoff = TimeSpan.FromSeconds(1);
            since.Restart();
            await relay.Completion.WaitAsync(current.Token);
            Log($"connection ended after {since.Elapsed.TotalMinutes:F1} min: {(int?)relay.CloseStatus} {relay.CloseReason}");
            if ((int?)relay.CloseStatus == CloseCodes.Replaced)
            {
                // Not an exit: this machine's apps still need the host. A new sign-in or a
                // restart tries the relay again.
                Log("another process took over this device on the relay; not reconnecting until signed in again or restarted");
                control.SetRelay(RelayState.Refused, "another process took over this device on the relay");
                if (!await WaitForSignInAsync()) { break; }
                continue;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
        // A new sign-in replaced this connection's account: connect again with it now.
        catch (OperationCanceledException) when (current.IsCancellationRequested) { reconnects = 0; continue; }
        catch (RelayRefusedException e) when (e.Code is CloseCodes.AuthFailed or CloseCodes.ProofFailed)
        {
            Log($"refused: {e.Message}; sign in again");
            control.SetRelay(RelayState.Refused, $"the relay refused this device's sign-in ({e.Message}); sign in again");
            if (!await WaitForSignInAsync()) { break; }
            continue;
        }
        // Anything else the relay or network does to one connection is a reason to
        // reconnect, never to exit: the relay is not trusted, and a host that one bad frame
        // can stop is a denial of service.
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log($"connection failed: {e.GetType().Name}: {e.Message}");
            control.SetRelay(RelayState.Connecting, $"connection failed: {e.Message}");
        }
        finally
        {
            connection = null;
            relayBlobs.Attach(null);
        }
        reconnects++;
        // A sign-in during the wait goes straight to connecting.
        try { await wake.WaitAsync(backoff, stop.Token); }
        catch (OperationCanceledException) { break; }
        backoff = TimeSpan.FromSeconds(Math.Min(30, backoff.TotalSeconds * 2));
    }
    Log("stopped");
    return 0;

    // Until someone signs in: the app, through the pipe, wakes this at once; the CLI's sign-in
    // in another process only writes the profile's files, so they are looked at every few
    // seconds. False when the host is stopping.
    async Task<bool> WaitForSignInAsync()
    {
        while (true)
        {
            try { if (await wake.WaitAsync(TimeSpan.FromSeconds(5), stop.Token)) { return true; } }
            catch (OperationCanceledException) { return false; }
            if (identity.ReloadAccount())
            {
                signedInRelay = SavedRelay(identity) ?? signedInRelay;
                Log($"signed in as {identity.Account!.Login} by another process");
                return true;
            }
        }
    }
}

// Launching and resuming sessions from other devices (docs/host-launch.md): only in the folders
// the profile's config.json approves, and on Windows only for now. The agent starts in a console
// of its own on this desktop, so the host must run here, as the user, unelevated.
ISessionLauncher Launcher(ISessionSource source, AttachmentStore? attachments)
{
    foreach (var problem in settings.LaunchProblems) { Log($"config.json: {problem}"); }
    IAgentStarter starter = OperatingSystem.IsWindows() ? new WindowsAgentStarter() : new UnsupportedPlatformStarter();
    IAgentHaBridgeProbe probe = OperatingSystem.IsWindows() ? new AgentHaBridgeProbe() : new NoAgentHaBridge();
    IConsoleInjector injector = OperatingSystem.IsWindows() ? new WorkerInjector() : new NoScreens();
    var launcher = new LaunchService(source, probe, injector, starter, new AgentLocator(), new AgencyCatalog(new BatchFileRunner(new CommandRunner())), new LaunchServiceOptions
    {
        CopilotHome = options.CopilotHome,
        ClaudeHome = options.ClaudeHome,
        CodexHome = options.CodexHome,
        Config = settings.Launch,
        WorktreeUsage = new AgentWorktreeUsage(new SystemProcessInspector(), options.CopilotHome, options.ClaudeRegistrationRoots, options.CodexRegistrationRoots),
    }, Log, attachments);
    var why = !settings.Launch.Enabled ? "turned off (launch.enabled)" : starter.Unavailable() ?? probe.Active();
    var count = settings.Launch.Workspaces.Count;
    Log(why is null
        ? $"launching sessions is on: {count} workspace(s) approved{(count == 0 ? "; approve one here with agent-bridge-host workspace add <folder>" : $" ({string.Join(", ", settings.Launch.Workspaces.Select(w => w.Label + (w.Isolate ? " [worktrees]" : "")))})")}"
        : $"launching sessions is off for now: {why}");
    return launcher;
}

// host.restart: the same command again, without a window, which waits for this process to let
// go of the profile's lock before it starts.
void RestartSelf()
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var a in args) { start.ArgumentList.Add(a); }
    using var _ = Process.Start(start);
}

static string OsName() =>
    OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

// Answers and replies are typed through agent-bridge-injector beside this executable, on
// Windows only for now, and never while agent-ha-bridge runs for this user (checked again
// for every hello, answer, reply and stop; this is only the startup note). They share one
// gate, so an answer, a reply and ending a session (which types /exit) never act on one
// session at once. Files sent with replies are staged in the profile's own folder, private
// to this user (docs/host-replies.md), and the prompts already
// answered are recorded there too, so none is typed into twice.
(ISessionAnswerer, ISessionReplier, AttachmentStore?, ISessionStopper) Typists(ISessionSource source, string staging, string attempts, IBlobSource blobs)
{
    if (!OperatingSystem.IsWindows())
    {
        Log("answering, replying and ending sessions from other devices are not supported on this platform yet");
        return (new UnsupportedPlatformAnswerer(), new UnsupportedPlatformReplier(), null, new UnsupportedPlatformStopper());
    }
    var probe = new AgentHaBridgeProbe();
    var injector = new WorkerInjector();
    var guard = new TargetGuard();
    var processes = new SystemProcessInspector();
    var gate = new DeliveryGate();
    if (!File.Exists(Path.Combine(AppContext.BaseDirectory, WorkerInjector.FileName))) { Log($"{WorkerInjector.FileName} is missing beside the host; answers and replies will be refused"); }
    Log(probe.Active() is { } active ? $"answering, replying and ending sessions are off for now: {active}" : "answering, replying and ending sessions are on");
    if (settingsProblem is not null) { Log(settingsProblem); }
    Log($"approving Copilot permission prompts from other devices is {(settings.RemoteApproval ? "on" : "off")} (permissions.remoteApproval)");
    var attachments = new AttachmentStore(new AttachmentOptions { StagingRoot = staging }, Log, blobs: blobs);
    return (
        new AnswerService(source, processes, probe, guard, injector, new AnswerOptions { CopilotHome = options.CopilotHome, AttemptLedgerPath = attempts, CopilotRemoteApproval = settings.RemoteApproval }, Log, gate),
        new ReplyService(source, processes, probe, guard, injector, new ReplyOptions { CopilotHome = options.CopilotHome }, Log, gate, attachments),
        attachments,
        new StopService(source, processes, probe, injector, new WindowsProcessControl(), new StopOptions { CopilotHome = options.CopilotHome }, Log, gate));
}

static string HostVersion()
{
    var v = typeof(SessionService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    // The SDK appends "+<commit>"; the contract's hostVersion is the version alone.
    var plus = v.IndexOf('+');
    return plus < 0 ? v : v[..plus];
}

// A release build carries AgentBridgeRelease (set by -p:AgentBridgeRelease=true). Anything
// else was built from a working copy, and says so beside its version, because the version
// only moves when a release is cut: two hosts days apart in features would read the same.
static bool IsDevBuild() =>
    !typeof(SessionService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(a => a.Key == "AgentBridgeRelease" && a.Value == "true");

static void Log(string message) => HostLog.Write(message);

static string Describe(SessionEvent ev) => ev.Data switch
{
    Session s => $"{DateTimeOffset.UtcNow:HH:mm:ss.fff} upsert {s.Id} {s.Agent} {s.Status} since {s.StatusSince:HH:mm:ss} tool={s.Activity.CurrentTool?.Name ?? "-"} question={(s.Question is null ? "-" : s.Question.Kind.ToString())} \"{s.Title}\"",
    SessionRemoved r => $"{DateTimeOffset.UtcNow:HH:mm:ss.fff} remove {r.Id}",
    SessionsSynced y => $"{DateTimeOffset.UtcNow:HH:mm:ss.fff} synced revision {y.Revision}",
    _ => ev.Name,
};

// Off Windows a launch is refused before anything would read a screen or ask after agent-ha-bridge.
sealed class NoAgentHaBridge : IAgentHaBridgeProbe
{
    public string? Active() => null;
}

sealed class NoScreens : IConsoleInjector
{
    public Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default) =>
        Task.FromResult(new InjectionResult(InjectionStatus.NothingTyped, "not supported on this platform"));
}
