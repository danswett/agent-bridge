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
    string prompt = "";
    (string Text, Tone Tone)? local;

    public LaunchViewModel(MainViewModel main, IUiDispatcher ui)
    {
        this.main = main;
        this.ui = ui;
        main.MachinesUpdated += MachinesUpdated;
        MachinesUpdated();
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
    public Tone StatusTone { get; private set; }
    public bool CanLaunch => options is not null && !Pending;
    public bool ShowResend => Entry?.State == DeliveryState.Failed;
    public bool ShowOpen => Entry is { State: DeliveryState.Done } e && Result(e)?.Session is not null;
    // What the last launch came to, for tests and the self-test: none, sending, waiting, failed or its outcome.
    public string State => Entry is not { } e ? "none" : e.State != DeliveryState.Done ? e.State.ToString().ToLowerInvariant() : OutcomeOf(e);

    Delivery? Entry => machine?.Deliveries.Get(Key);
    bool Pending => Entry?.State is DeliveryState.Sending or DeliveryState.Waiting;

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
        foreach (var m in main.AllMachines.Where(m => !watching.ContainsKey(m)).ToList())
        {
            void Watch(Delivery d) => OnDelivery(m, d);
            watching[m] = Watch;
            m.Deliveries.Changed += Watch;
        }
        foreach (var (m, watch) in watching.Where(p => !main.AllMachines.Contains(p.Key)).ToList())
        {
            m.Deliveries.Changed -= watch;
            watching.Remove(m);
        }
        var list = main.AllMachines.Where(m => m.Host.Can(LaunchProtocol.Capability))
            .OrderBy(m => m.IsLocal ? 0 : 1)
            .Select(m => new Choice(m.Key, m.IsLocal ? $"{m.Name} (this machine)" : m.Name)).ToList();
        if (list.SequenceEqual(Machines)) { return; }
        Machines = list;
        filling = true;
        try { Raise(nameof(Machines)); }
        finally { filling = false; }
        Raise(nameof(Available));
        Raise(nameof(ShowMachines));
        var keep = list.FirstOrDefault(c => c.Value == selectedMachine?.Value) ?? list.FirstOrDefault();
        if (keep?.Value != selectedMachine?.Value) { selectedMachine = keep; Target(keep is null ? null : main.MachineOf(keep.Value)); }
        else { selectedMachine = keep; }
        Raise(nameof(SelectedMachine));
        Raise(nameof(Title));
    }

    void Target(Machine? next)
    {
        machine = next;
        options = null;
        error = null;
        local = null;
        scope = "";
        permissionsSet = false;
        loads++;
        loading = false;
        // Another machine's choices: nothing picked on the last one carries over but the prompt.
        resume = agent = workspace = profile = model = effort = context = permissions = null;
        Raise(nameof(Title));
        Draw();
        if (machine is not null) { _ = RefreshAsync(); }
    }

    // The page was opened: a workspace approved, an agent installed or a session ended since.
    public void Opened()
    {
        if (machine is not null && !loading) { _ = RefreshAsync(); }
    }

    // launch.options again.
    public async Task RefreshAsync()
    {
        var m = machine;
        if (m is null) { return; }
        var load = ++loads;
        loading = true;
        error = null;
        Draw();
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
                if (got is not null) { options = got; } else { error = failed; }
                Draw();
            }
            done.SetResult();
        });
        await done.Task;
    }

    // An agent or launcher this app does not know cannot be launched from it, so it is left out.
    static LaunchOptions Known(LaunchOptions o) => o with
    {
        Agents = [.. o.Agents.Where(a => LaunchProtocol.Agents.Contains(a.Id))],
        Resumable = [.. o.Resumable.Where(r => LaunchProtocol.Agents.Contains(r.Launcher))],
    };

    LaunchAgent? AgentOf(string? id) => options?.Agents.FirstOrDefault(a => a.Id == id);
    ResumableSession? Resumed => options?.Resumable.FirstOrDefault(r => r.Id == resume?.Value && r.Id.Length > 0);

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
                Resumes = [new Choice("", "New session"), .. o.Resumable.Select(r => new Choice(r.Id, $"{r.Title}{(r.Workspace is { Length: > 0 } w ? $" · {w}" : "")}"))];
                resume = Keep(Resumes, resume, "");
                var resumed = Resumed;
                Agents = [.. o.Agents.Where(a => resumed is null || a.Kind == resumed.Agent).Select(a => new Choice(a.Id, a.Label))];
                agent = Keep(Agents, agent, resumed?.Launcher ?? o.Defaults.Agent);
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
                ShowResume = o.Resumable.Count > 0;
                ShowWorkspace = resumed is null && o.Workspaces.Count > 0;
                ShowProfile = a?.Profiles is not null;
                PromptPlaceholder = a?.NeedsPrompt == true && resumed is null ? $"{a.Label} starts on a first message: what should it do?" : "What should the session do first? (optional)";
                var noAgent = o.Agents.Count == 0;
                var noWorkspace = resumed is null && o.Workspaces.Count == 0;
                NoteWarns = noAgent || noWorkspace;
                Note = noAgent ? $"No agent is installed on {Name}: install Copilot, Claude or Codex there."
                    : noWorkspace ? $"No folder on {Name} is approved for launching yet. At the machine, run: {ApproveHint}"
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
        var a = AgentOf(agent?.Value);
        return new LaunchRequest
        {
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
            ?? (launch.Resume is null && launch.Workspace is null
                ? o.Workspaces.Count > 0 ? "pick a workspace" : $"no workspace is approved on this machine yet; approve one at the machine ({ApproveHint})"
                : null)
            ?? LaunchProtocol.Check(launch with { Request = Guid.Empty.ToString() })
            ?? (a?.NeedsPrompt == true && launch.Resume is null && launch.Prompt is null ? $"{a.Label} starts its session only on a first message; give it a prompt" : null)
            // The host measures the request as sent, where quotes and backslashes count twice.
            ?? (JsonSerializer.SerializeToUtf8Bytes(launch with { Request = Guid.Empty.ToString() }, ProtocolJson.Options).Length > LaunchProtocol.MaxRequest
                ? $"the prompt is too long once sent (over {LaunchProtocol.MaxRequest / 1024} KiB); shorten it, and send the rest once the session has started" : null);
        if (problem is not null)
        {
            local = ($"Not launched: {Sentence(problem)}", Tone.Bad);
            Show();
            return;
        }
        local = null;
        // No request is ever replayed to a host that restarted meanwhile: it remembers requests in
        // memory only, so a second copy could start a second session.
        m.Deliveries.Submit(Key, LaunchProtocol.Launch, request => launch with { Request = request }, _ => false, payload: launch, replayAfterRestart: false, timeout: LaunchTimeout, resendOnBusy: false);
        Show();
    }

    public void Resend()
    {
        if (machine is null || Entry is not { State: DeliveryState.Failed, Payload: LaunchRequest launch }) { return; }
        machine.Deliveries.Resend(Key, LaunchProtocol.Launch, request => launch with { Request = request });
    }

    // The new session's card, which may arrive after the outcome.
    public void OpenSession()
    {
        if (machine is not null && Entry is { } e) { Open(machine, e); }
    }

    void Open(Machine m, Delivery e)
    {
        if (Result(e)?.Session is { } id) { main.Open(m.Key, id); }
    }

    void OnDelivery(Machine m, Delivery d)
    {
        if (d.Key != Key) { return; }
        if (m == machine) { Show(); }
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
        if (m == machine && outcome is "started" or "unconfirmed") { _ = RefreshAsync(); }
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
        (StatusText, StatusTone) = Entry is { } e && local is null ? Describe(e, machine?.Name) : local ?? ("", Tone.None);
        foreach (var p in new[] { nameof(StatusText), nameof(StatusTone), nameof(CanLaunch), nameof(ShowResend), nameof(ShowOpen), nameof(State) }) { Raise(p); }
    }

    // What a launch's progress or outcome says (describeLaunch in the PWA).
    public static (string Text, Tone Tone) Describe(Delivery e, string? machineName)
    {
        var on = string.IsNullOrEmpty(machineName) ? "" : $" on {machineName}";
        switch (e.State)
        {
            case DeliveryState.Sending:
                return ($"Starting the session{on}… This waits for it to start, up to a minute.", Tone.Progress);
            case DeliveryState.Waiting:
                return ($"Waiting for the connection to the machine{on}. The launch goes as soon as it is back.", Tone.Progress);
            case DeliveryState.Failed:
                return ($"Couldn't send it: {e.Reason ?? "the machine said no"}.", Tone.Bad);
        }
        var how = Result(e)?.Launched?.Worktree == true ? " in a new worktree" : "";
        return OutcomeOf(e) switch
        {
            "started" => ($"Session started{how}.", Tone.Ok),
            "unconfirmed" => ($"Launched{how}, but not confirmed{(e.Reason is { } r ? $": {Sentence(r)}" : ". Check at the machine.")}", Tone.Warn),
            "busy" => ($"Not launched: {Sentence(e.Reason ?? "the machine is busy with another launch")} Try again in a moment.", Tone.Warn),
            "refused" => ($"Not launched: {Sentence(e.Reason ?? "the machine refused it")}", Tone.Bad),
            _ => ($"Couldn't launch{(e.Reason is { } f ? $": {Sentence(f)}" : ". Check at the machine.")}", Tone.Bad),
        };
    }

    static string Sentence(string s) => s.Length > 0 && !".!?".Contains(s[^1]) ? $"{s}." : s;
}
