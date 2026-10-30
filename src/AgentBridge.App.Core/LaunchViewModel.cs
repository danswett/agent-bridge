using System.Text;
using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// One entry of a picker: what is sent, and what the person reads.
public sealed record Choice(string Value, string Label)
{
    public override string ToString() => Label;
}

// The Launch page (pwa/js/ui/launch-view.js, which follows agent-ha-bridge's launch card):
// starting or resuming a session on any of the account's machines that can (sessions.launch).
// What it offers comes from that machine (launch.options), never from here: an agent,
// workspace or model the machine does not list cannot be picked. The machine's Deliveries send
// each press, and resend it with the same request until there is an outcome; once a session
// has started the window goes to its card. The prompt goes whole, up to 16 KiB.
//
// Where the machine takes sessions from the account's other machines (sessions.transfer), Resume
// also lists the sessions they offer (sessions.transfer.serve), by machine. Picking one brings
// its transcript over as a new session, in the workspace picked, with a command this app signs as
// a person's device for the machine it is on (Transfers, v1-transfer.md); the original stays. A
// transfer to this machine goes to its host through the relay, never the pipe (Fleet.Loopback).
public sealed class LaunchViewModel : Observable
{
    public const string ApproveHint = "agent-bridge-host workspace add <folder>";
    const string Key = "launch";
    const string AgentDefault = "";
    // A launch waits for its session to register (60 s on the host) and, for Copilot, for its
    // window to be ready, as the PWA's launch does.
    static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(120);
    // launch.options can take a minute on a slow machine: Agency's profiles are two commands of up
    // to 20 s each, and listing resumable sessions another 20 s.
    static readonly TimeSpan OptionsTimeout = TimeSpan.FromSeconds(90);

    readonly MainViewModel main;
    readonly IUiDispatcher ui;
    Machine? machine;
    LaunchOptions? options;
    string? error;
    int loads;
    bool loading;
    bool filling;
    string scope = "";
    bool permissionsSet;
    string? handled;
    // Every machine's launches are followed, not only the one on screen: one pressed before
    // switching machines still opens its session when it starts.
    readonly Dictionary<Machine, Action<Delivery>> watching = [];
    // The last launch pressed here, whichever machine it went to: its progress and outcome stay on
    // the page until the next press, whatever is picked since. One is on its way at a time. lost:
    // its machine left before saying what became of it.
    Machine? lastMachine;
    string lastName = "";
    Delivery? last;
    string? lost;
    // The machine last picked by hand, picked again when it comes back after dropping out.
    string? wanted;
    // What was picked on each machine, by its key: a machine that drops out for a moment, or is
    // left for another and picked again, comes back with the choices still offered as they were.
    readonly Dictionary<string, Picks> picked = new(StringComparer.Ordinal);
    // Whether the page is on screen: its choices are fetched only then.
    bool shown;
    sealed record Picks(Choice? Resume, Choice? Agent, Choice? Workspace, Choice? Profile, Choice? Model, Choice? Effort, Choice? Context, Choice? Permissions, bool PermissionsSet, string Scope);
    string prompt = "";
    // What stops the form being launched now (no prompt for Codex, too long...): about the form,
    // not the last launch, so it is said apart from that launch's outcome and its Try again.
    string problem = "";
    // What the account's other machines offer to bring here, for the machine shown; and whether
    // they have been asked yet, since a session picked from one before is kept until they answer.
    IReadOnlyList<TransferSource> sources = [];
    bool sourcesKnown;
    // Where the last launch went: its machine's Deliveries, or for a transfer to this machine, the
    // loopback's; and for a transfer, what it was brought from, to sign again for a new request.
    Machine? lastVia;
    TransferSource? lastSource;
    Machine? watchedLoopback;
    Fleet? watchedFleet;

    public LaunchViewModel(MainViewModel main, IUiDispatcher ui)
    {
        this.main = main;
        this.ui = ui;
        main.MachinesUpdated += MachinesUpdated;
        MachinesUpdated();
    }

