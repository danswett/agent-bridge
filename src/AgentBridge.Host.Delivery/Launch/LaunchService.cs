using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery.Launch;

// launch.options and session.launch for a host: what hello offers, the options, and one launch.
public interface ISessionLauncher
{
    // Whether hello offers sessions.launch now; asked per hello, like answers.
    bool Available { get; }

    Task<LaunchOptions> OptionsAsync(CancellationToken cancellationToken = default);

    Task<LaunchResult> LaunchAsync(LaunchRequest request, string peer, CancellationToken cancellationToken = default);

    // The agents launches started that still run, registered or not: whatever runs inside one
    // is an agent's, and may not manage the host (§6.3 invariant 13).
    IReadOnlyList<Started> LaunchedProcesses() => [];

    // launch.trust.answer, for a person whose signed command the caller has checked: answers a
    // folder-trust question a launched agent is waiting on (trust, or quit).
    Task<TrustAnswerResult> AnswerTrustAsync(Guid request, string question, bool trust, string device, string by, CancellationToken cancellationToken = default) =>
        Task.FromResult(new TrustAnswerResult(TrustAnswerOutcome.Refused, "this machine does not answer folder-trust questions from other devices; answer at the machine"));

    // Whether it takes launch.trust.answer at all (hello's launch.trust).
    bool AnswersTrust => false;

    // The devices approved at the machine to answer from (LaunchService.TrustDevices).
    IReadOnlyCollection<string> TrustDevices => [];
}

public sealed class LaunchServiceOptions
{
    public required string CopilotHome { get; init; }
    public required string ClaudeHome { get; init; }
    public required string CodexHome { get; init; }
    public LaunchConfig Config { get; init; } = LaunchConfig.Default;
    public bool PlatformSupported { get; init; } = OperatingSystem.IsWindows();

    // How long a launch request waits for the session to register before it answers
    // unconfirmed. Copilot registers in seconds; a first launch that has to sign in can take
    // minutes, and is followed on after the answer.
    public TimeSpan RegistrationWait { get; init; } = TimeSpan.FromSeconds(60);

    // How long a launch is followed in all, after which the host stops looking: the backstop
    // agent-ha-bridge's Update-DaemonPendingLaunch keeps.
    public TimeSpan FollowFor { get; init; } = TimeSpan.FromMinutes(10);

    // How long one is followed while its folder-trust question waits, when a person may answer
    // it from their devices (launch.remoteFolderTrust): they may not see it at once.
    public TimeSpan TrustFollowFor { get; init; } = TimeSpan.FromMinutes(30);

    // After a folder-trust answer is typed, how long the session is given to be seen started
    // (trust) or the agent to close (quit) before the answer reads unconfirmed. A first Codex
    // turn writes its session only once the model answers.
    public TimeSpan TrustConfirmWait { get; init; } = TimeSpan.FromSeconds(60);

    // How long a trust question keeps other launches busy: agent-ha-bridge's two minutes.
    public TimeSpan TrustBusyFor { get; init; } = TimeSpan.FromSeconds(120);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    // How long the window is given to draw before it is read, and how often it is read after.
    public TimeSpan ScreenAfter { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ScreenEvery { get; init; } = TimeSpan.FromSeconds(1);

    // How long a session that registers before its window is ready (Copilot) is given to show
    // it ready, or ask a startup question, before its launch answers unconfirmed.
    public TimeSpan ReadyWait { get; init; } = TimeSpan.FromSeconds(20);

    // How long a started process that exited is given for its session to show, for a launcher
    // that hands the session on to another process and exits.
    public TimeSpan ExitGrace { get; init; } = TimeSpan.FromSeconds(3);

    // After a launch settles. A client resends a transfer launch for up to 20 minutes from its
    // first send (pwa/js/launch-service.js), and settling comes after that send, so 25 minutes
    // means a resend always finds the outcome rather than starting the launch again, which for a
    // transfer would be refused (its command is spent) and read as not launched.
    public TimeSpan RememberFor { get; init; } = TimeSpan.FromMinutes(25);

    public TimeProvider Time { get; init; } = TimeProvider.System;

    // Folders no session is launched in (Folders.IsSystemDirectory). Temp is one, which is
    // where tests keep their workspaces, so they replace it.
    public Func<string, bool> IsSystemDirectory { get; init; } = Folders.IsSystemDirectory;

    // The clock a worktree's name is stamped from: local time, as agent-ha-bridge names them.
    public Func<DateTime> LocalNow { get; init; } = () => DateTime.Now;

    // Which folders live sessions work in, asked before a finished worktree is removed. Null
    // is "cannot tell", which removes none.
    public IWorktreeUsage? WorktreeUsage { get; init; }

    // Brings sessions from the account's other machines (session.launch's transfer). Null: none.
    public ISessionFetcher? Transfers { get; init; }

    // A private folder of this host's where a fetched bundle is unpacked before it is installed.
    public string? TransferStaging { get; init; }
}

// Launches and resumes agent sessions as agent-ha-bridge's Launch button does
// (hooks/daemon-launch.ps1, Use-DaemonLaunchRequest, Resolve-DaemonRequestedLaunch,
// Start-DaemonLaunch, Update-DaemonPendingLaunch; hooks/session-launch.ps1,
// Start-BridgeCopilotSession), for a request that carries every setting and the whole prompt:
//   * Only approved folders: a new session starts in a workspace a person listed in this
//     host's config at the machine, and a resumed one only if its own folder is in one (or in a
//     worktree this host made for one). A workspace marked isolate starts each new session in
//     a git worktree of its own, and fails closed (Worktrees).
//   * Every setting is checked against what launch.options offers before anything starts; a
//     value nobody offered is refused, never passed on.
//   * The agent starts in a visible console of its own on the person's desktop, unelevated
//     (IAgentStarter), and the launch is bound to the session by the id it was given (Codex,
//     which picks its own, by the first new Codex session after the start). The answer waits
//     for that session to register: started, with its id.
//   * A question the agent asks before it registers (whether to trust the folder) is never
//     answered from here: only a person at the machine answers it. While one waits, other
//     launches are busy, so a second press is never taken for an answer. Copilot registers
//     before it asks, so its launch is started only once its window shows it ready.
//   * Never while agent-ha-bridge is active for this user (G5), and never twice for one request
//     (OutcomeMemory). One start at a time, so worktrees and Codex's binding cannot race.
public sealed partial class LaunchService : ISessionLauncher
{
    readonly ISessionSource source;
    readonly IAgentHaBridgeProbe probe;
    readonly IConsoleInjector injector;
    readonly IAgentStarter starter;
    readonly IAgentLocator locator;
    readonly AgencyCatalog agency;
    readonly ResumableStore resumable;
    readonly LaunchServiceOptions options;
    readonly Action<string> log;
    readonly AttachmentStore? attachments;
    readonly OutcomeMemory<LaunchResult> requests;
    readonly OutcomeMemory<TrustAnswerResult> trustAnswers;
    readonly SemaphoreSlim starting = new(1, 1);
    readonly Lock pendingGate = new();
    readonly List<Pending> pending = [];
    // Every session id a launch has started an agent on (given, resumed, or the Codex session
    // bound to it), with its process: while that process runs, the session is not resumed
    // again, even after its launch has stopped being followed, and a Codex session bound to
    // one launch is never bound to another.
    readonly Dictionary<string, Started> launchedOn = new(StringComparer.OrdinalIgnoreCase);

    public LaunchService(ISessionSource source, IAgentHaBridgeProbe probe, IConsoleInjector injector, IAgentStarter starter, IAgentLocator locator,
        AgencyCatalog agency, LaunchServiceOptions options, Action<string>? log = null, AttachmentStore? attachments = null)
    {
        this.source = source;
        this.probe = probe;
        this.injector = injector;
        this.starter = starter;
        this.locator = locator;
        this.agency = agency;
        this.options = options;
        this.log = log ?? (_ => { });
        this.attachments = attachments;
        resumable = new ResumableStore(options.CopilotHome, options.ClaudeHome, options.CodexHome, options.Time);
        requests = new OutcomeMemory<LaunchResult>(options.Time, options.RememberFor);
        trustAnswers = new OutcomeMemory<TrustAnswerResult>(options.Time, options.RememberFor);
        remoteFolderTrust = options.Config.RemoteFolderTrust;
        TrustDevices = options.Config.RemoteFolderTrustDevices;
    }

    LaunchConfig Config => options.Config;

    public IReadOnlyList<Started> LaunchedProcesses()
    {
        lock (pendingGate) { return [.. launchedOn.Values.Concat(pending.Select(p => p.Started)).Distinct().Where(starter.Running)]; }
    }

    // agent-ha-bridge's $script:BridgeResumeCacheLimit: how many of the newest sessions in
    // approved folders it caches, and so decides the resume labels over.
    const int KindsAmong = 200;

    public bool Available => Config.Enabled && options.PlatformSupported && probe.Active() is null && starter.Unavailable() is null;

    public bool AnswersTrust => Available;

    // launch.remoteFolderTrust as it is now: config.json's at start, and the app's setting at once
    // when it changes it (HostControl), so it can be allowed while a question waits.
    public bool RemoteFolderTrust
    {
        get => Volatile.Read(ref remoteFolderTrust);
        set => Volatile.Write(ref remoteFolderTrust, value);
    }

