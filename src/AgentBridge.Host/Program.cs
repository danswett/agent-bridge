using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using AgentBridge.Hooks;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.DevBox;
using AgentBridge.Host.Core.Intake;
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
//   agent-bridge-host hooks status              hook handling on this machine: whether this
//                                               profile has taken over agent-ha-bridge's hook
//                                               spool, its heartbeat, and the hooks' timings
//                                               (docs/host-hooks.md)
//   agent-bridge-host migrate [--dry-run] | rollback [--dry-run] | migration status
//                                               move this machine's hooks and settings from
//                                               agent-ha-bridge to this host, and back
//                                               (docs/migration.md; Windows)
//   agent-bridge-host devbox status | dry-run   the Dev Box keep-awake serve runs (devBox.*): whether
//                                               this is a Dev Box and who keeps it awake, and one
//                                               pass that only reads (docs/host-config.md)
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
// serve reads what it may do on this machine from the profile's config.json (HostSettings), and
// so do ha shadow, which must see the sessions as serve would (permissions.remoteApproval), and
// usage, which reads what serve would (usage.*). usage only reads the file: it never makes the
// profile a device key, as loading its identity would. serve and ha shadow also read whether this
// profile has taken over agent-ha-bridge's hooks (IntakeState, docs/host-hooks.md).
var serveFolder = command == "serve" || (command == "ha" && arguments.ElementAtOrDefault(1) == "shadow") ? LoadIdentity().Folder : null;
var (settings, settingsProblem) = serveFolder is not null
    ? HostSettings.Load(serveFolder)
    : command is "usage" or "devbox" && Identity.IsValidProfile(Option("--profile") ?? "default")
        ? HostSettings.Load(Path.Combine(Identity.DefaultRoot, Option("--profile") ?? "default"))
        // An invalid profile is refused by the usage command itself, before anything is read.
        : (HostSettings.Default, null);
// sessions, get, watch and usage read a migrated installation's registrations as serve does, from
// the same profile; they look for its folder without creating one.
var profileName = Option("--profile") ?? "default";
var intakeFolder = serveFolder ?? (Identity.IsValidProfile(profileName) ? Path.Combine(Identity.DefaultRoot, profileName) : null);
var (intakeState, intakeProblem) = intakeFolder is not null && Directory.Exists(intakeFolder) ? IntakeState.Load(intakeFolder) : (null, null);
// A migrated installation's registrations are read where its runtime is, wherever that is.
string[] WithIntake(IReadOnlyList<string> roots, string folder) =>
    intakeState is null ? [.. roots] : [.. roots.Prepend(Path.Combine(intakeState.RuntimeRoot, folder)).Distinct(StringComparer.OrdinalIgnoreCase)];