    // The loopback's launches are followed as this machine's, and what it offers redraws the page.
    void WatchLoopback()
    {
        if (main.Fleet is { } f && f != watchedFleet)
        {
            watchedFleet = f;
            f.LoopbackChanged += () => ui.Post(() => { WatchLoopback(); if (machine?.IsLocal == true) { Draw(); } });
        }
        var now = main.Fleet?.CurrentLoopback;
        if (now == watchedLoopback) { return; }
        if (watchedLoopback is { } was)
        {
            // Dropped (another account): the local machine stays in AllMachines, so its leaving is
            // never seen there, and a launch through it would wait for ever with Launch disabled.
            // One whose outcome is in is shown as it ended; one still on its way is unconfirmed.
            if (lastVia == was && lost is null && last is not null && main.MachineOf(Machine.LocalKey) is { } local)
            {
                if (was.Deliveries.Get(Key) is { State: DeliveryState.Done or DeliveryState.Failed } final && final.Request == last.Request) { OnDelivery(local, final, was); }
                else if (Pending)
                {
                    lost = "Not confirmed: the relay connection to this machine's host changed before it said whether the launch started. Check at this machine before launching again.";
                    Show();
                }
            }
            was.Deliveries.Changed -= OnLoopback;
        }
        watchedLoopback = now;
        if (now is not null) { now.Deliveries.Changed += OnLoopback; }
        // Try again goes only through the loopback in use (ShowResend).
        Show();
    }

    void OnLoopback(Delivery d)
    {
        if (main.MachineOf(Machine.LocalKey) is { } local) { OnDelivery(local, d, watchedLoopback); }
    }

    // The machines that can launch now, this one first.
    public IReadOnlyList<Choice> Machines { get; private set; } = [];
    public bool Available => Machines.Count > 0;
    public bool ShowMachines => Machines.Count > 1;
    Choice? selectedMachine;
    public Choice? SelectedMachine
    {
        get => selectedMachine;
        set
        {
            if (filling || value is null || value.Value == selectedMachine?.Value) { return; }
            selectedMachine = value;
            wanted = value.Value;
            Raise();
            Target(main.MachineOf(value.Value));
        }
    }

    public string Title => machine is null ? "Start a session" : $"Start a session on {machine.Name}";
    string Name => machine?.Name ?? "this machine";

    public IReadOnlyList<Choice> Resumes { get; private set; } = [];
    public IReadOnlyList<Choice> Agents { get; private set; } = [];
    public IReadOnlyList<Choice> Workspaces { get; private set; } = [];
    public IReadOnlyList<Choice> Profiles { get; private set; } = [];
    public IReadOnlyList<Choice> Models { get; private set; } = [];
    public IReadOnlyList<Choice> Efforts { get; private set; } = [];
    public IReadOnlyList<Choice> Contexts { get; private set; } = [];
    public IReadOnlyList<Choice> PermissionChoices { get; } = [new(LaunchProtocol.Ask, "Ask permission"), new(LaunchProtocol.AllowAll, "Allow all")];

    Choice? resume, agent, workspace, profile, model, effort, context, permissions;
    public Choice? Resume { get => resume; set => Pick(ref resume, value, redraw: true); }
    public Choice? Agent { get => agent; set => Pick(ref agent, value, redraw: true); }
    public Choice? Workspace { get => workspace; set => Pick(ref workspace, value); }
    public Choice? Profile { get => profile; set => Pick(ref profile, value); }
    public Choice? Model { get => model; set => Pick(ref model, value); }
    public Choice? Effort { get => effort; set => Pick(ref effort, value); }
    public Choice? Context { get => context; set => Pick(ref context, value); }
    public Choice? Permissions { get => permissions; set => Pick(ref permissions, value); }

    public string Prompt
    {
        get => prompt;
        set
        {
            if (!Set(ref prompt, value ?? "")) { return; }
            Raise(nameof(CountText));
            Raise(nameof(CountTooLong));
        }
    }
    public string CountText
    {
        get
        {
            var n = Encoding.UTF8.GetByteCount(prompt);
            return n > LaunchProtocol.MaxPrompt * 0.8 ? $"{(n + 1023) / 1024} of {LaunchProtocol.MaxPrompt / 1024} KiB" : "";
        }
    }
    public bool CountTooLong => Encoding.UTF8.GetByteCount(prompt) > LaunchProtocol.MaxPrompt;
    public string PromptPlaceholder { get; private set; } = "What should the session do first? (optional)";

    public bool ShowForm => options is not null;
    public bool ShowResume { get; private set; }
    public bool ShowWorkspace { get; private set; }
    public bool ShowProfile { get; private set; }
    public bool ShowModel { get; private set; }
    public bool ShowEffort { get; private set; }
    public bool ShowContext { get; private set; }
    public bool ShowReload => options is not null || error is not null;
    public bool CanReload => !loading;