    bool remoteFolderTrust;

    // The devices a person approved at the machine to answer folder-trust questions from
    // (launch.remoteFolderTrustDevices), by device id. The relay's kind for a device is what it
    // says it is, and any device of the account can say "pwa", so this list, not the kind, is
    // what lets one answer. Live from the app, as RemoteFolderTrust.
    public IReadOnlyCollection<string> TrustDevices
    {
        get => Volatile.Read(ref trustDevices);
        set => Volatile.Write(ref trustDevices, new HashSet<string>(value, StringComparer.Ordinal));
    }

    IReadOnlyCollection<string> trustDevices = new HashSet<string>(StringComparer.Ordinal);

    // ---- launch.options ----

    public async Task<LaunchOptions> OptionsAsync(CancellationToken cancellationToken = default) => (await ResolveAsync(cancellationToken)).Options;

    // The options, with what a launch needs that a client is not sent: each launcher's
    // executable and each resumable session's entry.
    sealed record Resolved(LaunchOptions Options, IReadOnlyDictionary<string, string> Executables, IReadOnlyList<ResumeEntry> Resumable);

    async Task<Resolved> ResolveAsync(CancellationToken ct)
    {
        var executables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in Launchers.All)
        {
            if (locator.Find(l, Config.Agent(l.Id)) is { } exe) { executables[l.Id] = exe; }
        }
        var live = source.Snapshot();
        var agents = new List<LaunchAgent>();
        foreach (var l in Launchers.All.Where(l => executables.ContainsKey(l.Id)))
        {
            var (models, efforts, contexts, defaults) = Tuning(l, live);
            IReadOnlyList<string>? profiles = null;
            string? profile = null;
            if (l.TakesProfile)
            {
                (profiles, profile) = await ProfilesAsync(executables[l.Id], ct);
            }
            agents.Add(new LaunchAgent
            {
                Id = l.Id,
                Label = l.Label,
                Kind = l.Kind,
                Models = models,
                Efforts = efforts,
                Contexts = contexts,
                Profiles = profiles,
                Defaults = defaults with { Profile = profile },
                Attachments = l.TakesAttachments && attachments is not null ? true : null,
                NeedsPrompt = l.NeedsPrompt ? true : null,
            });
        }

        var workspaces = Config.Workspaces
            .Where(w => Folders.Physical(w.Path) is { } p && !options.IsSystemDirectory(p))
            .Select(w => new LaunchWorkspace { Label = w.Label, Path = w.Path, Isolate = w.Isolate ? true : null })
            .ToList();

        var entries = await ResumableAsync(executables, live, ct);
        var resumableList = entries.Select(e => new ResumableSession
        {
            Id = e.Entry.Id,
            Agent = e.Entry.Agent,
            Launcher = e.Entry.Launcher,
            Title = e.Title,
            Cwd = e.Entry.Folder,
            Workspace = e.Workspace,
            UpdatedAt = e.Entry.Updated,
        }).ToList();

        var defaultAgent = Config.DefaultAgent != "auto" && executables.ContainsKey(Config.DefaultAgent) ? Config.DefaultAgent : agents.FirstOrDefault()?.Id;
        var defaultWorkspace = workspaces.FirstOrDefault(w => w.Label == Config.DefaultWorkspace)?.Label ?? workspaces.FirstOrDefault()?.Label;
        var result = new LaunchOptions
        {
            Agents = agents,
            Workspaces = workspaces,
            Resumable = resumableList,
            Defaults = new LaunchDefaults { Agent = defaultAgent, Workspace = defaultWorkspace, Permissions = Config.Permissions },
            Busy = BusyReason(),
            Trust = TrustQuestions() is { Count: > 0 } trust ? trust : null,
        };
        // Within its budget, losing the oldest resumable sessions first, then the longest lists.
        while (ProtocolJson.ByteCount(result) > LaunchProtocol.MaxOptionsBytes && result.Resumable.Count > 0)
        {
            result = result with { Resumable = [.. result.Resumable.Take(result.Resumable.Count - 1)] };
        }
        while (ProtocolJson.ByteCount(result) > LaunchProtocol.MaxOptionsBytes && result.Agents.Any(a => a.Models.Count > 8))
        {
            result = result with { Agents = [.. result.Agents.Select(a => a with { Models = [.. a.Models.Take(Math.Max(8, a.Models.Count - 8))] })] };
        }
        return new Resolved(result, executables, [.. entries.Select(e => e.Entry)]);
    }

    // Model, effort and context for a launcher, as agent-ha-bridge's Get-BridgeTuningOptions
    // offers them: the configured default first (so a machine set to a preview model can still
    // see and choose it), then the configured list, or else the built-in one. This host adds
    // the models its live sessions of that kind are using, in place of the roster
    // agent-ha-bridge discovers by starting each agent headless.
    (IReadOnlyList<string> Models, IReadOnlyList<string> Efforts, IReadOnlyList<string> Contexts, LaunchTuning Defaults) Tuning(Launcher l, IReadOnlyList<Session> live)
    {
        var config = Config.Agent(l.Tuning);
        var table = Launchers.TuningFor(l.Tuning);
        IEnumerable<string> models = config.Models.Count > 0 ? config.Models : table.Models.Concat(live.Where(s => s.Agent == l.Kind && s.Model is not null).Select(s => s.Model!));
        var m = Offer(config.Model, models, ModelName());
        var e = Offer(config.Effort, config.Efforts.Count > 0 ? config.Efforts : table.Efforts, EffortName());
        var c = Offer(config.Context, config.Contexts.Count > 0 ? config.Contexts : table.Contexts, ModelName());
        var defaults = new LaunchTuning
        {
            Model = config.Model is { } dm && m.Contains(dm) ? dm : null,
            Effort = config.Effort is { } de && e.Contains(de) ? de : null,
            Context = config.Context is { } dc && c.Contains(dc) ? dc : null,
        };
        return (m, e, c, defaults);
    }

    // Distinct, in order, and only what may reach a command line.
    static List<string> Offer(string? first, IEnumerable<string> values, Regex valid)
    {
        var list = new List<string>();
        foreach (var v in (first is null ? values : values.Prepend(first)))
        {
            if (list.Count >= LaunchProtocol.MaxChoices) { break; }
            if (valid.IsMatch(v) && v is not ("true" or "false" or "inf" or "nan") && !list.Contains(v, StringComparer.Ordinal)) { list.Add(v); }
        }
        return list;
    }

    // agent-ha-bridge's filters for a model name and an effort name.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:\[\]-]{0,127}$")]
    private static partial Regex ModelName();

    [GeneratedRegex(@"^[a-z][a-z0-9_-]{0,31}$")]
    private static partial Regex EffortName();

    // Agency's profiles as agent-ha-bridge offers them (Get-BridgeAgencyProfiles): every one it
    // lists, or only those of them launch.profiles names (all, if that leaves none); the
    // configured list alone when Agency cannot be asked. The default is launch.defaultProfile
    // when offered, else the first.
    async Task<(IReadOnlyList<string>, string?)> ProfilesAsync(string exe, CancellationToken ct)
    {
        var found = await agency.ProfilesAsync(exe, ct);
        IReadOnlyList<string> offered;
        if (found is null) { offered = Config.Profiles; }
        else if (Config.Profiles.Count == 0) { offered = found; }
        else
        {
            var both = found.Where(p => Config.Profiles.Contains(p, StringComparer.Ordinal)).ToList();
            offered = both.Count > 0 ? both : found;
        }
        offered = [.. offered.Where(p => p.Length <= LaunchProtocol.MaxName && !p.Any(char.IsControl)).Take(LaunchProtocol.MaxChoices)];
        var def = Config.DefaultProfile is { } d && offered.Contains(d) ? d : offered.FirstOrDefault();
        return (offered, def);
    }

    sealed record Offered(ResumeEntry Entry, string Title, string? Workspace);