var options = new SessionSourceOptions
{
    // A migrated installation's agent homes, where its hooks were re-pointed, unless told otherwise.
    CopilotHome = Option("--copilot-home") ?? intakeState?.Homes?.Copilot ?? defaults.CopilotHome,
    ClaudeHome = Option("--claude-home") ?? intakeState?.Homes?.Claude ?? defaults.ClaudeHome,
    ClaudeConfigDirExplicit = Option("--claude-home") is not null ? true
        : HookCommands.ClaudeModeFor(intakeState?.Homes, defaults, command == "hooks"),
    CodexHome = Option("--codex-home") ?? intakeState?.Homes?.Codex ?? defaults.CodexHome,
    ClaudeRegistrationRoots = WithIntake(defaults.ClaudeRegistrationRoots, SpoolContract.ClaudeFolder),
    CodexRegistrationRoots = WithIntake(defaults.CodexRegistrationRoots, SpoolContract.CodexFolder),
    CopilotRemoteApproval = settings.RemoteApproval,
    Log = command == "serve" ? Log : null,
    // Only serve keeps what it listed for its next start (#147); no other command reads or writes it.
    StateFile = command == "serve" && serveFolder is not null ? Path.Combine(serveFolder, "sessions-listed.json") : null,
};
var pretty = new JsonSerializerOptions(ProtocolJson.Options) { WriteIndented = !Flag("--compact") };
var agentLocator = new AgentLocator();

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
            // A profile is a folder name, never a path, as Identity.Load has it; read as the default
            // profile instead, a mistyped one would ask vendors with settings nobody wrote.
            if (!Identity.IsValidProfile(Option("--profile") ?? "default"))
            {
                Console.Error.WriteLine($"\"{Option("--profile")}\" is not a profile name: use letters, digits, '-', '_' and '.'");
                return 2;
            }
            // One ask of every agent, whatever the pace: what serve would report right now. What it
            // notices goes to stderr: stdout is the report, for whatever parses it.
            Action<string> note = m => Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} {m}");
            if (settingsProblem is not null) { note(settingsProblem); }
            foreach (var p in settings.UsageProblems) { note($"config.json: {p}"); }
            if (!settings.Usage.Publish)
            {
                note("usage is not read on this machine (usage.publish is off)");
                Console.WriteLine(JsonSerializer.Serialize(UsageReport.Empty, pretty));
                return 0;
            }
            await using var source = new SessionSource(options);
            await source.RefreshAsync();
            using var http = new UsageHttp(TimeSpan.FromSeconds(15));
            await using var monitor = NewUsageMonitor(source, http, note);
            // Every further account too: a pass reads one, so as many passes as there are.
            await monitor.PassAsync();
            for (var i = 1; i < settings.Usage.CopilotAccounts.Count; i++) { await monitor.PassAsync(); }
            Console.WriteLine(JsonSerializer.Serialize(monitor.Current.Report, pretty));
            return 0;
        }
    case "serve":
        {
            // Started by the logon task's launcher (#106), which then starts the replacement after
            // host.restart. Cleared, so nothing this host starts inherits it.
            var supervised = Environment.GetEnvironmentVariable(HostLauncherContract.Variable) == "1";
            Environment.SetEnvironmentVariable(HostLauncherContract.Variable, null);
            return await HostHandover.RunAsync(ServeAsync, RestartSelf, supervised);
        }
    case "ha" when arguments.ElementAtOrDefault(1) == "shadow":
        {
            await using var source = new SessionSource(options);
            await source.RefreshAsync();
            // One reading of every agent's usage, further Copilot accounts included, as the usage
            // command takes it, so the usage sensors are compared too. None when usage.publish is
            // off: serve would publish none either.
            using var http = new UsageHttp(TimeSpan.FromSeconds(15));
            Action<string> note = m => Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} {m}");
            await using var monitor = settings.Usage.Publish ? NewUsageMonitor(source, http, note) : null;
            if (monitor is not null)
            {
                await monitor.PassAsync();
                for (var i = 1; i < settings.Usage.CopilotAccounts.Count; i++) { await monitor.PassAsync(); }
            }
            return await HaCommands.ShadowAsync(LoadIdentity().Folder, source, Flag("--json"), Option("--window"), HostVersion(), IsDevBuild(), null, Console.Out, Console.Error,
                usage: monitor is null ? null : () => monitor.Current);
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
    case "hooks" when arguments.ElementAtOrDefault(1) == "status":
        // Read only: the profile's folder is looked in, never created, and no identity is loaded.
        if (intakeFolder is null) { Console.Error.WriteLine("agent-bridge-host: --profile is not a valid profile name"); return 2; }
        return HookCommands.Status(intakeFolder, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agent-ha-bridge", "runtime"), Console.Out, DateTimeOffset.Now);
    case "hooks":
        return await HookCommands.SetupAsync(arguments, Identity.DefaultRoot, options, Console.Out, Console.Error,
            () => Refused("change agent hook registrations"));
    case "migrate" or "rollback" or "migration":
        if (MigrateCommands.Writes(arguments) && Refused("migrate this machine or roll it back")) { return 4; }
        {
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            return await MigrateCommands.RunAsync(arguments, Identity.DefaultRoot, Console.Out, Console.Error, o => OperatingSystem.IsWindows()
                ? new AgentBridge.Host.Migration.Migrator(o, new AgentBridge.Host.Migration.WindowsAgentHaBridgeRuntime(), new AgentBridge.Host.Migration.LocalMigrationHost(o.ProfileFolder), new AgentBridge.Host.Migration.ProcessHookRunner())
                : throw new PlatformNotSupportedException(), stop.Token);
        }
    case "devbox":
        {
            if (!Identity.IsValidProfile(profileName)) { Console.Error.WriteLine($"\"{profileName}\" is not a profile name: use letters, digits, '-', '_' and '.'"); return 2; }
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            using var devCenter = new AgentBridge.Host.Core.DevBox.DevCenterHttp(TimeSpan.FromSeconds(30));
            return await DevBoxCommands.RunAsync(arguments, settings, settingsProblem, Path.Combine(Identity.DefaultRoot, profileName, "logs", "host.log"),
                FindDevBox, AgentHaBridgeKeepAwakeOwner, () => NewKeepAwake(devCenter), Console.Out, Console.Error, stop.Token);
        }
    default:
        Console.Error.WriteLine("usage: agent-bridge-host sessions [--compact] | get <id> | watch [--json] | usage | sign-in --relay <url> | serve [--relay <url>] [--name <name>] [--local-only] | whoami");
        Console.Error.WriteLine("       agent-bridge-host workspace list | workspace add <folder> [--label <label>] [--isolate] | workspace remove <label or folder> | config show | config set <key> <value> | config unset <key> | ha status | ha token set | ha token clear | ha shadow [--json] [--window <seconds>]");
        Console.Error.WriteLine("       " + AgentBridge.Host.Shadow.ShadowCommand.Usage);
        Console.Error.WriteLine("       agent-bridge-host hooks status | hooks install|remove <copilot|claude|codex> [...] [--dry-run]");
        Console.Error.WriteLine("       agent-bridge-host hooks cleanup [--profile <name>]");
        Console.Error.WriteLine("       agent-bridge-host migrate [--dry-run] [--bridge-home <dir>] [--hook <file>] | rollback [--dry-run] | migration status");
        Console.Error.WriteLine("       agent-bridge-host devbox status | devbox dry-run");
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
    Console.Error.WriteLine($"agent-bridge-host: refused to {what}: this is running {inside}. That is for a person to do, in a terminal of their own or from the {Product.App}.");
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

async Task<int> ServeAsync(HostHandover handover)
{
    var identity = LoadIdentity();
    var profile = Option("--profile") ?? "default";
    var localOnly = Flag("--local-only");
    // One host per profile on this machine. A host restarting itself (host.restart) starts its
    // replacement once it has let go of this (HostHandover); the wait covers a host something else
    // started just before.
    using var hostLock = HostLock.TryAcquire(identity.Folder, TimeSpan.FromSeconds(15));
    if (hostLock is null)
    {
        Log($"another host is already serving profile \"{profile}\" ({identity.Folder})");
        return 3;
    }
    // Started at logon there is no console; the app shows the end of this file.
    HostLog.OpenFile(Path.Combine(identity.Folder, "logs", "host.log"));
    var name = Option("--name") ?? HostConfigFile.ReadName(identity.Folder) ?? $"{Environment.MachineName}{(profile == "default" ? "" : $"/{profile}")}";
    // Instance: this run's own id. A client that lost an outcome sends the request again only to
    // the run that may have acted on it, which remembers the request; never to a fresh one.
    var machine = new MachineInfo(Wire.DeviceId(identity.Key.Public), name, OsName(), HostVersion(), Dev: IsDevBuild() ? true : null, Instance: Guid.NewGuid().ToString("N"));
    using var trustedPeers = new RemoteAuthorization(settings.TrustedPeers,
        new TrustedPeerStore(identity.Folder, identity.Key.Public, () => identity.Account?.AccountId,
            log: settings.TrustedPeers ? Log : null));
    if (trustedPeers.Enabled) { Log("trusted-peer enforcement is on; only explicit full-key pins grant remote authority"); }

    using var stop = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    // Who drives each session (the purple edge): one record for the host's own clients and the
    // Home Assistant cards, so both show the same.
    var drivers = new SessionDrivers();
    // One source for the life of the process: reconnecting to the relay neither rescans the
    // machine nor loses a change, and every new channel subscribes to the same state.
    // One process inspector for discovery and delivery: discovery's first pass, at start, is then
    // the scan a first reply would have paid for (137 ms cold, 6 ms warm, measured in #599), and
    // its command lines are already known.
    var processes = new SystemProcessInspector();
    await using var source = new SessionSource(options, processes, drivers: drivers);
    // Writes to transcripts as the source's watcher hears them, so a delivery confirms as soon as
    // the agent records what it typed rather than at its next poll.
    var writes = new TranscriptWrites();
    source.TranscriptWritten += writes.Written;
    await source.StartAsync(stop.Token);
    // Files over 256 KiB come from the relay's blob store, through whichever connection is up.
    using var relayBlobs = new RelayBlobs();
    // Set once the hook intake starts; until then, and until it has settled the markers, a Codex
    // approval on a migrated machine is not answered.
    HookIntake? runningIntake = null;
    var codexUnsettled = HookIntake.AnswerGate(intakeState?.RuntimeRoot, intakeProblem, () => runningIntake);
    // One G5 check for everything this host offers and does (design G5), so a hello asks it once
    // rather than once for each capability, and a burst of hellos once between them (#607). What
    // types, stops or launches still looks afresh, immediately before it acts.
    var haProbe = OperatingSystem.IsWindows() ? new AgentHaBridgeProbe() : null;
    var g5 = new SharedAgentHaBridgeProbe((IAgentHaBridgeProbe?)haProbe ?? new NoAgentHaBridge());
    var deliveryGate = new DeliveryGate();
    var (answers, replies, attachments, stops) = Typists(source, g5, processes, writes, Path.Combine(identity.Folder, "attachments"), Path.Combine(identity.Folder, "answer-attempts.json"), relayBlobs, codexUnsettled, deliveryGate);
    using var attachmentsLifetime = attachments;
    // Notifications: the account's phones hear of questions and finished turns even with the
    // app closed. Subscriptions live in the profile's own folder, for this account only. None at
    // all when push.enabled is off (the shadow host beside agent-ha-bridge, which already notifies
    // the phones): no subscription is taken and nothing is sent.
    await using var push = settings.Push
        ? new PushNotifier(source, machine, PushStore.Load(Path.Combine(identity.Folder, "push-subscriptions.json"), identity.Account?.AccountId, Log), log: Log, trust: trustedPeers)
        : null;
    if (push is not null)
    {
        trustedPeers.Changed += push.RevalidateTrust;
        push.RevalidateTrust();
    }
    push?.Start();
    // Each agent's allowance, asked at the pace its sessions set, and never more often than
    // the idle pace while agent-ha-bridge is asking the same vendors for this user; further Copilot
    // accounts at the idle pace. None at all when usage.publish is off: no vendor is asked, and
    // hello leaves usage out.
    using var usageHttp = new UsageHttp(TimeSpan.FromSeconds(15));
    foreach (var problem in settings.UsageProblems) { Log($"config.json: {problem}"); }
    await using var usageMonitor = settings.Usage.Publish
        ? NewUsageMonitor(source, usageHttp, Log, () => OperatingSystem.IsWindows() ? haProbe!.Active() : null, NewUsageProbes(identity.Folder))
        : null;
    Log(usageMonitor is null ? "agent usage is off (usage.publish)"
        : $"agent usage is on{(settings.Usage.CopilotAccounts.Count == 0 ? "" : $", with {settings.Usage.CopilotAccounts.Count} further Copilot account(s) (usage.copilotAccounts)")}");
    usageMonitor?.Start();
    // Other machines' readings, asked of the account's other hosts and kept in the profile's own
    // private folder, so an account's allowance is still shown once the machine that read it is
    // offline (#172). Only where this machine reports usage itself.
    var peerUsage = usageMonitor is null ? null
        : PeerUsage.Load(Path.Combine(identity.Folder, "usage"), machine.Id, identity.Account?.AccountId, log: Log);
    await using var usagePeers = peerUsage is null ? null : new UsagePeers(peerUsage, machine.Id, Log) { Trust = trustedPeers };
    if (usagePeers is not null)
    {
        trustedPeers.Changed += usagePeers.RevalidateTrust;
        usagePeers.RevalidateTrust();
    }
    usagePeers?.Start();
    // The Dev Box keep-awake (docs/host-config.md, #47): on a Dev Box, a pass soon after starting and
    // then every devBox.intervalMinutes while devBox.keepAwake is on, standing back while
    // agent-ha-bridge's own keep-awake task is on. Off Windows there is no Dev Box.
    foreach (var problem in settings.DevBoxProblems) { Log($"config.json: {problem}"); }
    using var devCenter = new DevCenterHttp(TimeSpan.FromSeconds(30));
    await using var keepAwake = OperatingSystem.IsWindows() ? NewKeepAwake(devCenter) : null;
    keepAwake?.Start();
    // The Home Assistant connector (docs/ha-connector.md): this machine's sessions on the dashboard
    // agent-ha-bridge's writer generates. Off unless config.json turns it on, and paused while
    // agent-ha-bridge runs for this user (looked at every few seconds, as the app's view of it is).
    Func<string?>? haActive = haProbe is null ? null : HostControl.Cached(() => OperatingSystem.IsWindows() ? haProbe.Active() : null, () => Environment.TickCount64);
    // Ended sessions moved between the account's machines, on commands a person's device signs,
    // through the relay's blob store (docs/host-transfer.md). Bundles wait in the profile's own
    // private folder, never a shared temporary one.
    using var transfers = new SessionTransfers(identity.Key, Path.Combine(identity.Folder, "transfers"), Log) { Trust = trustedPeers };
    var launcher = Launcher(source, g5, attachments, transfers, deliveryGate);
    transfers.Launcher = launcher;
    // Folder-trust questions answered by a person from their devices, where config.json allows it.
    var trustAnswers = new FolderTrustAnswers(identity.Key, launcher, Log) { Trust = trustedPeers };
    // Before the connector, which launches from the card through it.
    // The host's own updates, from the machine's update entity: through the app's package, which
    // carries the host (Windows only: elsewhere nothing packages it yet). Installing ends this
    // serve as Ctrl+C would, once the release is handed to the updater. None on a read-only host
    // (delivery.readOnly), which acts on nothing from another device: the buttons are withdrawn.
    var hostUpdates = VelopackHostUpdates.For(ReadOnlyReason(), OperatingSystem.IsWindows(),
        () => new VelopackHostUpdates(Path.GetDirectoryName(identity.Folder) ?? Identity.DefaultRoot, profile, () => stop.Cancel(), Log));
    // Home Assistant's refusals of this machine, counted with every other process of this user so a
    // rejected token is never repeated into a ban (#227); the app shows what it says.
    var haAuth = new AgentBridge.HomeAssistant.HaAuthGuard(AgentBridge.HomeAssistant.HaAuthGuard.FileFor(identity.Folder), log: Log);
    await using var homeAssistant = HaRunning.Start(settings, identity.Folder, source, usageMonitor, haActive, IsDevBuild(), HostVersion(), Log, answers,
        HaReplies.For(replies, attachments), stops, launcher, hostUpdates, drivers, haAuth);
    // What takes answering, replying, ending and launching away while it runs (design G5).
    // Asked afresh by what acts on it (updates, the readiness checks, the app's status).
    string? ServeReadOnly() => ReadOnlyReason() ?? g5.Active();
    // The same for what hello says, from the answer the delivery services offer from: a hello
    // holds one (SessionService.G5), so it never says read-only beside actions offered, or offers
    // none without saying why.
    string? OfferedReadOnly() => ReadOnlyReason() ?? g5.Recent();
    // Updates asked for from the account's other devices, which the app beside this host installs
    // (#51, docs/protocol/v1-update.md). A read-only machine serves none, as #112 withdrew Home
    // Assistant's Install there.
    var updates = new UpdateService(HostVersion(), Log) { ReadOnly = ServeReadOnly, OfferedReadOnly = OfferedReadOnly };
    var service = new SessionService(source, machine, Log, answers, replies, push, attachments, stops, usageMonitor is null ? null : new UsageService(usageMonitor, Log, peerUsage), launcher, transfers)
    {
        Drivers = drivers,
        // Said in hello so a phone shows the machine read-only and why.
        ReadOnly = OfferedReadOnly,
        G5 = g5,
        TrustAnswers = trustAnswers,
        Updates = updates,
    };
    // Who is on the other end of the pipe: the app beside this host, or something inside an
    // agent session, which may not manage it (§6.3 invariant 13).
    var inspector = new SystemProcessInspector();
    var callers = new CallerGuard(() => KnownAgents.Of(source, launcher, inspector), OperatingSystem.IsWindows() ? Path.Combine(AppContext.BaseDirectory, CallerGuard.AppExecutable) : null,
        hostImage: OperatingSystem.IsWindows() ? Environment.ProcessPath : null);
    // On a machine migrated from agent-ha-bridge, the hooks hand their events to this host
    // (docs/host-hooks.md). Its notices are there for the Home Assistant connector to subscribe to.
    // Migrated agents run a copy of the hook beside this host; an app update brings a new one.
    if (intakeState is { Standalone: true } && OperatingSystem.IsWindows())
    {
        try { StandaloneHooks.Refresh(serveFolder!, Path.Combine(AppContext.BaseDirectory, "agent-bridge-host-hook.exe")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log($"the fresh installation's hook could not be refreshed: {e.Message}"); }
    }
    else if (intakeState is not null && OperatingSystem.IsWindows()
        && AgentBridge.Host.Migration.Migrator.RefreshHook(serveFolder!, Path.Combine(AppContext.BaseDirectory, "agent-bridge-host-hook.exe")) is { } refreshed)
    {
        Log(refreshed);
    }
    await using var intake = StartIntake(haProbe, stop.Token);
    runningIntake = intake;

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
    // The account with the relay that issued it, replaced whole after each sign-in, so the app
    // reading it (host.relay.account) never gets a new token with the old relay's address.
    RelayAccount? credential = null;
    void PublishCredential() => Volatile.Write(ref credential,
        identity.Account is { } a && ServeRelay() is { } r ? new RelayAccount(r.ToString(), a.Token, a.AccountId, a.Login) : null);
    PublishCredential();
    // agent-ha-bridge's state for the app's status, looked for at most every few seconds.
    var agentHaCached = haProbe is null ? () => null : HostControl.Cached(() => OperatingSystem.IsWindows() ? haProbe.Active() : null, () => Environment.TickCount64);
    var agentHomes = new IntakeState.AgentHomes(options.CopilotHome, options.ClaudeHome, options.CodexHome)
    {
        ClaudeConfigDirExplicit = options.ClaudeConfigDirExplicit,
    };
    var codexEnvironment = new Dictionary<string, string> { ["CODEX_HOME"] = agentHomes.Codex };
    var standaloneHooks = OperatingSystem.IsWindows() ? new StandaloneHooks(identity.Folder,
        agentHomes, Path.Combine(AppContext.BaseDirectory, "agent-bridge-host-hook.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agent-ha-bridge"),
        ServeReadOnly, agentLocator, new BatchFileRunner(new CommandRunner(codexEnvironment), codexEnvironment)) : null;
    var agentSetup = standaloneHooks is null ? null : new AgentSetupService(standaloneHooks, ServeReadOnly, stop.Token);
    await using var agentInstall = standaloneHooks is null ? null : new AgentInstallService(identity.Folder,
        agentLocator, agentLocator.Refresh, new VersionCommandRunner(),
        () =>
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
            return File.Exists(path) ? path : null;
        },
        agent =>
        {
            if (ServeReadOnly() is { } why) { throw new InvalidOperationException($"CLI installation is unavailable while this host is read-only: {why}"); }
            standaloneHooks.CheckInstall(agent);
        }, stop.Token);
    await using var agentReadiness = new AgentReadinessService(identity.Folder, agentHomes,
        settings.Launch, agentLocator, new VersionCommandRunner(), ServeReadOnly, intake);
    var control = new HostControl(new HostControlOptions
    {
        Profile = profile,
        Folder = identity.Folder,
        Version = HostVersion(),
        Dev = IsDevBuild(),
        Elevated = PeerPolicy.SelfElevated(),
        Machine = machine,
        Running = HostControl.Settings(settings, HostConfigFile.ReadName(identity.Folder)),
        LocalOnly = localOnly,
        Log = HostLog.FilePath,
        ApplyRemoteFolderTrust = on =>
        {
            launcher.RemoteFolderTrust = on;
            Log($"folder-trust questions from devices: {(on ? "on" : "off")} (set by the app; applies now)");
        },
        ApplyRemoteFolderTrustDevices = ids => launcher.TrustDevices = ids,
        ApplyDevBoxKeepAwake = keepAwake is null ? null : on => keepAwake.Enabled = on,
        DevBox = () => DevBoxCommands.Status(keepAwake?.State),
        Updates = updates,
        Account = () => identity.Account is { } a ? new HostAccount(a.Login, a.AccountId) : null,
        Credential = () => Volatile.Read(ref credential),
        Relay = ServeRelay,
        AgentHaBridge = agentHaCached,
        HomeAssistant = () => haAuth.Problem,
        ReadOnly = () => ReadOnlyReason() ?? agentHaCached(),
        ReadOnlyNow = ServeReadOnly,
        Agents = agentReadiness,
        AgentSetup = agentSetup,
        AgentInstall = agentInstall,
        Lifecycle = new LifecycleCoordinator(deliveryGate, new DepartureConditions()),
        SignIn = async (relay, show, ct) =>
        {
            using var http = new HttpClient();
            return await SignIn.DeviceFlowAsync(http, relay, show, ct);
        },
        SignedIn = (result, relay) =>
        {
            // Before anything says the new account: other machines' readings kept for the one before
            // are never served under it, even while the relay reconnects.
            peerUsage?.Bind(result.AccountId);
            identity.Save(result);
            File.WriteAllText(Path.Combine(identity.Folder, "relay.txt"), relay.ToString());
            signedInRelay = relay;
            PublishCredential();
            // Whatever connection there is belongs to the account before; start over.
            connection?.Cancel();
            wake.Release();
        },
        Stop = () => stop.Cancel(),
        // Done with any usage probe tied to this host before it stops, so a standalone Codex probe
        // is not cut mid-refresh (#238); the replacement starts once it has (HostHandover).
        Restart = () => _ = Task.Run(async () =>
        {
            if (usageMonitor is not null) { await usageMonitor.SettleProbesAsync(TimeSpan.FromSeconds(90)); }
            handover.Request();
            stop.Cancel();
        }),
        HoldProbes = () => usageMonitor?.HoldProbes(TimeSpan.FromMinutes(15)) == true
            ? "Codex's app-server, started to read its allowance, is still answering or finishing" : null,
    }, Log);
    agentReadiness.Start();
    // A pass, or keep-awake turned on or off, changes what the app's Settings shows.
    if (keepAwake is not null) { keepAwake.Changed += control.Refresh; }
    haAuth.Changed += control.Refresh;
    LocalServer? local = null;
    try { local = LocalServer.Start(LocalEndpoint.For(identity.Folder), LocalApi.Handler(service, control, callers, Log, trustedPeers), Log); }
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
    // Stopped from the app while starting, before anything could reach this through the pipe
    // (HostHandover.StoppedFromTheApp, #106's review): not served at all.
    if (handover.StoppedFromTheApp(identity.Folder))
    {
        Log($"stopped from the {Product.App} while starting; not serving");
        return 0;
    }
    Log($"watching {source.Snapshot().Count} live session(s) as \"{name}\" ({machine.Id[..8]}); local {(local is null ? "API off" : $"pipe {local.Endpoint.Address}")}; {(push is null ? "notifications off (push.enabled)" : $"{push.Subscribers} device(s) to notify")}");

    var backoff = new ReconnectBackoff();
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
        int? endedWith = null;
        string? endedWhy = null;
        try
        {
            control.SetRelay(RelayState.Connecting, reconnects > 0 ? $"reconnecting (attempt {reconnects})" : null);
            await using var relay = await RelayClient.ConnectAsync(relayUri, account.Token, identity.Key, name, "host", current.Token);
            var hub = new ChannelHub(relay, identity.Key, name, "host", trustedPeers.Allows)
            {
                JsonOptions = ProtocolJson.Options,
                Handler = trustedPeers.Handler(service),
                // Answered from an IK first message (#603), which a relay can replay: hello only
                // reads, so running it again changes nothing.
                EarlyOps = [Ops.Hello],
            };
            trustedPeers.Attach(hub);
            hub.Accepted += ch => Log($"channel from {HostConfigFile.CleanName(ch.PeerName)} ({HostConfigFile.CleanName(ch.PeerKind)}, {ch.PeerId[..8]})");
            hub.Rejected += (peer, e) => Log($"rejected a frame from {peer[..Math.Min(8, peer.Length)]}: {e.Message}");
            push?.Attach(new RelayPushSender(relay));
            relayBlobs.Attach(relay);
            transfers.Attach(relay, hub);
            usagePeers?.Attach(relay, hub);
            trustAnswers.Attach(relay);
            updates.Attach(relay);
            Log($"connected as {relay.Login} on {relayUri.Host}{(reconnects > 0 ? $", reconnect {reconnects}" : "")}");
            control.SetRelay(RelayState.Connected, $"as {relay.Login}");
            backoff.Reset();
            since.Restart();
            await relay.Completion.WaitAsync(current.Token);
            (endedWith, endedWhy) = ((int?)relay.CloseStatus, relay.CloseReason);
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
        // can stop is a denial of service. That includes a cancellation neither token above
        // asked for: the socket's own keep-alive aborts with one, and on DSWETT-DEV-VM1 one
        // that got this far took the host down for good, minutes after a relay deploy (#40).
        catch (Exception e)
        {
            // A relay restarting before ready (1012) still spreads this retry, as after one.
            if (e is RelayRefusedException refused) { (endedWith, endedWhy) = (refused.Code, refused.Reason); }
            Log($"connection failed: {e.GetType().Name}: {e.Message}");
            control.SetRelay(RelayState.Connecting, $"connection failed: {e.Message}");
        }
        finally
        {
            connection = null;
            trustedPeers.Attach(null);
            relayBlobs.Attach(null);
            transfers.Attach(null, null);
            usagePeers?.Attach(null, null);
            trustAnswers.Attach(null);
            updates.Attach(null);
        }
        reconnects++;
        // A sign-in during the wait goes straight to connecting.
        try { await wake.WaitAsync(backoff.Next(endedWith, endedWhy), stop.Token); }
        catch (OperationCanceledException) { break; }
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
                peerUsage?.Bind(identity.Account!.AccountId);
                signedInRelay = SavedRelay(identity) ?? signedInRelay;
                PublishCredential();
                Log($"signed in as {identity.Account!.Login} by another process");
                return true;
            }
        }
    }
}