    public string Note { get; private set; } = "";
    public bool NoteWarns { get; private set; }
    public string Busy => options?.Busy is { Length: > 0 } b ? $"{Name} is busy: {Sentence(b)}" : "";

    public string StatusText { get; private set; } = "";
    public string Problem => problem;

    void SetProblem(string text)
    {
        if (problem == text) { return; }
        problem = text;
        Raise(nameof(Problem));
    }
    public Tone StatusTone { get; private set; }
    public bool CanLaunch => options is not null && !Pending;
    // Try again: a launch that could not be sent goes again with the same request; one the machine
    // reports failed (the agent did not start) goes as a new one, since the machine keeps the
    // failure as that request's outcome.
    public bool ShowResend => lost is null && (last?.State == DeliveryState.Failed || last is { State: DeliveryState.Done } d && OutcomeOf(d) == "failed")
        && lastMachine is { } m && main.AllMachines.Contains(m)
        // Only through the machine's own connection or the loopback in use: one retired by an
        // account switch is disposed, and a request sent through it would wait for ever.
        && (lastVia is null || lastVia == m || lastVia == watchedLoopback);
    public bool ShowOpen => lost is null && last is { State: DeliveryState.Done } e && Result(e)?.Session is not null;
    // What the last launch came to, for tests and the self-test: none, sending, waiting, failed or its outcome.
    public string State => lost is not null ? "unconfirmed" : last is not { } e ? "none" : e.State != DeliveryState.Done ? e.State.ToString().ToLowerInvariant() : OutcomeOf(e);

    bool Pending => lost is null && last?.State is DeliveryState.Sending or DeliveryState.Waiting;

    void Pick(ref Choice? field, Choice? value, bool redraw = false, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        // A picker that lost its items while they were replaced says null: that is not a choice.
        if (filling || value is null || value == field) { return; }
        field = value;
        Raise(name);
        if (redraw) { Draw(); }
    }

    void MachinesUpdated()
    {
        WatchLoopback();
        foreach (var m in main.AllMachines.Where(m => !watching.ContainsKey(m)).ToList())
        {
            void Watch(Delivery d) => OnDelivery(m, d);
            watching[m] = Watch;
            m.Deliveries.Changed += Watch;
        }
        foreach (var (m, watch) in watching.Where(p => !main.AllMachines.Contains(p.Key)).ToList())
        {
            // An outcome already in, whose notice is still on its way, is handled before letting go.
            // One still on its way leaves with the machine (one that comes back is a new one, with no
            // memory of it), and the session may have started.
            if (m == lastMachine && lost is null && last is not null)
            {
                // Done or failed: shown as it ended, so the page is not left waiting on it.
                if ((lastVia ?? m).Deliveries.Get(Key) is { State: DeliveryState.Done or DeliveryState.Failed } final && final.Request == last.Request) { OnDelivery(m, final, lastVia); }
                else if (Pending)
                {
                    lost = $"Not confirmed: {m.Name} left before saying whether the launch started. Check at that machine before launching again.";
                }
            }
            m.Deliveries.Changed -= watch;
            watching.Remove(m);
        }
        var list = main.AllMachines.Where(m => m.Host.Can(LaunchProtocol.Capability))
            .OrderBy(m => m.IsLocal ? 0 : 1)
            .Select(m => new Choice(m.Key, m.IsLocal ? $"{m.Name} (this machine)" : m.Name)).ToList();
        if (list.SequenceEqual(Machines))
        {
            Show();
            return;
        }
        Machines = list;
        filling = true;
        try { Raise(nameof(Machines)); }
        finally { filling = false; }
        Raise(nameof(Available));
        Raise(nameof(ShowMachines));
        var keep = list.FirstOrDefault(c => c.Value == wanted) ?? list.FirstOrDefault(c => c.Value == selectedMachine?.Value) ?? list.FirstOrDefault();
        if (keep?.Value != selectedMachine?.Value) { selectedMachine = keep; Target(keep is null ? null : main.MachineOf(keep.Value)); }
        else { selectedMachine = keep; }
        Raise(nameof(SelectedMachine));
        Raise(nameof(Title));
        Show();
    }