    // agent-ha-bridge's Get-BridgeResumableSessions: every installed agent's sessions (Agency's
    // when it has any, Copilot's own store otherwise), each once and under its own agent, with
    // the live ones left out (two CLIs on one transcript would corrupt it) and only those in
    // approved folders kept, before the list is cut to resumeCount. Newest first.
    async Task<List<Offered>> ResumableAsync(IReadOnlyDictionary<string, string> executables, IReadOnlyList<Session> live, CancellationToken ct)
    {
        if (Config.ResumeCount <= 0) { return []; }
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        if (executables.ContainsKey("claude")) { kinds.Add("claude"); }
        if (executables.ContainsKey("codex")) { kinds.Add("codex"); }
        IReadOnlyList<ResumeEntry> fromAgency = executables.TryGetValue("agency", out var a) ? await agency.SessionsAsync(a, ct) : [];
        var copilotLauncher = executables.ContainsKey("copilot") ? "copilot" : executables.ContainsKey("agency") ? "agency" : null;
        if (fromAgency.Count == 0 && copilotLauncher is not null) { kinds.Add("copilot"); }
        var stored = await Task.Run(() => resumable.Read(kinds), ct);

        var candidates = stored.Where(e => e.Agent != AgentKind.Copilot).Concat(fromAgency)
            .Concat(stored.Where(e => e.Agent == AgentKind.Copilot).Select(e => e with { Launcher = copilotLauncher! }));
        // An agent whose discovery is held may have a live session discovery could not see, and
        // offering it would put two CLIs on one transcript; none of its sessions is offered
        // until a complete pass (§6.3 invariants 6 and 10).
        if (source.Held is { } held) { candidates = candidates.Where(e => !held.Agents.Contains(e.Agent)); }
        var once = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Approval can ask git (a worktree's top and marker), so each folder is asked once per
        // pass, and newest first, so the older sessions past the limit are never asked about.
        var approvals = new Dictionary<string, WorkspaceConfig?>(StringComparer.Ordinal);
        WorkspaceConfig? ApprovedOnce(string? folder)
        {
            if (folder is not { Length: > 0 }) { return null; }
            if (!approvals.TryGetValue(folder, out var w)) { approvals[folder] = w = Approved(folder); }
            return w;
        }
        var eligible = candidates.Where(e => once.Add(e.Id))
            .OrderByDescending(e => e.Updated ?? DateTimeOffset.MinValue)
            .Select(e => (Entry: e, Workspace: ApprovedOnce(e.Folder)))
            .Where(x => x.Workspace is not null)
            .Take(KindsAmong)
            .ToList();
        // The live list read after the hold, not the one taken before the awaits above: a session
        // that discovery found meanwhile is listed before its hold is said to clear, so a clear
        // hold here means it is in this snapshot.
        var excluded = new HashSet<string>(live.Concat(source.Snapshot()).Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
        // And any a launch has started an agent on that has not registered yet.
        lock (pendingGate) { excluded.UnionWith(launchedOn.Where(x => starter.Running(x.Value)).Select(x => x.Key)); }
        var offered = new List<Offered>();
        // The agent is named once the sessions that could be offered are of more than one kind:
        // agent-ha-bridge decides it over its cached list (the newest 200 in approved folders,
        // live ones still in it). Decided over every session found instead, one Claude session
        // in a folder nobody approved put "Agency: " on every label of a Copilot-only list.
        var prefixed = eligible.Select(x => x.Entry.Agent).Distinct().Count() > 1;
        // Labels are told apart over that same list, live ones included and ignoring case, as
        // agent-ha-bridge's Get-BridgeResumableSessions does: a repeat gets its id's first eight
        // characters, and one that still repeats is not offered. Told apart over what is left
        // after the live ones, an older namesake of a running session kept a plain label here
        // and a suffixed one there (#40).
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (e, workspace) in eligible)
        {
            if (offered.Count >= Config.ResumeCount) { break; }
            var title = Title(e, prefixed);
            if (!labels.Add(title))
            {
                title = Limits.KeepStart($"{title} ({e.Id[..Math.Min(8, e.Id.Length)]})", LaunchProtocol.MaxLabel * 2)!;
                if (!labels.Add(title)) { continue; }
            }
            if (!excluded.Contains(e.Id)) { offered.Add(new Offered(e, title, workspace!.Label)); }
        }
        return offered;
    }

    // "<summary> - <folder>", the agent named when the list holds more than one kind, cut to 120
    // characters, as agent-ha-bridge labels its resume list.
    static string Title(ResumeEntry e, bool prefixed)
    {
        var summary = ResumableStore.Collapse(e.Summary);
        if (summary.Length == 0) { summary = $"Session {e.Id[..8]}"; }
        if (prefixed) { summary = $"{(Launchers.Find(e.Launcher)?.Label ?? e.Launcher)}: {summary}"; }
        var leaf = e.Folder?.TrimEnd('\\', '/').Split('\\', '/').LastOrDefault();
        var label = string.IsNullOrEmpty(leaf) ? summary : $"{summary} - {leaf}";
        return label.Length > LaunchProtocol.MaxLabel ? label[..(LaunchProtocol.MaxLabel - 3)] + "..." : label;
    }

    // The approved workspace a session ran in: that folder itself, as agent-ha-bridge's
    // Test-BridgeWorkspacePathApproved has it, or anywhere in a worktree this host made for an
    // isolated one; null when neither. Not a folder below a workspace: approving ~\repos
    // approved resuming every session in every repository under it, which agent-ha-bridge
    // never offered (found by the shadow comparison, #40).
    WorkspaceConfig? Approved(string folder)
    {
        if (!Path.IsPathFullyQualified(folder) || Folders.Physical(folder) is not { } physical || options.IsSystemDirectory(physical)) { return null; }
        return Folders.Exactly(Config.Workspaces, physical) ?? Worktrees.Managed(Config.Workspaces, physical)?.Workspace;
    }

    // ---- session.transfer, the source's half ----

    public AgentHomes Homes => new(options.CopilotHome, options.ClaudeHome, options.CodexHome);

    public TransferConfig TransferConfig => Config.Transfer;

    // Whether this machine hands its sessions to the account's other machines now.
    public string? ServeUnavailable => !Config.Transfer.Serve ? "this machine does not hand its sessions to other machines (launch.transfer.serve in its config)"
        : !Config.Enabled ? "launching is turned off on this machine (launch.enabled in its config)"
        : probe.Active();

    // A session another machine asks for, if this machine offers it now, with its title; else
    // why not. Only what the resume list offers can be taken: an ended session in an approved
    // folder. The approved-folder filter on what is listed is a filter on what can be taken too,
    // or any session in the agent's home could be asked for by name.
    public async Task<(ResumeEntry? Entry, string? Title, string? Problem)> TransferableAsync(string id, CancellationToken cancellationToken = default)
    {
        if (ServeUnavailable is { } why) { return (null, null, why); }
        var resolved = await ResolveAsync(cancellationToken);
        var entry = resolved.Resumable.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return (null, null, source.Get(id) is not null || Starting(id)
                ? "that session is running on its machine; it can be brought over once it has ended there"
                : "that session is not one its machine offers (it is not on its resume list)");
        }
        return (entry, resolved.Options.Resumable.FirstOrDefault(x => x.Id == entry.Id)?.Title ?? entry.Summary, null);
    }

    // Asked again just before a session is packed: one resumed at its machine meanwhile is live.
    public bool Running(string id) => source.Get(id) is not null || Starting(id);

    // ---- session.launch ----