// Launching and resuming sessions from other devices (docs/host-launch.md): only in the folders
// the profile's config.json approves, and on Windows only for now. The agent starts in a console
// of its own on this desktop, so the host must run here, as the user, unelevated.
LaunchService Launcher(ISessionSource source, IAgentHaBridgeProbe g5, AttachmentStore? attachments, SessionTransfers transfers, DeliveryGate deliveryGate)
{
    foreach (var problem in settings.LaunchProblems) { Log($"config.json: {problem}"); }
    IAgentStarter starter = OperatingSystem.IsWindows() ? new WindowsAgentStarter() : new UnsupportedPlatformStarter();
    IAgentHaBridgeProbe probe = new ReadOnlyGate(g5, ReadOnlyReason());
    IConsoleInjector injector = OperatingSystem.IsWindows() ? new WorkerInjector() : new NoScreens();
    var launcher = new LaunchService(deliveryGate, source, probe, injector, starter, agentLocator, new AgencyCatalog(new BatchFileRunner(new CommandRunner())), new LaunchServiceOptions
    {
        CopilotHome = options.CopilotHome,
        ClaudeHome = options.ClaudeHome,
        ClaudeConfigDirExplicit = options.ClaudeConfigDirExplicit,
        CodexHome = options.CodexHome,
        Config = settings.Launch,
        WorktreeUsage = new AgentWorktreeUsage(new SystemProcessInspector(), options.CopilotHome, options.ClaudeRegistrationRoots, options.CodexRegistrationRoots),
        Transfers = transfers,
        TransferStaging = transfers.Staging,
    }, Log, attachments);
    var why = !settings.Launch.Enabled ? "turned off (launch.enabled)" : starter.Unavailable() ?? probe.Active();
    var count = settings.Launch.Workspaces.Count;
    Log(why is null
        ? $"launching sessions is on: {count} workspace(s) approved{(count == 0 ? "; approve one here with agent-bridge-host workspace add <folder>" : $" ({string.Join(", ", settings.Launch.Workspaces.Select(w => w.Label + (w.Isolate ? " [worktrees]" : "")))})")}"
        : $"launching sessions is off for now: {why}");
    return launcher;
}