    void Target(Machine? next)
    {
        // Saved even after a failed reload, when the picks are still in place though not shown.
        if (machine is { } was && new[] { resume, agent, workspace, profile, model, effort, context, permissions }.Any(c => c is not null))
        {
            picked[was.Key] = new Picks(resume, agent, workspace, profile, model, effort, context, permissions, permissionsSet, scope);
        }
        machine = next;
        options = null;
        sources = [];
        sourcesKnown = false;
        error = null;
        SetProblem("");
        loads++;
        loading = false;
        // Another machine's choices: nothing picked on the last one carries over but the prompt.
        // What was picked on this one before is kept, for as far as it is still offered (Draw).
        if (next is not null && picked.TryGetValue(next.Key, out var p))
        {
            (resume, agent, workspace, profile, model, effort, context, permissions, permissionsSet, scope) =
                (p.Resume, p.Agent, p.Workspace, p.Profile, p.Model, p.Effort, p.Context, p.Permissions, p.PermissionsSet, p.Scope);
        }
        else
        {
            resume = agent = workspace = profile = model = effort = context = permissions = null;
            permissionsSet = false;
            scope = "";
        }
        Raise(nameof(Title));
        Draw();
        Show();
        if (machine is not null && shown) { _ = RefreshAsync(); }
    }

    // The page was opened: a workspace approved, an agent installed or a session ended since.
    public void Opened()
    {
        shown = true;
        if (machine is not null && !loading) { _ = RefreshAsync(); }
    }

    // launch.options again.
    // Left for another page: the choices are fetched again when it is opened, not as machines come
    // and go meanwhile. launch.options is costly at the host (profiles, resumable folders).
    public void Closed() => shown = false;

    public async Task RefreshAsync()
    {
        var m = machine;
        if (m is null) { return; }
        var load = ++loads;
        loading = true;
        error = null;
        Draw();
        // The other machines' sessions are added when they arrive: asking each takes a handshake,
        // and one slow to answer must not hold up this machine's own choices.
        _ = SourcesAsync(m, load);
        LaunchOptions? got = null;
        string? failed = null;
        try { got = Known(await m.Host.CallAsync<LaunchOptions>(LaunchProtocol.Options, timeout: OptionsTimeout)); }
        catch (Exception e) when (e is IOException or HostErrorException or TimeoutException or JsonException) { failed = e.Message; }
        var done = new TaskCompletionSource();
        ui.Post(() =>
        {
            if (load == loads && m == machine)
            {
                loading = false;
                // A reload that fails says so, rather than leaving choices that may be out of date.
                if (got is not null) { options = got; } else { options = null; error = failed; }
                Draw();
            }
            done.SetResult();
        });
        await done.Task;
    }