    public Task<LaunchResult> LaunchAsync(LaunchRequest request, string peer, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(request.Request, "D", out var key)) { return Task.FromResult(Refused("request must be a UUID")); }
        return requests.Once(key, () => RunAsync(request, key, peer), result =>
            log($"launch {Short(key)}: {result.Outcome.ToString().ToLowerInvariant()}{(result.Session is { } s ? $" session {Short(s)}" : "")}{(result.Reason is null ? "" : $" ({result.Reason})")}"));
    }

    // Everything a launch was settled on before it starts.
    sealed record Plan(Launcher Launcher, string Executable, string Directory, string? Workspace, WorkspaceConfig? Isolated, ResumeEntry? Resume,
        string Where, string Profile, string Model, string Effort, string Context, bool AllowAll, string Prompt, LaunchTransfer? Transfer = null);

    async Task<LaunchResult> RunAsync(LaunchRequest request, Guid key, string peer)
    {
        try
        {
            if (!options.PlatformSupported) { return Refused("launching sessions is not supported on this platform yet; start it at the machine"); }
            if (probe.Active() is { } active) { return Refused(active); }
            if (!Config.Enabled) { return Refused("launching is turned off on this machine (launch.enabled in the host's config)"); }
            if (starter.Unavailable() is { } unavailable) { return Refused(unavailable); }
            if (LaunchProtocol.Check(request) is { } invalid) { return Refused(invalid); }
            var settings = string.Join(", ", new[] { ("agent", request.Agent), ("workspace", request.Workspace), ("resume", request.Resume), ("profile", request.Profile), ("model", request.Model), ("effort", request.Effort), ("context", request.Context), ("permissions", request.Permissions) }
                .Where(x => x.Item2 is not null).Select(x => $"{x.Item1}={x.Item2}"));
            log($"launch {Short(key)} received ({(settings.Length > 0 ? settings : "machine defaults")}; prompt of {(request.Prompt ?? "").Length} chars{((request.Attachments?.Count ?? 0) > 0 ? $", {request.Attachments!.Count} attachment(s)" : "")})");
            if (BusyReason() is { } busy) { return new LaunchResult(LaunchOutcome.Busy, busy) { Trust = BusyQuestion() }; }

            var resolved = await ResolveAsync(CancellationToken.None);
            // A resume not on the list while discovery is held may be of a session that is
            // running unseen; that cannot be told until a complete pass, so it waits.
            if (request.Resume is { } resumeId && source.Held is { } held
                && !resolved.Resumable.Any(e => string.Equals(e.Id, resumeId, StringComparison.OrdinalIgnoreCase)))
            {
                return new LaunchResult(LaunchOutcome.Busy, HeldReason(held));
            }
            var (plan, problem) = Settle(request, resolved);
            if (plan is null) { return Refused(problem!); }

            Claim? claim = null;
            if (request.Attachments is { Count: > 0 } refs)
            {
                if (!plan.Launcher.TakesAttachments || attachments is null) { return Refused($"{plan.Launcher.Label} cannot be given attachments when it starts; send them with a reply once it has"); }
                (claim, var missing) = attachments.Claim(peer, refs);
                if (claim is null) { return Refused(missing!); }
            }
            var handed = new StrongBox<bool>();
            FetchedBundle? fetched = null;
            try
            {
                // A session from another machine is fetched, and checked, before anything here is
                // touched: a transfer that fails leaves no worktree and no session behind. Inside
                // this try, so however it fails the attachments claimed above are released.
                if (plan.Transfer is { } transfer)
                {
                    log($"launch {Short(key)}: bringing session {Short(transfer.Session)} from {Short(transfer.From)}");
                    try { fetched = await options.Transfers!.FetchAsync(transfer, peer, Config.Transfer.MaxBytes, CancellationToken.None); }
                    catch (TransferRefusedException e) { return Refused($"the session could not be brought from the other machine: {e.Message}"); }
                    if (fetched.About.Agent != plan.Launcher.Kind)
                    {
                        var theirs = Launchers.Find(fetched.About.Launcher)?.Label ?? fetched.About.Agent.ToString();
                        return Refused($"'{fetched.About.Title}' is a {theirs} session, so it resumes in {theirs}, not {plan.Launcher.Label}");
                    }
                }
                return await StartAsync(plan, claim, key, handed, fetched);
            }
            finally
            {
                // Anything not handed to the agent stays held for another try.
                if (claim is not null && !handed.Value) { attachments!.Release(claim); }
                fetched?.Dispose();
            }
        }
        catch (Exception e)
        {
            return Refused($"the host could not launch the session: {e.Message}");
        }
    }

    // agent-ha-bridge's Resolve-DaemonRequestedLaunch: what the request names, against what this
    // machine offers, with its defaults filling what the request leaves out.
    (Plan?, string?) Settle(LaunchRequest r, Resolved resolved)
    {
        var o = resolved.Options;
        Launcher? launcher;
        ResumeEntry? resume = null;
        WorkspaceConfig? workspace = null;
        string directory;
        string where;
        if (r.Transfer is { } transfer)
        {
            if (!Config.Transfer.Receive) { return (null, "bringing sessions from other machines is turned off here (launch.transfer.receive in the host's config)"); }
            if (options.Transfers is null || options.TransferStaging is null) { return (null, "this host cannot bring sessions from other machines"); }
            if (options.Transfers.Unavailable is { } unavailable) { return (null, unavailable); }
            // The launcher it resumes in is named by the client, from the other machine's list,
            // and checked against the bundle once it is here.
            launcher = r.Agent is { } named ? Launchers.Find(named) : null;
            if (launcher is null || !resolved.Executables.ContainsKey(launcher.Id))
            {
                return (null, r.Agent is null ? "name the agent that resumes it, as the other machine lists it" : $"{launcher?.Label ?? r.Agent} is not installed here, so that session can't be resumed here");
            }
            if (o.Workspaces.Count == 0) { return (null, "no workspace is approved on this machine yet; approve one at the machine (agent-bridge-host workspace add <folder>)"); }
            workspace = Config.Workspaces.FirstOrDefault(w => w.Label == r.Workspace && o.Workspaces.Any(x => x.Label == w.Label));
            if (workspace is null) { return (null, $"Unknown workspace '{Limits.KeepStart(r.Workspace, 64)}'; this machine has {string.Join(", ", o.Workspaces.Select(w => w.Label))}"); }
            if (Folders.Physical(workspace.Path) is not { } physical) { return (null, "Launch refused: select a currently configured workspace."); }
            directory = physical;
            where = workspace.Label;
        }
        else if (r.Resume is { } resumeId)
        {
            resume = resolved.Resumable.FirstOrDefault(e => string.Equals(e.Id, resumeId, StringComparison.OrdinalIgnoreCase));
            if (resume is null)
            {
                return (null, source.Get(resumeId) is not null || Starting(resumeId)
                    ? "that session is already running; two agents on one session would corrupt it"
                    : $"No resumable session '{Limits.KeepStart(resumeId, 64)}' on this machine; it is not on its resume list");
            }
            launcher = Launchers.Find(r.Agent ?? resume.Launcher);
            var title = o.Resumable.FirstOrDefault(x => x.Id == resume.Id)?.Title ?? resume.Summary;
            if (launcher is null || !resolved.Executables.ContainsKey(launcher.Id))
            {
                return (null, $"{launcher?.Label ?? r.Agent} is not installed here, so that session can't be resumed");
            }
            if (launcher.Kind != resume.Agent)
            {
                var theirs = Launchers.Find(resume.Launcher)?.Label ?? resume.Agent.ToString();
                return (null, $"'{title}' is a {theirs} session, so it resumes in {theirs}, not {launcher.Label}");
            }
            if (resume.Folder is not { } folder || Approved(folder) is null || Folders.Physical(folder) is not { } physical)
            {
                return (null, "Resume refused: the original directory must exist and be an approved workspace.");
            }
            directory = physical;
            where = title;
        }
        else
        {
            var agentId = r.Agent ?? o.Defaults.Agent;
            if (o.Agents.Count == 0) { return (null, "no agent is installed on this machine (Copilot, Agency, Claude or Codex)"); }
            launcher = Launchers.Find(agentId);
            if (launcher is null || !resolved.Executables.ContainsKey(launcher.Id))
            {
                return (null, $"Unknown agent '{agentId}'; this machine has {string.Join(", ", o.Agents.Select(a => a.Id))}");
            }
            if (o.Workspaces.Count == 0)
            {
                return (null, "no workspace is approved on this machine yet; approve one at the machine (agent-bridge-host workspace add <folder>)");
            }
            var label = r.Workspace ?? o.Defaults.Workspace;
            workspace = Config.Workspaces.FirstOrDefault(w => w.Label == label && o.Workspaces.Any(x => x.Label == w.Label));
            if (workspace is null) { return (null, $"Unknown workspace '{Limits.KeepStart(label, 64)}'; this machine has {string.Join(", ", o.Workspaces.Select(w => w.Label))}"); }
            if (Folders.Physical(workspace.Path) is not { } physical) { return (null, "Launch refused: select a currently configured workspace."); }
            directory = physical;
            where = workspace.Label;
            if (launcher.NeedsPrompt && LaunchProtocol.Flatten(r.Prompt).Length == 0 && (r.Attachments?.Count ?? 0) == 0)
            {
                return (null, $"{launcher.Label} starts its session only on a first message; give it a prompt");
            }
        }

        var agent = o.Agents.First(a => a.Id == launcher.Id);
        var profile = "";
        if (launcher.TakesProfile && agent.Profiles is { } profiles)
        {
            var asked = r.Profile ?? agent.Defaults?.Profile;
            if (asked is { Length: > 0 })
            {
                if (!profiles.Contains(asked, StringComparer.Ordinal)) { return (null, $"Unknown profile '{Limits.KeepStart(asked, 64)}'; this machine has {(profiles.Count == 0 ? "none" : string.Join(", ", profiles))}"); }
                profile = asked;
            }
        }
        string? Axis(string name, string? asked, string? configured, IReadOnlyList<string> offered, out string? why)
        {
            why = null;
            // Absent: the machine's default. "": the agent's own, over the machine's.
            var value = asked ?? configured ?? "";
            if (value.Length == 0) { return ""; }
            if (offered.Contains(value, StringComparer.Ordinal)) { return value; }
            why = $"Unknown {name} '{Limits.KeepStart(value, 64)}' for {launcher!.Label}; it offers {(offered.Count == 0 ? "only its own default" : string.Join(", ", offered))}";
            return null;
        }
        var model = Axis("model", r.Model, agent.Defaults?.Model, agent.Models, out var badModel);
        if (model is null) { return (null, badModel); }
        var effort = Axis("effort", r.Effort, agent.Defaults?.Effort, agent.Efforts, out var badEffort);
        if (effort is null) { return (null, badEffort); }
        var context = Axis("context", r.Context, agent.Defaults?.Context, agent.Contexts, out var badContext);
        if (context is null) { return (null, badContext); }
        var allowAll = (r.Permissions ?? Config.Permissions) == LaunchProtocol.AllowAll;

        return (new Plan(launcher, resolved.Executables[launcher.Id], directory, workspace?.Label, workspace?.Isolate == true ? workspace : null, resume, where,
            profile, model, effort, context, allowAll, LaunchProtocol.Flatten(r.Prompt), r.Transfer), null);
    }

    async Task<LaunchResult> StartAsync(Plan plan, Claim? claim, Guid key, StrongBox<bool> handed, FetchedBundle? fetched)
    {
        Pending follow;
        LaunchedWith launched;
        await starting.WaitAsync();
        string? reservation = null, reserved = null;
        try
        {
            // Checked again: agent-ha-bridge may have started while this one waited its turn.
            if (probe.Active() is { } active) { return Refused(active); }
            if (BusyReason() is { } busy) { return new LaunchResult(LaunchOutcome.Busy, busy) { Trust = BusyQuestion() }; }
            // And the session to resume, which another press may have resumed while this one
            // waited: two agents on one transcript would corrupt it.
            if (plan.Resume is { } again && (source.Get(again.Id) is not null || Starting(again.Id)))
            {
                return Refused("that session is already running; two agents on one session would corrupt it");
            }
            // Or whose agent's discovery became held meanwhile. And a new Codex session, which is
            // bound to its launch as the first new Codex session to appear: with Codex's
            // discovery held, one that was running unseen could appear first and be taken for it.
            if (HeldFor(plan) is { } held) { return new LaunchResult(LaunchOutcome.Busy, HeldReason(held)); }

            var directory = plan.Directory;
            string? worktree = null;
            if (plan.Isolated is { } isolated)
            {
                var made = await Task.Run(() => Worktrees.Create(directory, Config, options.LocalNow, new HeldWorktreeUsage(source, options.WorktreeUsage), m => log($"launch {Short(key)}: worktree: {m}")));
                if (made.Detail.Length > 0) { log($"launch {Short(key)}: worktree: {made.Detail}"); }
                if (made.Path is null) { return Refused($"Launch refused: requested isolation failed. {made.Problem}"); }
                directory = made.Path;
                worktree = made.Worktree;
            }
            else if (plan.Resume is not null)
            {
                worktree = (await Task.Run(() => Worktrees.Managed(Config.Workspaces, directory)))?.Worktree;
            }
            if (worktree is not null)
            {
                var (token, problem) = await Task.Run(() => Worktrees.Reserve(worktree));
                if (problem is not null) { return Refused($"Launch refused: {problem}"); }
                if (token is not null) { (reservation, reserved) = (token, worktree); }
            }

            var prompt = plan.Prompt;
            if (claim is not null)
            {
                if (attachments!.StagingRoot.Any(char.IsWhiteSpace)) { return Refused("the host's attachment folder has a space in its path, which an @ mention cannot carry; nothing was started"); }
                IReadOnlyList<string> staged;
                try { staged = attachments.Stage(claim); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
                {
                    return Refused($"the host could not stage the attachments privately ({e.Message}); nothing was started");
                }
                if (CopilotReply.WithAttachments(prompt, staged) is not { } line) { return Refused("an attachment's staged path cannot be mentioned; nothing was started"); }
                prompt = line;
            }

            // A session from another machine resumes under an id of its own. The command line is
            // checked before it is installed, so a refusal leaves no copy behind.
            var newId = fetched is not null ? Guid.NewGuid().ToString() : null;
            var sessionId = newId ?? plan.Resume?.Id ?? (plan.Launcher.ChoosesOwnSessionId ? "" : Guid.NewGuid().ToString());
            var args = Launchers.Arguments(plan.Launcher, sessionId, plan.Resume is not null || newId is not null, plan.Model, plan.Effort, plan.Context, plan.AllowAll,
                Config.Agent(plan.Launcher.Id).ExtraArgs, plan.Profile, prompt);
            var spec = new StartSpec(plan.Executable, args, directory, new Dictionary<string, string> { [Launchers.SessionVariable] = sessionId.Length > 0 ? sessionId : "launched" });
            if (starter.CommandLine(spec, out var tooLong) is null)
            {
                return Refused($"the prompt is too long to start {plan.Launcher.Label} with on this machine: {tooLong}; shorten it, or send the rest as a reply once it has started");
            }

            // It becomes one of this machine's sessions, working in the folder settled on here;
            // then it resumes like any other.
            if (fetched is not null && newId is not null)
            {
                try
                {
                    var unpack = Path.Combine(PrivateFolder.Ensure(options.TransferStaging!), $"unpack-{Guid.NewGuid():N}");
                    await Task.Run(() => SessionBundles.Install(fetched.Zip, fetched.About.Agent, plan.Transfer!.Session, newId, directory, Homes, unpack, Config.Transfer.MaxSourceBytes, options.Time.GetUtcNow()));
                }
                catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    return Refused($"the session from the other machine could not be installed here: {e.Message}; nothing was started");
                }
                log($"launch {Short(key)}: installed session {Short(plan.Transfer!.Session)} from {Short(fetched.From)} as {Short(newId)} ({TransferLimits.Mib(fetched.About.Size)}, {fetched.About.Files} file(s))");
                plan = plan with { Resume = new ResumeEntry(newId, fetched.About.Agent, plan.Launcher.Id, fetched.About.Title, directory, options.Time.GetUtcNow()), Where = fetched.About.Title };
            }

            launched = new LaunchedWith
            {
                Agent = plan.Launcher.Id,
                Workspace = plan.Workspace,
                Resume = plan.Resume?.Id,
                Cwd = directory,
                Worktree = plan.Isolated is not null ? true : null,
                Profile = plan.Profile.Length > 0 ? plan.Profile : null,
                Model = plan.Model.Length > 0 ? plan.Model : null,
                Effort = plan.Effort.Length > 0 ? plan.Effort : null,
                Context = plan.Context.Length > 0 ? plan.Context : null,
                Permissions = plan.AllowAll ? LaunchProtocol.AllowAll : LaunchProtocol.Ask,
                Transfer = fetched is null ? null : new LaunchedTransfer { From = fetched.From, Session = plan.Transfer!.Session, Bytes = fetched.About.Size },
            };
            // And again last thing: preparing a worktree takes seconds, in which a hold can begin.
            // The reservation and any staged attachments are released below.
            if (HeldFor(plan) is { } heldNow) { return new LaunchResult(LaunchOutcome.Busy, HeldReason(heldNow)); }
            var codexBefore = source.Snapshot().Where(s => s.Agent == AgentKind.Codex).Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var tuning = string.Join(" / ", new[] { plan.Model, plan.Effort, plan.Context, plan.AllowAll ? "allow all" : "" }.Where(x => x.Length > 0));
            log($"launch {Short(key)}: {(plan.Resume is null ? "new" : "resuming")} {plan.Launcher.Label} session in '{plan.Where}' ({directory}){(plan.Profile.Length > 0 ? $" profile '{plan.Profile}'" : "")}{(tuning.Length > 0 ? $" [{tuning}]" : "")}");

            Started started;
            try { started = starter.Start(spec); }
            catch (Exception e)
            {
                log($"launch {Short(key)}: {plan.Launcher.Label} could not be started: {e.Message}");
                return new LaunchResult(LaunchOutcome.Failed, $"{plan.Launcher.Label} could not be started: {e.Message}") { Launched = launched };
            }
            if (claim is not null) { attachments!.Consume(claim); handed.Value = true; }
            if (sessionId.Length > 0) { Launched(sessionId, started); }
            log($"launch {Short(key)}: {(plan.Resume is null ? "started" : "resumed")} pid {started.ProcessId} in {directory} via {plan.Launcher.Label} (session {(sessionId.Length > 0 ? sessionId : "id chosen by the agent")})");
            // The reservation is the follow's now, released when the session registers.
            follow = new Pending(key, plan.Launcher, sessionId, started, plan.Where, options.Time.GetUtcNow(), codexBefore, reserved, reservation, launched);
            reservation = null;
            lock (pendingGate) { pending.Add(follow); }
            resumable.Forget();
        }
        finally
        {
            if (reservation is not null && reserved is not null) { Worktrees.Release(reserved, reservation); }
            starting.Release();
        }

        _ = Task.Run(() => FollowAsync(follow));
        var waited = await Task.WhenAny(follow.Decided.Task, Task.Delay(options.RegistrationWait, options.Time));
        if (waited == follow.Decided.Task) { return await follow.Decided.Task; }
        // A session that registered but whose window is not yet seen ready (Copilot) did appear;
        // it is the window that is not known.
        var unconfirmed = new LaunchResult(LaunchOutcome.Unconfirmed, follow.RegisteredAt is not null
            ? $"{plan.Launcher.Label} started in {plan.Where}, but its window was not seen ready, so whether it is waiting on a question there is not known; check its window"
            : $"{plan.Launcher.Label} started in {plan.Where} (pid {follow.Started.ProcessId}), but its session had not appeared after {options.RegistrationWait.TotalSeconds:0} s; it may still be starting, so check its window")
        {
            Session = follow.SessionId.Length > 0 ? follow.SessionId : null,
            Launched = launched,
        };
        follow.Decided.TrySetResult(unconfirmed);
        return await follow.Decided.Task;
    }

    // One launch followed from its start until its session registers, its process exits, or
    // FollowFor runs out, a look every PollInterval: agent-ha-bridge's Update-DaemonPendingLaunch.
    async Task FollowAsync(Pending p)
    {
        var label = p.Launcher.Label;
        var lastScreen = DateTimeOffset.MinValue;
        try
        {
            while (true)
            {
                // Discovery runs on its own every second; while its answer is awaited a launch
                // looks itself, so that answer does not wait a pass.
                if (options.Time.GetUtcNow() - p.At < options.RegistrationWait)
                {
                    try { await source.RefreshAsync(); } catch (Exception) { }
                }
                var now = options.Time.GetUtcNow();
                var registered = Registered(p);
                if (registered is { } id)
                {
                    p.RegisteredAt ??= now;
                    p.LiveId = id;
                    if (!p.Launcher.RegistersBeforeReady || p.Ready)
                    {
                        log($"launch {Short(p.Key)}: {label} in {p.Where} registered as session {Short(id)} after {(now - p.At).TotalSeconds:0.0} s");
                        Decide(p, new LaunchResult(LaunchOutcome.Started) { Session = id, Launched = p.Launched });
                        return;
                    }
                    // Answered once, and followed on: a trust question drawn later still keeps
                    // other launches busy, and a window that becomes ready still counts.
                    if (p.Question == StartupQuestion.None && !p.ReadyMissed && now - p.RegisteredAt >= options.ReadyWait)
                    {
                        p.ReadyMissed = true;
                        log($"launch {Short(p.Key)}: {label} in {p.Where} registered as session {Short(id)}, but its window was not seen ready within {options.ReadyWait.TotalSeconds:0} s");
                        p.Decided.TrySetResult(new LaunchResult(LaunchOutcome.Unconfirmed,
                            $"{label} started in {p.Where}, but its window was not seen ready, so whether it is waiting on a question there is not known; check its window")
                        { Session = id, Launched = p.Launched });
                    }
                }
                if (!p.HandedOn && !starter.Running(p.Started))
                {
                    // A launcher that hands the session to another process may exit first: its
                    // session is given a moment to show before the launch counts as failed.
                    await Task.Delay(options.ExitGrace, options.Time);
                    try { await source.RefreshAsync(); } catch (Exception) { }
                    if (Registered(p) is not { } late)
                    {
                        log(p.Declined is { } by
                            ? $"launch {Short(p.Key)}: {label} in {p.Where} closed: a person declined to trust the folder (from {by})"
                            : p.RegisteredAt is null
                            ? $"launch {Short(p.Key)}: {label} (pid {p.Started.ProcessId}) exited before its session registered"
                            : $"launch {Short(p.Key)}: {label} in {p.Where} closed before its window was seen ready");
                        Decide(p, new LaunchResult(LaunchOutcome.Failed, $"{label} in {p.Where} closed before it started; start it at the machine to see why") { Launched = p.Launched });
                        return;
                    }
                    p.RegisteredAt ??= options.Time.GetUtcNow();
                    p.LiveId = late;
                    if (!p.Launcher.RegistersBeforeReady || p.Ready)
                    {
                        Decide(p, new LaunchResult(LaunchOutcome.Started) { Session = late, Launched = p.Launched });
                        return;
                    }
                    // Handed on before its window was seen ready: the session's own window is
                    // read from here on, as it would have been.
                    p.HandedOn = true;
                }
                else if (p.HandedOn && registered is null)
                {
                    log($"launch {Short(p.Key)}: {label} in {p.Where} closed before its window was seen ready");
                    Decide(p, new LaunchResult(LaunchOutcome.Failed, $"{label} in {p.Where} closed before it started; start it at the machine to see why") { Launched = p.Launched });
                    return;
                }
                if (now - p.At >= options.ScreenAfter && now - lastScreen >= options.ScreenEvery)
                {
                    lastScreen = now;
                    var window = p.HandedOn && registered is { } handed && source.ProcessOf(handed) is { } owner ? owner.Id : p.Started.ProcessId;
                    p.Window = window;
                    ConsoleScreen screen;
                    try { screen = await injector.ReadScreenAsync(window); }
                    catch (Exception) { screen = new ConsoleScreen(null, "unreadable"); }
                    var question = StartupQuestions.Read(screen.Rows);
                    p.Ready = p.Launcher.RegistersBeforeReady && StartupQuestions.CopilotReady(screen.Rows);
                    // Read again on every look while it waits: what is shown to a person is what
                    // the window shows now.
                    if (question == StartupQuestion.Trust)
                    {
                        var read = StartupQuestions.ReadTrust(p.Launcher.Kind, screen.Rows, p.Launched.Cwd ?? "");
                        // A question whose root or words change is another question, under a new
                        // id: a command signed for the one shown must not answer it. The id and what
                        // it names are one value, so nobody ever sees one with the other's words.
                        var was = p.Trust;
                        p.Trust = was is not null && StartupQuestions.SameQuestion(read, was.Screen) ? was with { Screen = read } : new TrustSnapshot(Guid.NewGuid().ToString(), read);
                    }
                    if (question != p.Question)
                    {
                        p.Question = question;
                        if (question == StartupQuestion.Trust)
                        {
                            p.TrustAt ??= now;
                            log($"launch {Short(p.Key)}: {label} in {p.Where} is asking whether to trust the folder; {(RemoteFolderTrust ? "a person can answer it here or from their devices" : "left for a person at the machine")}");
                            p.Decided.TrySetResult(new LaunchResult(LaunchOutcome.Unconfirmed,
                                RemoteFolderTrust
                                ? $"{label} started, and is asking whether to trust {p.Where}. A person can answer that here or at the machine; the session appears here once they do"
                                : $"{label} started, and is asking whether to trust {p.Where}. Only a person at the machine can answer that; the session appears here once they do")
                            { Session = p.SessionId.Length > 0 ? p.SessionId : null, Launched = p.Launched, Trust = Question(p) });
                        }
                        else if (question == StartupQuestion.BypassPermissions)
                        {
                            // Turns other launches away as a trust question does, but is never one:
                            // not listed, not answerable, and said for what it is (#149).
                            p.TrustAt ??= now;
                            log($"launch {Short(p.Key)}: {label} in {p.Where} is asking to confirm Bypass Permissions mode (Allow all); left for a person at the machine");
                            p.Decided.TrySetResult(new LaunchResult(LaunchOutcome.Unconfirmed,
                                $"{label} started in {p.Where}, and is asking you to confirm Bypass Permissions mode (Allow all). Only a person at the machine can answer that; the session appears here once they do")
                            { Session = p.SessionId.Length > 0 ? p.SessionId : null, Launched = p.Launched });
                        }
                        else if (question == StartupQuestion.HooksReview)
                        {
                            log($"launch {Short(p.Key)}: {label} in {p.Where} is waiting on a question in its window (hooks need review)");
                            p.Decided.TrySetResult(new LaunchResult(LaunchOutcome.Unconfirmed,
                                $"{label} started in {p.Where}, and is asking in its window for its hooks to be trusted; answer it at the machine, and the session appears here")
                            { Launched = p.Launched });
                        }
                    }
                }
                // A question a person may answer from their devices is followed for longer, so it
                // can still be answered, and its answer seen, after the usual ten minutes.
                var followFor = (RemoteFolderTrust && p.Question == StartupQuestion.Trust) || Volatile.Read(ref p.Answering) != 0
                    ? TimeSpan.FromTicks(Math.Max(options.FollowFor.Ticks, options.TrustFollowFor.Ticks)) : options.FollowFor;
                // Never while an answer is being typed or its effect looked for: what it started is
                // only seen by following, so stopping now would read a session that started as not.
                if (now - p.At >= followFor && Volatile.Read(ref p.Answering) == 0)
                {
                    log(p.RegisteredAt is null
                        ? $"launch {Short(p.Key)}: {label} (pid {p.Started.ProcessId}) did not register within {followFor.TotalMinutes:0} minutes; stopped watching"
                        : $"launch {Short(p.Key)}: {label} in {p.Where} registered, but its window was not seen ready within {followFor.TotalMinutes:0} minutes; stopped watching");
                    return;
                }
                await Task.Delay(options.PollInterval, options.Time);
            }
        }
        catch (Exception e)
        {
            log($"launch {Short(p.Key)}: following it failed: {e.Message}");
        }
        finally
        {
            lock (pendingGate) { pending.Remove(p); }
            if (p.Reservation is { } token && p.Worktree is { } worktree)
            {
                // Only once it has registered: one still starting stays protected, and one that
                // never did is left locked for a person to look at, as agent-ha-bridge leaves it.
                // A session seen registered counts, ready or not.
                if (p.Decided.Task.IsCompletedSuccessfully && p.Decided.Task.Result.Outcome == LaunchOutcome.Started || p.RegisteredLate || p.RegisteredAt is not null)
                {
                    try { Worktrees.Release(worktree, token); } catch (Exception) { }
                }
            }
            p.Decided.TrySetResult(new LaunchResult(LaunchOutcome.Unconfirmed, $"{label} in {p.Where} was not seen to start; check its window") { Launched = p.Launched });
        }
    }

    // The session a launch produced, once it is live: by the id it was given, or for Codex, which
    // chooses its own, a new Codex session that started after the launch and runs in the process
    // the launch started or one under it. Only when that cannot be told (its process is not
    // known, or the launcher has gone) is timing enough, and then only while no other Codex
    // launch is waiting for its session, which could be this one. A session bound to one launch
    // is never bound to another.
    string? Registered(Pending p)
    {
        if (p.SessionId.Length > 0) { return source.Get(p.SessionId) is not null ? p.SessionId : null; }
        if (p.Bound is { } bound) { return bound; }
        List<Pending> others;
        lock (pendingGate) { others = [.. pending.Where(x => x != p && x.SessionId.Length == 0 && x.Bound is null)]; }
        foreach (var s in source.Snapshot())
        {
            if (s.Agent != AgentKind.Codex || !Candidate(p, s.Id, out var process)) { continue; }
            var own = process is null ? null : starter.Owns(p.Started, process.Id);
            if (own == false || (own is null && others.Any(x => Candidate(x, s.Id, out _)))) { continue; }
            lock (pendingGate)
            {
                if (launchedOn.ContainsKey(s.Id)) { continue; }
                launchedOn[s.Id] = p.Started;
                p.Bound = s.Id;
            }
            return s.Id;
        }
        return null;
    }

    // Whether a Codex session is new to a launch: not there when it started, and started after.
    bool Candidate(Pending p, string id, out SessionProcess? process)
    {
        process = source.ProcessOf(id);
        return !p.CodexBefore.Contains(id)
            && (process?.StartedAt ?? source.Get(id)?.StartedAt) is { } at && at >= p.Started.StartedAt - TimeSpan.FromSeconds(2);
    }

    // Whether a launch has started an agent on this session that is still running.
    bool Starting(string sessionId)
    {
        lock (pendingGate) { return launchedOn.TryGetValue(sessionId, out var started) && starter.Running(started); }
    }

    void Launched(string sessionId, Started started)
    {
        lock (pendingGate)
        {
            foreach (var gone in launchedOn.Where(x => !starter.Running(x.Value)).Select(x => x.Key).ToList()) { launchedOn.Remove(gone); }
            launchedOn[sessionId] = started;
        }
    }

    void Decide(Pending p, LaunchResult result)
    {
        if (!p.Decided.TrySetResult(result) && result.Outcome == LaunchOutcome.Started) { p.RegisteredLate = true; }
    }

    // agent-ha-bridge's Test-DaemonTrustAwaitingPress: a launched agent asking whether to trust
    // its folder, within the last two minutes, turns other launches away, so nothing is ever
    // taken as its answer and nothing new starts over it.
    string? BusyReason()
    {
        var now = options.Time.GetUtcNow();
        lock (pendingGate)
        {
            // A trust question first: it is the one shown beside the busy (BusyQuestion).
            bool Asking(Pending x, StartupQuestion q) => x.Question == q && x.TrustAt is { } at && now - at <= options.TrustBusyFor;
            var p = pending.FirstOrDefault(x => Asking(x, StartupQuestion.Trust)) ?? pending.FirstOrDefault(x => Asking(x, StartupQuestion.BypassPermissions));
            return p is null ? null
                : p.Question == StartupQuestion.BypassPermissions
                ? $"Busy: {p.Launcher.Label} in {p.Where} is waiting for a person at the machine to confirm Bypass Permissions mode (Allow all), so nothing was launched. Try again in a couple of minutes."
                : $"Busy: {p.Launcher.Label} in {p.Where} is waiting for a person to answer whether to trust that folder, so nothing was launched. Try again in a couple of minutes.";
        }
    }

    // The question that makes a launch busy, for a person to see beside it (and answer, where
    // they may).
    TrustQuestion? BusyQuestion()
    {
        var now = options.Time.GetUtcNow();
        Pending? p;
        lock (pendingGate) { p = pending.FirstOrDefault(x => x.Question == StartupQuestion.Trust && x.TrustAt is { } at && now - at <= options.TrustBusyFor); }
        return p is null ? null : Question(p);
    }

    // ---- launch.trust ----

    // The most a list carries: each is a few KiB, inside launch.options' budget.
    const int MostTrustQuestions = 4;

    // Folder-trust questions launched agents are waiting on now, newest first.
    public IReadOnlyList<TrustQuestion> TrustQuestions()
    {
        List<Pending> waiting;
        lock (pendingGate) { waiting = [.. pending.Where(x => x.Question == StartupQuestion.Trust && x.Trust is not null).OrderByDescending(x => x.TrustAt)]; }
        return [.. waiting.Take(MostTrustQuestions).Select(Question)];
    }

    // Why a folder-trust question cannot be answered from a person's device now, and what would
    // change that; null when it can.
    string? AtMachine(Pending? p, StartupQuestions.TrustScreen? screen)
    {
        if (!RemoteFolderTrust)
        {
            return "Answer at the machine. To answer from your devices, turn on \"Answer folder-trust questions from my devices\" in the Agent Bridge app's Settings on that machine, "
                + "or run agent-bridge-host config set launch.remoteFolderTrust true there and restart the host.";
        }
        if (probe.Active() is { } off) { return $"Answer at the machine: {off}."; }
        if (TrustDevices.Count == 0)
        {
            return "Answer at the machine. No device is approved yet to answer these from: approve yours in the Agent Bridge app's Settings on that machine, "
                + "or add its id to launch.remoteFolderTrustDevices there.";
        }
        if (p is { TrustTyped: true }) { return "Answer at the machine: an answer was already typed into its window, and what it did is for a person there to see and finish."; }
        if (p is not null && Volatile.Read(ref p.Answering) != 0) { return "It is being answered from a device now."; }
        if (screen is null) { return "Answer at the machine: its window could not be read in full, so nothing is typed into it from here."; }
        return null;
    }

    TrustQuestion Question(Pending p)
    {
        var shown = p.Trust;
        var screen = shown?.Screen;
        var why = AtMachine(p, screen);
        return new TrustQuestion
        {
            Id = shown?.Id ?? "",
            Agent = p.Launcher.Id,
            Kind = p.Launcher.Kind,
            Label = p.Launcher.Label,
            Workspace = p.Launched.Workspace,
            Folder = p.Launched.Cwd ?? p.Where,
            Root = screen?.Root,
            RootNote = screen?.RootNote,
            Warning = screen?.Warning,
            AskedAt = p.TrustAt ?? p.At,
            Answerable = why is null,
            AtMachine = why,
        };
    }

    // Answers a folder-trust question for a person, whose device the caller has checked: types
    // into that agent's own console, by its process, only while the window still shows exactly
    // the question it read, and then waits to see the session start (trust) or the agent close
    // (quit). Nothing is ever typed twice: a key that did not have its effect is reported, and
    // the question is left for the machine. By request, so a resend gets the first outcome.
    public Task<TrustAnswerResult> AnswerTrustAsync(Guid request, string questionId, bool trust, string device, string by, CancellationToken cancellationToken = default) =>
        trustAnswers.Once(request, () => AnswerTrustOnceAsync(questionId, trust, device, by), r => log($"folder-trust answer {Short(request)} from {by}: {r.Outcome.ToString().ToLowerInvariant()}{(r.Reason is { } why ? $" ({why})" : "")}"));

    static TrustAnswerResult TrustRefused(string reason) => new(TrustAnswerOutcome.Refused, reason);

    async Task<TrustAnswerResult> AnswerTrustOnceAsync(string questionId, bool trust, string device, string by)
    {
        Pending? p;
        lock (pendingGate) { p = pending.FirstOrDefault(x => x.Trust?.Id == questionId); }
        if (p is null || p.Question != StartupQuestion.Trust) { return TrustRefused("that question is no longer waiting: it was answered, or the agent closed"); }
        if (Stopped(device) is not null) { return TrustRefused(Refusal(device)); }
        if (Interlocked.CompareExchange(ref p.Answering, 1, 0) != 0) { return TrustRefused("that question is being answered from another device"); }
        var label = p.Launcher.Label;
        var choice = trust ? "trust" : "quit";
        // Whether a key of this answer may have reached the window: from then on nothing reads as
        // refused, which says nothing was typed.
        var wrote = false;
        try
        {
            // Never a second time: once a key may have reached its window, whatever it did is for
            // a person at the machine to see and finish.
            if (p.TrustTyped) { return TrustRefused("an answer was already typed into its window; answer at the machine"); }
            if (!AgentAlive(p)) { return TrustRefused($"{label} has closed"); }
            var window = p.Window ?? p.Started.ProcessId;
            var screen = await ReadTrustAsync(p, window);
            if (screen is null) { return TrustRefused("its window does not show that question in full now, so nothing was typed; answer at the machine"); }
            // The question the command was signed for, folder, root and words: one that changed is
            // another question, under another id.
            if (!Shown(p, questionId, screen)) { return TrustRefused("the question in its window has changed since it was shown; look at it again"); }

            // Trust: the highlight is moved to the option that trusts, the move is seen, and only
            // then is Enter pressed. Quit: Esc, which each of these agents reads as no.
            if (trust && screen.Selected != screen.TrustAt)
            {
                var steps = screen.TrustAt - screen.Selected;
                var move = string.Concat(Enumerable.Repeat(steps > 0 ? KeyPlan.Down : KeyPlan.Up, Math.Abs(steps)));
                // Asked again at every key, and again once the injector holds the typing slot:
                // agent-ha-bridge starting, the setting turned off or this device no longer
                // approved, while the window was read or another delivery typed, stops it there.
                if (Stopped(device) is not null) { return TrustRefused(Refusal(device)); }
                wrote = true;
                var moved = await injector.InjectAsync(window, new KeyPlan([new KeySend(move, false, 0, TrustKeyPauseMs, screen.Hold)]) { Proceed = () => Stopped(device) });
                wrote = moved.Status != InjectionStatus.NothingTyped;
                p.TrustTyped |= wrote;
                log($"launch {Short(p.Key)}: folder trust for {by}: {Math.Abs(steps)} {(steps > 0 ? "down" : "up")} -> {moved.Status} {moved.Detail}");
                if (moved.Status == InjectionStatus.NothingTyped)
                {
                    return Stopped(device) is not null ? TrustRefused(Refusal(device)) : TrustRefused($"its window changed before anything was typed ({moved.Detail}); answer at the machine");
                }
                if (moved.Status != InjectionStatus.Typed) { return TrustUnconfirmed("the highlight may have moved, but whether it did is not known"); }
                var asked = screen;
                screen = await ReadTrustAsync(p, window);
                if (screen is null || screen.Selected != screen.TrustAt) { return TrustUnconfirmed("the highlight was not seen on the option that trusts the folder, so Enter was not pressed"); }
                // Enter's expectations come from this read, so it must be the question the command
                // was signed for: the same as before the move, and still under that id.
                if (!StartupQuestions.SameQuestion(screen, asked) || !Shown(p, questionId, screen)) { return TrustUnconfirmed("the question in its window changed after the highlight moved, so Enter was not pressed"); }
            }
            if (Stopped(device) is { } halt) { return wrote ? TrustUnconfirmed($"the highlight was moved, but Enter was not pressed: {halt}") : TrustRefused(Refusal(device)); }
            var plan = new KeyPlan([new KeySend(trust ? "" : KeyPlan.Escape, trust, 0, 0, screen.Hold)]) { Proceed = () => Stopped(device) };
            var before = wrote;
            wrote = true;
            var typed = await injector.InjectAsync(window, plan);
            wrote = before || typed.Status != InjectionStatus.NothingTyped;
            p.TrustTyped |= wrote;
            log($"launch {Short(p.Key)}: folder trust for {by}: {choice}: {plan.Describe()} -> {typed.Status} {typed.Detail}");
            if (typed.Status == InjectionStatus.NothingTyped)
            {
                var stopped = Stopped(device);
                return before
                    ? TrustUnconfirmed(stopped is not null ? $"the highlight was moved, but Enter was not pressed: {stopped}" : "the highlight was moved, but its window changed before Enter, so Enter was not pressed")
                    : stopped is not null ? TrustRefused(Refusal(device)) : TrustRefused($"its window changed before anything was typed ({typed.Detail}); answer at the machine");
            }
            // Esc that closed the agent can end the worker's attach before it says it is done
            // (interrupted): whether Quit worked is then what the agent did, which is looked for.
            // A Trust not known to have gone whole is not.
            if (typed.Status != InjectionStatus.Typed && trust) { return TrustUnconfirmed($"the {choice} may not have reached {label}"); }
            if (!trust) { p.Declined = by; }
            return await ConfirmTrustAsync(p, trust);
        }
        catch (Exception e)
        {
            // A worker that failed part way may have typed: settled, so a resend gets this too.
            if (!wrote) { return TrustRefused($"nothing was typed ({e.Message}); answer at the machine"); }
            p.TrustTyped = true;
            log($"launch {Short(p.Key)}: folder trust for {by}: {choice} failed part way: {e.Message}");
            return TrustUnconfirmed($"the {choice} may not have reached {label} ({e.Message})");
        }
        finally { Volatile.Write(ref p.Answering, 0); }
    }

    const int TrustKeyPauseMs = 300;

    // Whether the window still shows the question that id named when it was shown: one value, read
    // once, so an id is never checked against another read's words.
    static bool Shown(Pending p, string questionId, StartupQuestions.TrustScreen screen) =>
        p.Trust is { } now && now.Id == questionId && StartupQuestions.SameQuestion(screen, now.Screen);

    // Why no key may go now for this device, asked before each one.
    string? Stopped(string device) =>
        probe.Active() ?? (!RemoteFolderTrust ? "answering from devices was turned off at the machine"
            : TrustDevices.Count == 0 ? "no device is approved to answer from any more"
            : !TrustDevices.Contains(device) ? "this device is no longer approved to answer from" : null);

    // What a refused device is told: the machine's reason, or how to approve this device.
    string Refusal(string device) =>
        !RemoteFolderTrust || probe.Active() is not null || TrustDevices.Count == 0 ? AtMachine(null, null)! : NotApproved(device);

    // For a device not approved: what a person at the machine does to approve it.
    public static string NotApproved(string device) =>
        $"Answer at the machine, or approve this device ({device[..Math.Min(8, device.Length)]}) to answer folder-trust questions in the Agent Bridge app's Settings on that machine.";

    static TrustAnswerResult TrustUnconfirmed(string what) => new(TrustAnswerOutcome.Unconfirmed, $"Not confirmed: answer at the machine. {char.ToUpperInvariant(what[0])}{what[1..]}.");

    // The agent's own process: the launcher's, or, once that handed the session on and exited,
    // the session's own, by its start time where discovery knows it.
    bool AgentAlive(Pending p)
    {
        if (!p.HandedOn) { return starter.Running(p.Started); }
        if (p.LiveId is not { } id || source.ProcessOf(id) is not { } owner) { return false; }
        return owner.StartedAt is not { } at || starter.Running(new Started(owner.Id, at));
    }

    async Task<StartupQuestions.TrustScreen?> ReadTrustAsync(Pending p, int window)
    {
        try
        {
            var screen = await injector.ReadScreenAsync(window);
            return StartupQuestions.ReadTrust(p.Launcher.Kind, screen.Rows, p.Launched.Cwd ?? "");
        }
        catch (Exception) { return null; }
    }

    async Task<bool> StillAskingAsync(Pending p)
    {
        try { return StartupQuestions.Read((await injector.ReadScreenAsync(p.Window ?? p.Started.ProcessId)).Rows) == StartupQuestion.Trust; }
        catch (Exception) { return false; }
    }

    // Trust is confirmed by the session starting (registered, and for Copilot its window ready),
    // which only happens once the question has gone; quit by the agent closing.
    async Task<TrustAnswerResult> ConfirmTrustAsync(Pending p, bool trust)
    {
        var label = p.Launcher.Label;
        var started = options.Time.GetTimestamp();
        while (options.Time.GetElapsedTime(started) < options.TrustConfirmWait)
        {
            await Task.Delay(options.PollInterval, options.Time);
            // Discovery runs on its own every second; looking here does not wait a pass.
            try { await source.RefreshAsync(); } catch (Exception) { }
            if (trust)
            {
                // Registered, and for Copilot its window ready: then the question is gone, which
                // is read once more for an agent that registers without its window being read.
                if (p.LiveId is { } id && (!p.Launcher.RegistersBeforeReady || p.Ready) && !await StillAskingAsync(p))
                {
                    return new TrustAnswerResult(TrustAnswerOutcome.Started) { Session = id };
                }
                if (p.LiveId is null && !AgentAlive(p)) { return TrustUnconfirmed($"{label} closed after the answer, before its session was seen"); }
            }
            else if (!AgentAlive(p))
            {
                // No longer asking from now, not from when following sees it gone after its grace:
                // a client asks again on this outcome, and found the launch still busy (live, the
                // app after a Quit from it).
                p.Question = StartupQuestion.None;
                return new TrustAnswerResult(TrustAnswerOutcome.Declined, $"{label} was not trusted with {p.Where}, and closed; nothing was launched");
            }
        }
        return TrustUnconfirmed(trust
            ? p.Question == StartupQuestion.Trust ? $"the question is still in {label}'s window" : $"{label}'s session was not seen to start within {options.TrustConfirmWait.TotalSeconds:0} s"
            : $"{label} was not seen to close within {options.TrustConfirmWait.TotalSeconds:0} s");
    }
    // A waiting folder-trust question as last read: its id and what it names, together.
    sealed record TrustSnapshot(string Id, StartupQuestions.TrustScreen? Screen);

    sealed class Pending(Guid key, Launcher launcher, string sessionId, Started started, string where, DateTimeOffset at, HashSet<string> codexBefore,
        string? worktree, string? reservation, LaunchedWith launched)
    {
        public Guid Key { get; } = key;
        public Launcher Launcher { get; } = launcher;
        public string SessionId { get; } = sessionId;
        public Started Started { get; } = started;
        public string Where { get; } = where;
        public DateTimeOffset At { get; } = at;
        public HashSet<string> CodexBefore { get; } = codexBefore;
        public string? Worktree { get; } = worktree;
        public string? Reservation { get; } = reservation;
        public LaunchedWith Launched { get; } = launched;
        public TaskCompletionSource<LaunchResult> Decided { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile StartupQuestion Question;
        public DateTimeOffset? TrustAt { get; set; }
        // While a folder-trust question waits: its id, and what its window last showed (null
        // when that could not be read in full). Answering is 1 while an answer is being typed.
        public volatile TrustSnapshot? Trust;
        public int Answering;
        // Whether a key of an answer from a device may have reached its window: then no other is.
        public volatile bool TrustTyped;
        // The window followed (the launcher's, or the process it handed the session on to), the
        // session once it is live, and who declined to trust the folder.
        public int? Window { get; set; }
        public volatile string? LiveId;
        public volatile string? Declined;
        // A launcher that registers before its window is ready: whether the last look found it
        // ready, when its session registered, whether its launch has already answered for a
        // window never seen ready, and whether its launcher exited, handing the session on.
        public volatile bool Ready;
        public DateTimeOffset? RegisteredAt { get; set; }
        public bool ReadyMissed { get; set; }
        public bool HandedOn { get; set; }
        public bool RegisteredLate { get; set; }
        public string? Bound { get; set; }
    }

    static string Short(Guid key) => key.ToString()[..8];

    // Says what is holding it, as agent-ha-bridge's launch note learned to: "discovery is
    // incomplete" alone sent somebody through the log for the process responsible, on a
    // machine where every launch had been refused all day.
    DiscoveryHeld? HeldFor(Plan plan) =>
        source.Held is { } held && held.Agents.Contains(plan.Launcher.Kind) && (plan.Resume is not null || plan.Launcher.ChoosesOwnSessionId) ? held : null;

    static string HeldReason(DiscoveryHeld held) =>
        $"Held: session discovery is waiting on {held.Reason}, so whether that session is still running cannot be told yet. Try again in a minute or two.";

    static string Short(string id) => id.Length > 8 ? id[..8] : id;

    static LaunchResult Refused(string reason) => new(LaunchOutcome.Refused, reason);
}