// Each agent's allowance as config.json's usage section says to read it: at its pace, with its
// further Copilot accounts.
UsageMonitor NewUsageMonitor(ISessionSource source, IUsageHttp http, Action<string> log, Func<string?>? agentHaBridge = null, UsageProbes? probes = null)
{
    var usageOptions = UsageOptions.For(options, settings.Usage);
    return new UsageMonitor(UsageMonitor.Collectors(usageOptions, http, new CommandRunner(), log: log, accounts: settings.Usage.CopilotAccounts, probes: probes), source, usageOptions,
        agentHaBridge: agentHaBridge, log: log);
}

// What lets Claude Code and Codex read their own allowance (#200): only for serve, which is there
// to see a probe through. Each agent is found as a launch finds it, with the config's path for it.
UsageProbes NewUsageProbes(string folder)
{
    var locator = new AgentLocator();
    return new UsageProbes
    {
        Launcher = new ProbeLauncher(),
        Processes = new SystemProcessInspector(),
        Find = agent => Launchers.Find(agent.ToString().ToLowerInvariant()) is { } l ? locator.Find(l, settings.Launch.Agent(l.Id)) : null,
        WorkingDirectory = Path.Combine(folder, "usage-probe"),
        ClientVersion = HostVersion(),
        // Beside every profile, so hosts serving different bridge profiles share it (#238).
        Lease = key => new FileKeepAwakeLock(Path.Combine(Identity.DefaultRoot, "usage-probes", key + ".lock")).TryTake(),
    };
}