    async Task SourcesAsync(Machine m, int load)
    {
        IReadOnlyList<TransferSource> got = [];
        if (main.Fleet is { } f && DeviceOf(m) is { } target)
        {
            if (m.IsLocal)
            {
                // This machine takes transfers only through the relay: its own host is asked there.
                var lb = f.Loopback();
                if (lb is not null)
                {
                    ui.Post(WatchLoopback);
                    try { await lb.Host.WhenConnectedAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
                    catch (TimeoutException) { }
                }
            }
            var takes = new TaskCompletionSource<bool>();
            ui.Post(() => takes.SetResult(TakesTransfers(m)));
            if (await takes.Task) { got = await f.TransferSourcesAsync(target, CancellationToken.None); }
        }
        ui.Post(() =>
        {
            if (load != loads || m != machine) { return; }
            sources = got;
            sourcesKnown = true;
            if (options is not null) { Draw(); }
        });
    }

    // An agent or launcher this app does not know cannot be launched from it, so it is left out.
    static LaunchOptions Known(LaunchOptions o) => o with
    {
        Agents = [.. o.Agents.Where(a => LaunchProtocol.Agents.Contains(a.Id))],
        Resumable = [.. o.Resumable.Where(r => LaunchProtocol.Agents.Contains(r.Launcher))],
    };

    LaunchAgent? AgentOf(string? id) => options?.Agents.FirstOrDefault(a => a.Id == id);
    ResumableSession? Resumed => options?.Resumable.FirstOrDefault(r => r.Id == resume?.Value && r.Id.Length > 0);

    // A session picked from another machine ("@<host>/<id>"), and the machine.
    (TransferSource From, ResumableSession Session)? Brought
    {
        get
        {
            if (resume?.Value is not { Length: > 1 } v || v[0] != '@' || v.IndexOf('/') is var slash && slash < 0) { return null; }
            var host = v[1..slash];
            var id = v[(slash + 1)..];
            return sources.FirstOrDefault(s => s.HostId == host) is { } s && s.Resumable.FirstOrDefault(r => r.Id == id) is { } r ? (s, r) : null;
        }
    }

    // Whether the machine shown takes sessions from the others: its own host's word, through the
    // relay for this machine (whose pipe never offers it).
    bool TakesTransfers(Machine m) => m.IsLocal ? watchedLoopback?.Host.Can(TransferProtocol.Capability) == true : m.Host.Can(TransferProtocol.Capability);

    string? DeviceOf(Machine m) => m.IsLocal ? main.LocalMachineId : m.Key;

    // Keeps what was picked if it is still offered, else the machine's preference, else the first.
    static Choice? Keep(IReadOnlyList<Choice> list, Choice? current, string? preferred) =>
        list.FirstOrDefault(c => c.Value == current?.Value) ?? list.FirstOrDefault(c => c.Value == preferred) ?? list.FirstOrDefault();

    // The choices for a model, effort or context: the agent's own default first, then what the
    // machine offers, with its configured default among them.
    static List<string> Tuning(IReadOnlyList<string>? values, string? preferred)
    {
        var list = (values ?? []).ToList();
        if (!string.IsNullOrEmpty(preferred) && !list.Contains(preferred)) { list.Insert(0, preferred); }
        return list;
    }

    void Draw()
    {
        var o = options;
        filling = true;
        try
        {
            if (o is null)
            {
                Resumes = Agents = Workspaces = Profiles = Models = Efforts = Contexts = [];
                ShowResume = ShowWorkspace = ShowProfile = ShowModel = ShowEffort = ShowContext = false;
                Note = machine is null ? "" : error is not null ? $"Couldn't get the choices from {Name}: {Sentence(error)}" : $"Getting the choices from {Name}…";
                NoteWarns = error is not null;
            }
            else
            {
                var away = sources.SelectMany(s => s.Resumable.Select(r => new Choice($"@{s.HostId}/{r.Id}", $"{r.Title} · on {s.MachineName}"))).ToList();
                // The options can answer before the other machines do: a session picked from one
                // on an earlier visit stays picked until they have, rather than becoming "New
                // session" (and launching one).
                if (!sourcesKnown && resume is { Value: ['@', ..] } waiting && away.All(c => c.Value != waiting.Value)) { away.Add(waiting); }
                Resumes = [new Choice("", "New session"), .. o.Resumable.Select(r => new Choice(r.Id, $"{r.Title}{(r.Workspace is { Length: > 0 } w ? $" · {w}" : "")}")), .. away];
                resume = Keep(Resumes, resume, "");
                var resumed = Resumed;
                var brought = Brought;
                // A session runs in an agent of its own kind, here as where it was.
                var kind = resumed?.Agent ?? brought?.Session.Agent;
                Agents = [.. o.Agents.Where(a => kind is null || a.Kind == kind).Select(a => new Choice(a.Id, a.Label))];
                agent = Keep(Agents, agent, resumed?.Launcher ?? brought?.Session.Launcher ?? o.Defaults.Agent);
                var a = AgentOf(agent?.Value);
                Workspaces = [.. o.Workspaces.Select(w => new Choice(w.Label, w.Isolate == true ? $"{w.Label} (new worktree)" : w.Label))];
                workspace = Keep(Workspaces, workspace, o.Defaults.Workspace);
                // Profile, model, effort and context were picked for an agent: another agent starts
                // from its own defaults.
                var same = scope == (a?.Id ?? "");
                scope = a?.Id ?? "";
                Profiles = a?.Profiles is { } ps ? [new Choice(AgentDefault, "Default profile"), .. ps.Select(p => new Choice(p, p))] : [];
                profile = Keep(Profiles, same ? profile : null, a?.Defaults?.Profile ?? AgentDefault);
                (Models, model, ShowModel) = Axis(a?.Models, a?.Defaults?.Model, same ? model : null);
                (Efforts, effort, ShowEffort) = Axis(a?.Efforts, a?.Defaults?.Effort, same ? effort : null);
                (Contexts, context, ShowContext) = Axis(a?.Contexts, a?.Defaults?.Context, same ? context : null);
                if (!permissionsSet || permissions is null)
                {
                    permissions = PermissionChoices.First(p => p.Value == (o.Defaults.Permissions == LaunchProtocol.AllowAll ? LaunchProtocol.AllowAll : LaunchProtocol.Ask));
                    permissionsSet = true;
                }
                ShowResume = o.Resumable.Count > 0 || away.Count > 0;
                ShowWorkspace = resumed is null && o.Workspaces.Count > 0;
                ShowProfile = a?.Profiles is not null;
                PromptPlaceholder = a?.NeedsPrompt == true && resumed is null && brought is null ? $"{a.Label} starts on a first message: what should it do?" : "What should the session do first? (optional)";
                var noAgent = o.Agents.Count == 0;
                var noWorkspace = resumed is null && o.Workspaces.Count == 0;
                NoteWarns = noAgent || noWorkspace;
                Note = noAgent ? $"No agent is installed on {Name}: install Copilot, Claude or Codex there."
                    : noWorkspace ? $"No folder on {Name} is approved for launching yet. At the machine, run: {ApproveHint}"
                    : brought is { } b ? $"This copies the session's whole conversation, with its checkpoints and files, from {b.From.MachineName} to {Name}, where it opens as a new session in the workspace picked. It stays on {b.From.MachineName} as it was."
                    : resume is { Value: ['@', ..] } ? "Looking for that session on the account's other machines…"
                    : resumed is not null ? "A resumed session reopens in its own folder." : "";
            }
        }
        finally { filling = false; }
        foreach (var p in new[] { nameof(Resumes), nameof(Resume), nameof(Agents), nameof(Agent), nameof(Workspaces), nameof(Workspace), nameof(Profiles), nameof(Profile),
            nameof(Models), nameof(Model), nameof(Efforts), nameof(Effort), nameof(Contexts), nameof(Context), nameof(Permissions),
            nameof(ShowForm), nameof(ShowResume), nameof(ShowWorkspace), nameof(ShowProfile), nameof(ShowModel), nameof(ShowEffort), nameof(ShowContext),
            nameof(ShowReload), nameof(CanReload), nameof(Note), nameof(NoteWarns), nameof(Busy), nameof(PromptPlaceholder) })
        {
            Raise(p);
        }
        Show();
    }

    static (IReadOnlyList<Choice>, Choice?, bool) Axis(IReadOnlyList<string>? values, string? preferred, Choice? current)
    {
        var list = Tuning(values, preferred);
        IReadOnlyList<Choice> choices = [new Choice(AgentDefault, "Agent default"), .. list.Select(v => new Choice(v, v))];
        return (choices, Keep(choices, current, preferred ?? AgentDefault), list.Count > 0);
    }

    // session.launch's settings, as picked. Model, effort and context go whenever they are
    // offered: "" is the agent's own default, which the picker offers beside the machine's.
    public LaunchRequest Settings()
    {
        var resumed = Resumed;
        var brought = Brought;
        var a = AgentOf(agent?.Value);
        return new LaunchRequest
        {
            // The command is signed at the press (Launch), for the machine the session is on.
            Transfer = brought is { } b ? new LaunchTransfer { From = b.From.HostId, Session = b.Session.Id } : null,
            Request = "",
            Agent = agent?.Value,
            Permissions = permissions?.Value,
            Resume = resumed?.Id,
            Workspace = resumed is null ? workspace?.Value : null,
            Profile = a?.Profiles is not null ? profile?.Value : null,
            Model = ShowModel ? model?.Value : null,
            Effort = ShowEffort ? effort?.Value : null,
            Context = ShowContext ? context?.Value : null,
            Prompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt,
        };
    }

    public void Launch()
    {
        var m = machine;
        if (m is null || options is not { } o || Pending) { return; }
        var launch = Settings();
        var a = AgentOf(launch.Agent);
        var problem = (launch.Agent is null ? "no agent is installed on this machine" : null)
            // A session from another machine not found (yet) is never launched as a new one.
            ?? (resume is { Value: ['@', ..] } && launch.Transfer is null ? "the session picked on another machine is still being looked for; try again in a moment" : null)
            ?? (launch.Resume is null && launch.Workspace is null
                ? o.Workspaces.Count > 0 ? "pick a workspace" : $"no workspace is approved on this machine yet; approve one at the machine ({ApproveHint})"
                : null)
            ?? (launch.Transfer is not null && !TakesTransfers(m) ? "this machine does not take sessions from other machines now" : null);
        if (problem is not null)
        {
            SetProblem($"Not launched: {Sentence(problem)}");
            return;
        }
        TransferSource? from = null;
        var via = m;
        if (launch.Transfer is not null)
        {
            // Signed before the request is measured: its command is part of what the host measures.
            from = Brought!.Value.From;
            if (m.IsLocal && watchedLoopback is null) { SetProblem("Not launched: this machine takes sessions from others only through the relay, which it is not connected to now."); return; }
            via = m.IsLocal ? watchedLoopback! : m;
            if (Signed(launch, from, m) is not { } signed) { SetProblem($"Not launched: couldn't sign the request for {from.MachineName}."); return; }
            launch = signed;
        }
        // Measured with a request id of the length Deliveries gives it.
        var sized = launch with { Request = Guid.Empty.ToString() };
        problem = LaunchProtocol.Check(sized)
            ?? (a?.NeedsPrompt == true && launch.Resume is null && launch.Transfer is null && launch.Prompt is null ? $"{a.Label} starts its session only on a first message; give it a prompt" : null)
            // The host measures the request as sent, where quotes and backslashes count twice.
            ?? (JsonSerializer.SerializeToUtf8Bytes(sized, ProtocolJson.Options).Length > LaunchProtocol.MaxRequest
                ? $"the prompt is too long once sent (over {LaunchProtocol.MaxRequest / 1024} KiB); shorten it, and send the rest once the session has started" : null);
        if (problem is not null)
        {
            SetProblem($"Not launched: {Sentence(problem)}");
            return;
        }
        SetProblem("");
        lost = null;
        lastMachine = m;
        lastVia = via;
        lastSource = from;
        lastName = m.Name;
        last = null;
        // No request is ever replayed to a host that restarted meanwhile: it remembers requests in
        // memory only, so a second copy could start a second session.
        Submit(via, launch);
    }

    // Signed once per press, for five minutes: a resend of that launch carries the same command,
    // which the machine the session is on acts on once.
    LaunchRequest? Signed(LaunchRequest launch, TransferSource from, Machine target) =>
        DeviceOf(target) is { } to && main.Fleet?.SignTransfer(from, launch.Transfer!.Session, to) is { } command
            ? launch with { Transfer = launch.Transfer with { Command = command } } : null;

    void Submit(Machine m, LaunchRequest launch)
    {
        var transfer = launch.Transfer is not null;
        var sent = m.Deliveries.Submit(Key, LaunchProtocol.Launch, request => launch with { Request = request }, _ => false, payload: launch, replayAfterRestart: false,
            timeout: transfer ? Transfers.LaunchTimeout : LaunchTimeout, resendOnBusy: false, giveUp: transfer ? Transfers.GiveUp : null);
        // Submit returns its first snapshot; the delivery may already have moved on (no connection:
        // waiting), and that newer state is the one to show.
        last = m.Deliveries.Get(Key) is { } now && now.Request == sent.Request ? now : sent;
        Show();
    }

    public void Resend()
    {
        if (!ShowResend || lastMachine is not { } m || last is not { Payload: LaunchRequest launch } e || lastVia is not { } via) { return; }
        if (e.State == DeliveryState.Done)
        {
            // A new request: a transfer's command was spent on the last one, so it is signed again.
            if (launch.Transfer is not null)
            {
                if (lastSource is not { } from || Signed(launch, from, m) is not { } again) { SetProblem("Not launched: couldn't sign the request again; pick the session and press Launch."); return; }
                launch = again;
            }
            Submit(via, launch);
        }
        else { via.Deliveries.Resend(Key, LaunchProtocol.Launch, request => launch with { Request = request }); }
    }

    // The new session's card, which may arrive after the outcome.
    public void OpenSession()
    {
        if (lost is null && lastMachine is { } m && last is { } e) { Open(m, e); }
    }

    void Open(Machine m, Delivery e)
    {
        if (Result(e)?.Session is { } id) { main.Open(m.Key, id, untilItArrives: true); }
    }

    // via: the Deliveries it came from, when not m's own (this machine's loopback).
    void OnDelivery(Machine m, Delivery d, Machine? via = null)
    {
        if (d.Key != Key) { return; }
        if (m == lastMachine && (via ?? m) == lastVia && (last is null || d.Request == last.Request)) { last = d; Show(); }
        if (d.State != DeliveryState.Done || handled == d.Request) { return; }
        handled = d.Request;
        var outcome = OutcomeOf(d);
        if (outcome == "started")
        {
            // Only the prompt that went: one typed since for the next launch stays.
            if (d.Payload is LaunchRequest sent && (string.IsNullOrWhiteSpace(prompt) ? null : prompt) == sent.Prompt) { Prompt = ""; }
            Open(m, d);
        }
        // What it offers has changed: a resumed session is no longer resumable, a worktree counts.
        if (m == machine && shown && outcome is "started" or "unconfirmed") { _ = RefreshAsync(); }
    }

    // An outcome this app does not know may still mean a session started, so it reads as
    // unconfirmed: check before launching again.
    static string OutcomeOf(Delivery e) => e.Outcome is "started" or "busy" or "refused" or "failed" or "unconfirmed" ? e.Outcome : "unconfirmed";

    static LaunchResult? Result(Delivery e)
    {
        if (e.Result.ValueKind != JsonValueKind.Object) { return null; }
        try { return e.Result.Deserialize<LaunchResult>(ProtocolJson.Options); }
        catch (JsonException) { return null; }
    }

    void Show()
    {
        var (text, tone) = lost is not null ? (lost, Tone.Warn) : last is { } e ? Describe(e, lastName) : ("", Tone.None);
        // Only a change is news: the status line is announced to screen readers whenever it is
        // raised, and this runs on every update of any machine.
        if (text != StatusText || tone != StatusTone)
        {
            (StatusText, StatusTone) = (text, tone);
            Raise(nameof(StatusText));
            Raise(nameof(StatusTone));
        }
        foreach (var p in new[] { nameof(CanLaunch), nameof(ShowResend), nameof(ShowOpen), nameof(State) }) { Raise(p); }
    }

    // What a launch's progress or outcome says (describeLaunch in the PWA).
    public static (string Text, Tone Tone) Describe(Delivery e, string? machineName)
    {
        var on = string.IsNullOrEmpty(machineName) ? "" : $" on {machineName}";
        switch (e.State)
        {
            case DeliveryState.Sending when e.Payload is LaunchRequest { Transfer: not null }:
                return ($"Bringing the session over and starting it{on}… A long session can take a few minutes.", Tone.Progress);
            case DeliveryState.Sending:
                return ($"Starting the session{on}… This waits for it to start, up to a minute.", Tone.Progress);
            case DeliveryState.Waiting:
                return ($"Waiting for the connection to the machine{on}. The launch goes as soon as it is back.", Tone.Progress);
            case DeliveryState.Failed:
                // An error from the host does not say the launch never ran: the error can come after.
                return ($"{NameOr(machineName)} returned an error: {Sentence(e.Reason ?? "no reason given")} The launch may have started: check at the machine before launching again.{Again(e, machineName)}", Tone.Warn);
        }
        var how = Result(e)?.Launched?.Worktree == true ? " in a new worktree" : "";
        return OutcomeOf(e) switch
        {
            "started" => (Result(e)?.Launched?.Transfer is not null ? $"Session brought over and started{how}. The original is still on its machine." : $"Session started{how}.", Tone.Ok),
            // Also said here when the launch may never have gone (no reply, a host that restarted).
            "unconfirmed" => ($"Not confirmed whether the session started{(e.Reason is { } r ? $": {Sentence(r)}" : ".")} Check at the machine before launching again.", Tone.Warn),
            "busy" => ($"Not launched: {Sentence(e.Reason ?? "the machine is busy with another launch")} Try again in a moment.", Tone.Warn),
            "refused" => ($"Not launched: {Sentence(e.Reason ?? "the machine refused it")}", Tone.Bad),
            _ => ($"Couldn't launch{(e.Reason is { } f ? $": {Sentence(f)}" : ". Check at the machine.")}{Again(e, machineName)}", Tone.Bad),
        };
    }

    static string NameOr(string? name) => string.IsNullOrEmpty(name) ? "The machine" : name;

    // What Try again sends: the launch as it was pressed, which the form may no longer show.
    static string Again(Delivery e, string? machineName)
    {
        if (e.Payload is not LaunchRequest l) { return ""; }
        var where = l.Resume is not null ? "resuming its session" : l.Transfer is not null ? $"bringing its session over into {l.Workspace}" : l.Workspace is { } w ? $"in {w}" : "";
        var how = l.Permissions == LaunchProtocol.AllowAll ? "allow all" : "ask permission";
        return $" Try again sends that same launch to {NameOr(machineName)}: {l.Agent}{(where.Length > 0 ? $" {where}" : "")}, {how}.";
    }

    static string Sentence(string s) => s.Length > 0 && !".!?".Contains(s[^1]) ? $"{s}." : s;
}
