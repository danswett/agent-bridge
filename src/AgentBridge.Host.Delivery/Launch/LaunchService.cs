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

    // How long a trust question keeps other launches busy: agent-ha-bridge's two minutes.
    public TimeSpan TrustBusyFor { get; init; } = TimeSpan.FromSeconds(120);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    // How long the window is given to draw before it is read, and how often it is read after.
    public TimeSpan ScreenAfter { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan ScreenEvery { get; init; } = TimeSpan.FromSeconds(1);

    // How long a started process that exited is given for its session to show, for a launcher
    // that hands the session on to another process and exits.
    public TimeSpan ExitGrace { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan RememberFor { get; init; } = TimeSpan.FromMinutes(15);

    public TimeProvider Time { get; init; } = TimeProvider.System;

    // Folders no session is launched in (Folders.IsSystemDirectory). Temp is one, which is
    // where tests keep their workspaces, so they replace it.
    public Func<string, bool> IsSystemDirectory { get; init; } = Folders.IsSystemDirectory;

    // The clock a worktree's name is stamped from: local time, as agent-ha-bridge names them.
    public Func<DateTime> LocalNow { get; init; } = () => DateTime.Now;
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
//     launches are busy, so a second press is never taken for an answer.
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
    }

    LaunchConfig Config => options.Config;

    public bool Available => Config.Enabled && options.PlatformSupported && probe.Active() is null && starter.Unavailable() is null;

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
        var excluded = new HashSet<string>(live.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
        // And any a launch has started an agent on that has not registered yet.
        lock (pendingGate) { excluded.UnionWith(launchedOn.Where(x => starter.Running(x.Value)).Select(x => x.Key)); }
        var offered = new List<Offered>();
        var labels = new HashSet<string>(StringComparer.Ordinal);
        var prefixed = candidates.Select(e => e.Agent).Distinct().Count() > 1;
        foreach (var e in candidates.Where(e => excluded.Add(e.Id)).OrderByDescending(e => e.Updated ?? DateTimeOffset.MinValue))
        {
            if (offered.Count >= Config.ResumeCount) { break; }
            if (e.Folder is not { Length: > 0 } folder || Approved(folder) is not { } workspace) { continue; }
            var title = Title(e, prefixed);
            if (!labels.Add(title)) { title = Limits.KeepStart($"{title} ({e.Id[..8]})", LaunchProtocol.MaxLabel * 2)!; labels.Add(title); }
            offered.Add(new Offered(e, title, workspace.Label));
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

    // The approved workspace a folder is in, directly or through a worktree this host made for
    // an isolated one; null when it is in none.
    WorkspaceConfig? Approved(string folder)
    {
        if (!Path.IsPathFullyQualified(folder) || Folders.Physical(folder) is not { } physical || options.IsSystemDirectory(physical)) { return null; }
        return Folders.Containing(Config.Workspaces, physical) ?? Worktrees.Managed(Config.Workspaces, physical)?.Workspace;
    }

    // ---- session.launch ----

    public Task<LaunchResult> LaunchAsync(LaunchRequest request, string peer, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(request.Request, "D", out var key)) { return Task.FromResult(Refused("request must be a UUID")); }
        return requests.Once(key, () => RunAsync(request, key, peer), result =>
            log($"launch {Short(key)}: {result.Outcome.ToString().ToLowerInvariant()}{(result.Session is { } s ? $" session {Short(s)}" : "")}{(result.Reason is null ? "" : $" ({result.Reason})")}"));
    }

    // Everything a launch was settled on before it starts.
    sealed record Plan(Launcher Launcher, string Executable, string Directory, string? Workspace, WorkspaceConfig? Isolated, ResumeEntry? Resume,
        string Where, string Profile, string Model, string Effort, string Context, bool AllowAll, string Prompt);

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
            if (BusyReason() is { } busy) { return new LaunchResult(LaunchOutcome.Busy, busy); }

            var resolved = await ResolveAsync(CancellationToken.None);
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
            try { return await StartAsync(plan, claim, key, handed); }
            finally
            {
                // Anything not handed to the agent stays held for another try.
                if (claim is not null && !handed.Value) { attachments!.Release(claim); }
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
        if (r.Resume is { } resumeId)
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
            profile, model, effort, context, allowAll, LaunchProtocol.Flatten(r.Prompt)), null);
    }

    async Task<LaunchResult> StartAsync(Plan plan, Claim? claim, Guid key, StrongBox<bool> handed)
    {
        Pending follow;
        LaunchedWith launched;
        await starting.WaitAsync();
        string? reservation = null, reserved = null;
        try
        {
            // Checked again: agent-ha-bridge may have started while this one waited its turn.
            if (probe.Active() is { } active) { return Refused(active); }
            if (BusyReason() is { } busy) { return new LaunchResult(LaunchOutcome.Busy, busy); }
            // And the session to resume, which another press may have resumed while this one
            // waited: two agents on one transcript would corrupt it.
            if (plan.Resume is { } again && (source.Get(again.Id) is not null || Starting(again.Id)))
            {
                return Refused("that session is already running; two agents on one session would corrupt it");
            }

            var directory = plan.Directory;
            string? worktree = null;
            if (plan.Isolated is { } isolated)
            {
                var made = await Task.Run(() => Worktrees.Create(directory, Config, options.LocalNow));
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

            var sessionId = plan.Resume?.Id ?? (plan.Launcher.ChoosesOwnSessionId ? "" : Guid.NewGuid().ToString());
            var args = Launchers.Arguments(plan.Launcher, sessionId, plan.Resume is not null, plan.Model, plan.Effort, plan.Context, plan.AllowAll,
                Config.Agent(plan.Launcher.Id).ExtraArgs, plan.Profile, prompt);
            var spec = new StartSpec(plan.Executable, args, directory);
            if (starter.CommandLine(spec, out var tooLong) is null)
            {
                return Refused($"the prompt is too long to start {plan.Launcher.Label} with on this machine: {tooLong}; shorten it, or send the rest as a reply once it has started");
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
            };
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
        var unconfirmed = new LaunchResult(LaunchOutcome.Unconfirmed,
            $"{plan.Launcher.Label} started in {plan.Where} (pid {follow.Started.ProcessId}), but its session had not appeared after {options.RegistrationWait.TotalSeconds:0} s; it may still be starting, so check its window")
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
                if (Registered(p) is { } id)
                {
                    log($"launch {Short(p.Key)}: {label} in {p.Where} registered as session {Short(id)} after {(now - p.At).TotalSeconds:0.0} s");
                    Decide(p, new LaunchResult(LaunchOutcome.Started) { Session = id, Launched = p.Launched });
                    return;
                }
                if (!starter.Running(p.Started))
                {
                    // A launcher that hands the session to another process may exit first: its
                    // session is given a moment to show before the launch counts as failed.
                    await Task.Delay(options.ExitGrace, options.Time);
                    try { await source.RefreshAsync(); } catch (Exception) { }
                    if (Registered(p) is { } late)
                    {
                        Decide(p, new LaunchResult(LaunchOutcome.Started) { Session = late, Launched = p.Launched });
                        return;
                    }
                    log($"launch {Short(p.Key)}: {label} (pid {p.Started.ProcessId}) exited before its session registered");
                    Decide(p, new LaunchResult(LaunchOutcome.Failed, $"{label} in {p.Where} closed before it started; start it at the machine to see why") { Launched = p.Launched });
                    return;
                }
                if (now - p.At >= options.ScreenAfter && now - lastScreen >= options.ScreenEvery)
                {
                    lastScreen = now;
                    ConsoleScreen screen;
                    try { screen = await injector.ReadScreenAsync(p.Started.ProcessId); }
                    catch (Exception) { screen = new ConsoleScreen(null, "unreadable"); }
                    var question = StartupQuestions.Read(screen.Rows);
                    if (question != p.Question)
                    {
                        p.Question = question;
                        if (question == StartupQuestion.Trust)
                        {
                            p.TrustAt ??= now;
                            log($"launch {Short(p.Key)}: {label} in {p.Where} is asking whether to trust the folder; left for a person at the machine");
                            p.Decided.TrySetResult(new LaunchResult(LaunchOutcome.Unconfirmed,
                                $"{label} started, and is asking whether to trust {p.Where}. Only a person at the machine can answer that; the session appears here once they do")
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
                if (now - p.At >= options.FollowFor)
                {
                    log($"launch {Short(p.Key)}: {label} (pid {p.Started.ProcessId}) did not register within {options.FollowFor.TotalMinutes:0} minutes; stopped watching");
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
                if (p.Decided.Task.IsCompletedSuccessfully && p.Decided.Task.Result.Outcome == LaunchOutcome.Started || p.RegisteredLate)
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
            var p = pending.FirstOrDefault(x => x.Question == StartupQuestion.Trust && x.TrustAt is { } at && now - at <= options.TrustBusyFor);
            return p is null ? null
                : $"Busy: {p.Launcher.Label} in {p.Where} is waiting for a person to answer whether to trust that folder, so nothing was launched. Try again in a couple of minutes.";
        }
    }

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
        public StartupQuestion Question { get; set; }
        public DateTimeOffset? TrustAt { get; set; }
        public bool RegisteredLate { get; set; }
        public string? Bound { get; set; }
    }

    static string Short(Guid key) => key.ToString()[..8];

    static string Short(string id) => id.Length > 8 ? id[..8] : id;

    static LaunchResult Refused(string reason) => new(LaunchOutcome.Refused, reason);
}