// The Dev Box keep-awake as config.json's devBox section says to run it (docs/host-config.md): az
// through the windowless runner (az.cmd through cmd.exe), one pass at a time for this user, and
// never while agent-ha-bridge's own keep-awake is on.
KeepAwakeService NewKeepAwake(IDevCenterHttp http) => new(new KeepAwakeOptions
{
    Settings = settings.DevBox,
    FindIdentity = FindDevBox,
    Token = new AzCliToken(new BatchFileRunner(new CommandRunner()), AzCliToken.Find),
    Http = http,
    Lock = OperatingSystem.IsWindows()
        ? new KeepAwakeLocks(new FileKeepAwakeLock(Path.Combine(Identity.DefaultRoot, "devbox-keepawake.lock")),
            new DelegateKeepAwakeLock(() => OperatingSystem.IsWindows() ? new AgentHaBridgeProbe().HoldKeepAwake() : null))
        : new FileKeepAwakeLock(Path.Combine(Identity.DefaultRoot, "devbox-keepawake.lock")),
    AgentHaBridgeKeepAwake = AgentHaBridgeKeepAwakeOwner,
}, Log);

// Dev Boxes are Windows machines: anywhere else this is not one.
static (DevBoxIdentity? Identity, string? Problem) FindDevBox() =>
    OperatingSystem.IsWindows() ? DevBoxIdentity.Find() : (null, null);

static KeepAwakeOwner? AgentHaBridgeKeepAwakeOwner() => OperatingSystem.IsWindows() ? new AgentHaBridgeProbe().KeepAwakeActive() : null;

// host.restart: the same command again, without a window, started once this process has stopped
// serving and let go of the profile's lock (HostHandover). Only for a host the logon task's
// launcher does not watch; a watched one leaves this to the launcher.
void RestartSelf()
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var a in args) { start.ArgumentList.Add(a); }
    using var _ = ProcessLaunch.Start(start);
}

// Hook intake (docs/host-hooks.md): only for a profile that has migrated from agent-ha-bridge,
// and only where agent-ha-bridge's daemon can be seen, since the intake must stand back
// whenever it runs (G5). Null when off.
HookIntake? StartIntake(AgentHaBridgeProbe? probe, CancellationToken cancellationToken)
{
    if (intakeProblem is not null) { Log(intakeProblem); }
    if (intakeState is null) { return null; }
    if (!OperatingSystem.IsWindows() || probe is null)
    {
        Log("hook intake is off: this platform cannot yet tell whether agent-ha-bridge is running, which it must know before touching the hook spool");
        return null;
    }
    var active = HostControl.Cached(() => OperatingSystem.IsWindows() ? probe.Active() : "not Windows", () => Environment.TickCount64, everyMs: 2000);
    var intake = new HookIntake(new HookIntakeOptions
    {
        RuntimeRoot = intakeState.RuntimeRoot,
        ClaudeHome = options.ClaudeHome,
        AgentHaBridgeActive = active,
    }, new SystemProcessInspector(), log: Log);
    intake.Start(cancellationToken);
    var owner = intakeState.Standalone ? "this fresh installation" : $"agent-ha-bridge installation {intakeState.InstallationId}";
    Log($"hook intake is on: taking {owner}'s hook spool in {intakeState.RuntimeRoot}{(active() is { } why ? $", once it stops ({why})" : "")}");
    return intake;
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
(ISessionAnswerer, ISessionReplier, AttachmentStore?, ISessionStopper) Typists(ISessionSource source, IAgentHaBridgeProbe g5, IProcessInspector processes, TranscriptWrites writes, string staging, string attempts, IBlobSource blobs, Func<string, string?>? codexUnsettled, DeliveryGate gate)
{
    if (!OperatingSystem.IsWindows())
    {
        Log("answering, replying and ending sessions from other devices are not supported on this platform yet");
        return (new UnsupportedPlatformAnswerer(), new UnsupportedPlatformReplier(), null, new UnsupportedPlatformStopper());
    }
    var probe = new ReadOnlyGate(g5, ReadOnlyReason());
    var injector = new WorkerInjector();
    var guard = new TargetGuard();
    if (!File.Exists(Path.Combine(AppContext.BaseDirectory, WorkerInjector.FileName))) { Log($"{WorkerInjector.FileName} is missing beside the host; answers and replies will be refused"); }
    Log(probe.Active() is { } active ? $"answering, replying and ending sessions are off for now: {active}" : "answering, replying and ending sessions are on");
    if (settingsProblem is not null) { Log(settingsProblem); }
    Log($"approving Copilot permission prompts from other devices is {(settings.RemoteApproval ? "on" : "off")} (permissions.remoteApproval)");
    var attachments = new AttachmentStore(new AttachmentOptions { StagingRoot = staging }, Log, blobs: blobs);
    return (
        new AnswerService(gate, source, processes, probe, guard, injector, new AnswerOptions { CopilotHome = options.CopilotHome, AttemptLedgerPath = attempts, CopilotRemoteApproval = settings.RemoteApproval, CodexApprovalsUnsettled = codexUnsettled, Writes = writes }, Log),
        new ReplyService(gate, source, processes, probe, guard, injector, new ReplyOptions { CopilotHome = options.CopilotHome, Writes = writes }, Log, attachments),
        attachments,
        new StopService(gate, source, processes, probe, injector, new WindowsProcessControl(), new StopOptions { CopilotHome = options.CopilotHome }, Log));
}

// Why this host takes nothing from other devices whatever agent-ha-bridge does, or null: made
// read-only in its config (delivery.readOnly), or a config.json it could not read, which might
// have said so.
string? ReadOnlyReason() => !settings.ReadOnly ? null : settingsProblem is null ? HostSettings.ReadOnlyReason : HostSettings.ProblemReason;

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
