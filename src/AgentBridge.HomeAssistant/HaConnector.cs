using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;

namespace AgentBridge.HomeAssistant;

public sealed record HaConnectorOptions
{
    // The computer's name, as agent-ha-bridge keys everything per machine on it.
    public required string MachineName { get; init; }
    // The host's version, for the machine row's version.
    public required string Version { get; init; }
    public bool Dev { get; init; }
    // What a card carries when the machine has no Detailed activity switch.
    public bool DetailedActivityFallback { get; init; } = true;
    public string DashboardUrlPath { get; init; } = HaSettings.DefaultDashboard;
    // Where sessions waiting to be retired are remembered across a restart. None in tests that
    // do not need it.
    public string? StateFile { get; init; }
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(1);
    // How often the Detailed activity switch and the dashboard are read: the daemon's reconcile.
    public TimeSpan Reconcile { get; init; } = TimeSpan.FromSeconds(15);
    // Discovery needs a moment to register before an entity id can be forced (the daemon waits
    // 1.5 s after publishing a session before Set-CopilotMqttEntityIds).
    public TimeSpan PinDelay { get; init; } = TimeSpan.FromSeconds(1.5);
    public int PinAttempts { get; init; } = 10;
    // A browser keeps rendering the dashboard config it has until Home Assistant pushes the new
    // one, so an ended session's entities go one pass after its card has left the served config
    // (Update-DaemonRetireQueue), never with it.
    public TimeSpan RetireGrace { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    // Home Assistant accounts that are agents rather than people (homeAssistant.agentUserIds):
    // a press made through one drives the session's next turn as the agent's (the purple edge).
    public IReadOnlyCollection<string> AgentUserIds { get; init; } = [];
    // The numbered usage sensors of the further Copilot logins this host is configured to report
    // (usage.copilotAccounts: copilot_2 for the first listed). The rest are withdrawn when the
    // connector starts, as the daemon withdraws every numbered sensor not in use on its first pass,
    // so a login taken off the list does not leave its last reading with nothing to refresh it.
    // From the configuration rather than the readings, so one not yet read is not withdrawn and
    // registered again under a new id moments later.
    public IReadOnlyList<string> UsageAccountSensors { get; init; } = [];
    // Field slots are republished for each question and need a moment to register before their
    // ids are forced (Publish-CopilotMqttDecisionFields waits 900 ms).
    public TimeSpan FieldPinDelay { get; init; } = TimeSpan.FromMilliseconds(900);
    // How long a select is waited on to offer an option before it is set anyway
    // (Set-CopilotMqttSelectOption: 12 attempts, 150 ms apart).
    public int SelectAttempts { get; init; } = 12;
    public TimeSpan SelectDelay { get; init; } = TimeSpan.FromMilliseconds(150);
    // An arm left by a press stops counting for the next turn after this long
    // (DriverArmSeconds): the turn it was for is not coming.
    public TimeSpan DriverArm { get; init; } = TimeSpan.FromSeconds(600);
    // How often a card with an open question is read even though the watch reported nothing, so
    // a press made while the watch was reconnecting is not lost; the watch makes it immediate.
    public TimeSpan AnswerSweep { get; init; } = TimeSpan.FromSeconds(5);
    // Whether presses on the cards are read and answered. Off for the shadow's dry pass, which
    // publishes the controls to compare them but must act on nothing.
    public bool ConsumeAnswers { get; init; } = true;
    // How long a press of Send waits for the reply box's words to reach Home Assistant before the
    // card is told it is waiting for them (ReplyCommitAttempts, ReplyCommitWaitMs): the tap on Send
    // is what commits the box, so the press usually lands first.
    public int ReplyCommitAttempts { get; init; } = 6;
    public TimeSpan ReplyCommitWait { get; init; } = TimeSpan.FromMilliseconds(500);
    // How long a press of Send at an empty box stays armed for the words to arrive (SubmitArmSeconds).
    public TimeSpan SubmitArm { get; init; } = TimeSpan.FromSeconds(600);
    // How often every card is read for a reply even though the watch reported nothing.
    public TimeSpan ReplySweep { get; init; } = TimeSpan.FromSeconds(15);
    // The largest image fetched back from Home Assistant's image store. Its upload takes up to
    // 10 MiB, so anything larger did not come from the card.
    public long ImageMaxBytes { get; init; } = 25L * 1024 * 1024;
    // The largest file the card sends inline (Save-BridgeReplyFile's MaxBytes).
    public int InlineFileMaxBytes { get; init; } = 262144;
    // How often a lapsed End session note is tried again when it could not be shown
    // (StopLapseRetrySeconds).
    public TimeSpan StopLapseRetry { get; init; } = TimeSpan.FromSeconds(2);
    // How long stopping waits for a reply being delivered.
    public TimeSpan ReplyStopWait { get; init; } = TimeSpan.FromSeconds(10);
    // How often the launch card's choices are asked of the host's launcher again, and its
    // selectors kept on a usable default (the daemon's reconcile).
    public TimeSpan LaunchRefresh { get; init; } = TimeSpan.FromSeconds(30);
}

// The Home Assistant connector (docs/ha-connector.md): this machine's sessions, liveness,
// version and agent usage on the dashboard agent-ha-bridge's publication writer generates, in
// agent-ha-bridge's own per-machine contract (HaContract), so a machine moved off the
// PowerShell daemon keeps its cards; and, given the host's answer, reply and stop delivery, the
// answers, replies and End session presses given on those cards (HaConnector.Answers.cs,
// HaConnector.Replies.cs, HaConnector.Stops.cs), and, given its launcher, the launch card
// (HaConnector.Launch.cs).
//
// What it never does, and why:
//   * write the shared card, dashboard or publication policy. The writer is configured and fenced
//     in agent-ha-bridge (its AGENTS.md, "Publication authority"); this host is never it, and
//     only reads the served dashboard to know when a card has gone;
//   * call a service that names more than one entity (ServiceTarget);
//   * run while agent-ha-bridge runs for this user (design G5). Two publishers of one machine's
//     entities would fight over every retained topic. Checked before every write, so the
//     daemon starting part-way through a pass stops the rest of it;
//   * update, or bring a session over from another machine: later stages. Until they land,
//     entities only those would serve are withdrawn rather than left as controls nothing acts on.
public sealed partial class HaConnector : IAsyncDisposable
{
    readonly HaConnectorOptions options;
    readonly IHomeAssistant ha;
    readonly ISessionSource source;
    readonly Func<(UsageReport Report, long Version)>? usage;
    readonly Func<string?> agentHaBridge;
    readonly Action<string> log;
    readonly TimeProvider time;
    readonly object gate = new();
    readonly HashSet<string> dirty = new(StringComparer.Ordinal);
    readonly Dictionary<string, Published> published = new(StringComparer.Ordinal);
    // Sessions whose first publication has begun but not finished: they already have retained
    // entities in Home Assistant, so one ending before a retry succeeds must still be retired.
    readonly Dictionary<string, string> starting = new(StringComparer.Ordinal);
    readonly Dictionary<string, Retiring> retiring = new(StringComparer.Ordinal);
    readonly Dictionary<string, Pin> pins = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> usageSignatures = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> problems = new(StringComparer.Ordinal);
    readonly SemaphoreSlim passes = new(1, 1);
    bool started;
    bool allDirty;
    string? pausedFor;
    DateTimeOffset retryAt = DateTimeOffset.MinValue;
    DateTimeOffset nextBeat;
    DateTimeOffset nextReconcile;
    DateTimeOffset globalAt;
    string? globalSignature;
    long usageVersion = -1;
    bool verbose;
    bool helperExists;
    CancellationTokenSource? stop;
    Task loop = Task.CompletedTask;
    Task watch = Task.CompletedTask;
    readonly Func<AnswerRequest, CancellationToken, Task<AnswerResult>>? answer;

    // Whether the cards' answer controls are served: only with the host's answer delivery.
    bool ServesAnswers => answer is not null;

    sealed class Published
    {
        public required string Node;
        public required string Name;
        public required string Kind;
        public string? Status;
        public string? Activity;
        // The activity attributes last published, which a transient status line is laid over.
        public JsonObject? Detail;
        // The question the card is armed with, and how many field slots that left armed (-1:
        // not known, so a clear sweeps them all).
        public Armed? Armed;
        public int ArmedSlots = -1;
        public bool Cleared;
        // The reply-card publish last used (Add-DaemonAnswerStamp's answer_consumed_at).
        public string? LastPayloadStamp;
        public string? ToldUnserved;
        // Replies (HaConnector.Replies.cs).
        public ReplyState Reply = new();
        // End session (HaConnector.Stops.cs).
        public StopState Stop = new();
        // Who is driving the session's turn, and an arm left by a press for the next one
        // (Set-DaemonDriverPending): consumed by the turn it was armed for, or lapsed.
        public string Driver = "human";
        public DateTimeOffset? DriverPendingAt;
        public string? TurnKey;

        public void ArmDriver(string driver, DateTimeOffset now)
        {
            Driver = driver;
            DriverPendingAt = now;
        }

        // An agent's launch: its opening turn, the prompt it launched with, is the agent's, and
        // the arm is for that turn alone. The opening turn may be here already (the session
        // registered before its launch was reported), so it is looked for as well as waited for;
        // the turn after it is whoever's it is. Without a prompt there is no opening turn, and
        // the arm waits for the first, as a press's does.
        string? openingPrompt;
        bool openingResumed;

        public void ArmLaunch(DateTimeOffset at, string? prompt, bool resumed)
        {
            ArmDriver("agent", at);
            openingPrompt = prompt is { Length: > 0 } ? Collapse(prompt) : null;
            openingResumed = resumed;
        }

        // The current turn, seen: when it is the launch's opening turn, the arm is spent on it.
        public void TurnSeen(Activity activity)
        {
            if (openingPrompt is not { } opening) { return; }
            var message = activity.UserMessage is { } m ? Collapse(m) : "";
            var head = opening[..Math.Min(80, opening.Length)];
            if (message.StartsWith(head, StringComparison.Ordinal) || (!openingResumed && activity.TurnCount >= 1))
            {
                DriverPendingAt = null;
                openingPrompt = null;
            }
        }

        static string Collapse(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();

        // A new turn: the press that armed it drives it, or nobody did and the person at the
        // machine is.
        public void TurnStarted(DateTimeOffset now, TimeSpan arm)
        {
            if (DriverPendingAt is not { } at || now - at >= arm) { Driver = "human"; }
            DriverPendingAt = null;
            openingPrompt = null;
        }
        // Which of the reply and the reasoning changed last. The host keeps the previous turn's
        // reply when a new turn starts, so "no reply yet" cannot say the newest line is a thought.
        public bool Seen;
        public string? SeenResponse;
        public string? SeenReasoning;
        public bool Thinking;

        public bool Observe(string? response, string? reasoning)
        {
            var hasReply = !string.IsNullOrWhiteSpace(response);
            var hasReasoning = !string.IsNullOrWhiteSpace(reasoning);
            if (!Seen) { Thinking = !hasReply && hasReasoning; }
            // A reply follows the thinking that led to it, so a pass seeing both change takes the reply.
            else if (hasReply && response != SeenResponse) { Thinking = false; }
            else if (hasReasoning && reasoning != SeenReasoning) { Thinking = true; }
            else if (!hasReasoning) { Thinking = false; }
            Seen = true;
            SeenResponse = response;
            SeenReasoning = reasoning;
            return Thinking;
        }
    }

    sealed class Retiring
    {
        public DateTimeOffset? AbsentSince;
    }

    sealed record Pin(string EntityId, DateTimeOffset NotBefore, int Attempts);

    public HaConnector(HaConnectorOptions options, IHomeAssistant ha, ISessionSource source, Func<string?> agentHaBridge,
        Func<(UsageReport Report, long Version)>? usage = null, Action<string>? log = null, TimeProvider? time = null,
        Func<AnswerRequest, CancellationToken, Task<AnswerResult>>? answer = null,
        Func<HaReply, CancellationToken, Task<ReplyResult>>? reply = null,
        Func<StopRequest, CancellationToken, Task<StopResult>>? stop = null,
        HaLauncher? launch = null)
    {
        this.options = options;
        launcher = launch;
        this.answer = answer;
        this.reply = reply;
        stopper = stop;
        // Every write asks first (design G5): a pass can run long (each request may take its
        // timeout), and agent-ha-bridge starting part-way through must stop the rest of it, not
        // race the daemon's own publishes until the pass ends.
        this.ha = new Guarded(ha, agentHaBridge);
        this.source = source;
        this.usage = usage;
        this.agentHaBridge = agentHaBridge;
        this.log = log ?? (_ => { });
        this.time = time ?? TimeProvider.System;
        Slug = HaContract.MachineSlug(options.MachineName);
        verbose = options.DetailedActivityFallback;
        source.Changed += OnChanged;
    }

    public string Slug { get; }

    // Whether the startup pass has run since the connector last started or was paused.
    public bool Started => started;

    // Nodes whose entities are waiting for their card to leave the dashboard.
    public IReadOnlyCollection<string> PendingRetirement { get { lock (gate) { return [.. retiring.Keys]; } } }

    public void Start()
    {
        stop = new CancellationTokenSource();
        var token = stop.Token;
        loop = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await TickAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception e) { Problem("tick", $"Home Assistant connector pass failed: {e.GetType().Name}: {e.Message}"); }
                try { await Task.Delay(options.Tick, time, token); }
                catch (OperationCanceledException) { break; }
            }
        }, token);
        if (ServesAnswers) { watch = Task.Run(() => WatchLoopAsync(token), token); }
    }

    void OnChanged(object? sender, SessionChange change)
    {
        lock (gate) { dirty.Add(change.Id); }
    }

    // One pass: whatever is due. The loop calls it every Tick; tests call it directly.
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        await passes.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            if (agentHaBridge() is { } active)
            {
                Pause(active);
                return;
            }
            if (pausedFor is not null) { log("Home Assistant connector resuming: agent-ha-bridge is no longer running for this user"); }
            pausedFor = null;
            if (!started)
            {
                if (now < retryAt) { return; }
                try
                {
                    await StartupAsync(now, cancellationToken);
                    Resolved("startup");
                }
                catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning))
                {
                    retryAt = now + options.RetryDelay;
                    Problem("startup", $"Home Assistant connector could not start: {Describe(e)}; trying again in {options.RetryDelay.TotalSeconds:F0}s");
                    return;
                }
            }
            await Step("sessions", () => SessionsAsync(now, cancellationToken));
            await Step("answers", () => AnswersAsync(now, cancellationToken));
            await Step("launch", () => LaunchAsync(now, cancellationToken));
            await Step("global", () => GlobalAsync(now, cancellationToken));
            await Step("heartbeat", () => HeartbeatAsync(now, cancellationToken));
            await Step("usage", () => UsageAsync(now, cancellationToken));
            await Step("pins", () => PinsAsync(now, cancellationToken));
            if (now >= nextReconcile)
            {
                nextReconcile = now + options.Reconcile;
                await Step("detailed", () => DetailedAsync(cancellationToken));
                await Step("retire", () => RetireAsync(now, cancellationToken));
            }
        }
        // agent-ha-bridge started during the pass: the write that found it was not made, and
        // nothing after it is.
        catch (AgentHaBridgeRunning e) { Pause(e.Message); }
        finally { passes.Release(); }
    }

    void Pause(string why)
    {
        if (pausedFor != why) { log($"Home Assistant connector paused: {why}"); }
        pausedFor = why;
        started = false;
    }

    async Task Step(string name, Func<Task> step)
    {
        try
        {
            await step();
            Resolved(name);
        }
        catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning))
        {
            Problem(name, $"Home Assistant connector {name} failed: {Describe(e)}; trying again");
        }
    }

    // Once per start, and again after a pause: declare liveness, read what is already there, and
    // take over this machine's entities from whatever published them last (agent-ha-bridge, or
    // this host before a restart).
    async Task StartupAsync(DateTimeOffset now, CancellationToken ct)
    {
        // The liveness sensor before its first beat: an unretained beat that reaches Home
        // Assistant before the config is processed is dropped, and the machine would read
        // offline for a whole interval from the moment it started.
        var (onlineTopic, onlineConfig) = HaContract.OnlineConfig(Slug, options.MachineName);
        await PublishAsync(onlineTopic, onlineConfig, true, ct);
        AddPin($"agent_bridge_{Slug}_online", HaContract.MachineEntityId("binary_sensor", "online", Slug), now);
        nextBeat = now + TimeSpan.FromSeconds(2);

        await DetailedAsync(ct);

        // agent-ha-bridge's transfer and install controls for this machine, and its launch card
        // when the host cannot launch here: withdrawn rather than left for a press that nothing
        // would act on; agent-ha-bridge publishes them again if it takes the machine back.
        launchServing = launcher?.Available() == true;
        launchOptions = null;
        launchSignature = null;
        launchDefaultsPending = null;
        launchPressBaselined = false;
        launchOutcomesRestored = false;
        foreach (var topic in Unserved()) { await ha.PublishAsync(topic, "", true, ct); }

        var (updateTopic, updateConfig) = HaContract.UpdateConfig(Slug, options.MachineName);
        await PublishAsync(updateTopic, updateConfig, true, ct);
        await PublishAsync(HaContract.UpdateStateTopic(Slug), HaPayloads.UpdateState(options.Version), true, ct);
        await PublishAsync(HaContract.UpdateAttributesTopic(Slug), HaPayloads.UpdateAttributes(), true, ct);
        AddPin($"agent_bridge_{Slug}_update", HaContract.MachineEntityId("update", "update", Slug), now);

        // Sessions this machine published before, by whichever bridge, that are not running now:
        // the ones its retained session list names, and the remembered ones whose retirement was
        // still waiting when this host stopped.
        var live = source.Snapshot().Select(s => HaContract.NodeId(s.Id)).ToHashSet(StringComparer.Ordinal);
        var inherited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in LoadLedger()) { inherited.Add(node); }
        try
        {
            if (await ha.GetStateAsync(HaContract.MachineEntityId("sensor", "sessions", Slug), ct) is { } own)
            {
                foreach (var node in ListedNodes(own.Attributes)) { inherited.Add(node); }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log($"Home Assistant connector could not read this machine's retained session list ({Describe(e)}); sessions it named are left until the next start");
        }
        foreach (var node in await OrphansAsync(live, ct)) { inherited.Add(node); }
        lock (gate)
        {
            // A session whose first publication a pause interrupted is retired like any other
            // that has gone; a live one is simply published again.
            foreach (var node in starting.Values) { inherited.Add(node); }
            starting.Clear();
            foreach (var node in inherited.Where(n => !live.Contains(n))) { retiring.TryAdd(node, new Retiring()); }
            published.Clear();
            usageSignatures.Clear();
            allDirty = true;
        }
        SaveLedger();
        globalSignature = null;
        usageVersion = -1;
        nextReconcile = now + options.Reconcile;
        started = true;
        log($"Home Assistant connector started for {options.MachineName} (agent_bridge_{Slug}): {live.Count} live session(s), {retiring.Count} to retire once their cards have gone");
    }

    // What agent-ha-bridge publishes for this machine that this stage does not serve.
    IEnumerable<string> Unserved()
    {
        var served = new HashSet<string>(StringComparer.Ordinal)
        {
            HaContract.UpdateConfig(Slug, options.MachineName).Topic,
            HaContract.SessionsConfig(Slug, options.MachineName).Topic,
            HaContract.OnlineConfig(Slug, options.MachineName).Topic,
            HaContract.UpdateStateTopic(Slug), HaContract.UpdateAttributesTopic(Slug), HaContract.OnlineStateTopic(Slug),
            HaContract.OnlineAttributesTopic(Slug), HaContract.GlobalStateTopic(Slug), HaContract.GlobalAttributesTopic(Slug),
        };
        // The launch card, when it is served (the transfer request is a later stage). Its note,
        // outcomes and prompt payload are left as they are: the payload is the card's.
        if (launchServing)
        {
            served.UnionWith(HaContract.LaunchConfigs(Slug, options.MachineName, new HaContract.LaunchCard()).Select(c => c.Topic));
            served.UnionWith([HaContract.LaunchResultTopic(Slug), HaContract.LaunchOutcomeTopic(Slug), HaContract.PromptPayloadTopic(Slug)]);
        }
        // The agents' own usage sensors are the usage pass's, and so are the numbered ones of the
        // further Copilot logins it reports; the other numbered ones go rather than keep a reading
        // nothing refreshes.
        served.UnionWith(HaContract.UsageTopics(Slug, [.. HaContract.UsageClients, .. usage is null ? [] : options.UsageAccountSensors]));
        return HaContract.MachineTopics(Slug).Where(t => !served.Contains(t));
    }

    // A session's entities this stage does not serve: with answers, only Stop (and two state
    // topics no daemon has published for years); without, everything but the two sensors.
    IEnumerable<string> UnservedSessionTopics(string node)
    {
        var t = HaContract.Topics(node);
        var served = new HashSet<string>(StringComparer.Ordinal)
        {
            HaContract.DiscoveryTopic("sensor", node, "status"), HaContract.DiscoveryTopic("sensor", node, "activity"),
            t.StatusState, t.StatusAttributes, t.ActivityState, t.ActivityAttributes, t.Availability,
        };
        if (ServesAnswers)
        {
            served.UnionWith(HaContract.SessionDiscoveryTopics(node).Where(x => ServesStops || x != HaContract.DiscoveryTopic("button", node, "stop")));
            // The card's own publishes and the armed question: never withdrawn from under it.
            served.Add(t.ReplyPayload);
            served.Add(t.DecisionAttributes);
        }
        return HaContract.SessionDiscoveryTopics(node).Concat(HaContract.SessionStateTopics(node)).Where(x => !served.Contains(x));
    }

    // Clear-CopilotMqttOrphans: session entities in Home Assistant that no machine lists and that
    // are not running here. Every daemon sweeps at start, because one that died, or a session
    // that ended while no daemon ran, leaves its retained entities behind. Skipped, not guessed,
    // when the picture is incomplete: no machine's list visible means Home Assistant is still
    // starting, and deleting a running machine's cards is far worse than a dead one lingering.
    // What it finds still waits for its card to leave the dashboard before anything is cleared.
    async Task<IReadOnlyList<string>> OrphansAsync(HashSet<string> live, CancellationToken ct)
    {
        IReadOnlyList<HaState> states;
        try { states = await ha.GetStatesAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log($"Home Assistant connector orphan sweep skipped: {Describe(e)}");
            return [];
        }
        var claimed = new HashSet<string>(live, StringComparer.Ordinal);
        var machines = 0;
        foreach (var state in states)
        {
            var m = MachineSensor().Match(state.EntityId);
            if (!m.Success) { continue; }
            machines++;
            var slug = m.Groups[1].Value;
            claimed.Add(HaContract.MachineNode(slug));
            if (slug == Slug) { continue; }
            foreach (var node in ListedNodes(state.Attributes)) { claimed.Add(node); }
        }
        if (machines == 0)
        {
            log("Home Assistant connector orphan sweep skipped: no machine's session list is visible yet");
            return [];
        }
        var orphans = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var state in states)
        {
            var m = SessionEntity().Match(state.EntityId);
            if (m.Success && !claimed.Contains(m.Groups[1].Value)) { orphans.Add(m.Groups[1].Value); }
        }
        if (orphans.Count > 0) { log($"Home Assistant connector found {orphans.Count} orphaned session(s): {string.Join(", ", orphans)}"); }
        return [.. orphans];
    }

    [GeneratedRegex("^sensor\\.agent_bridge_([a-z0-9_]+)_sessions$")]
    private static partial Regex MachineSensor();

    [GeneratedRegex("^(?:select|sensor|text|button)\\.(agent_bridge_[0-9a-z]{12,})_")]
    private static partial Regex SessionEntity();

    static IEnumerable<string> ListedNodes(JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Object || !attributes.TryGetProperty("sessions", out var sessions) || sessions.ValueKind != JsonValueKind.Array) { yield break; }
        foreach (var s in sessions.EnumerateArray())
        {
            if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty("node", out var n) && n.ValueKind == JsonValueKind.String
                && n.GetString() is { Length: > 0 } node && node.StartsWith("agent_bridge_", StringComparison.Ordinal))
            {
                yield return node;
            }
        }
    }

    async Task SessionsAsync(DateTimeOffset now, CancellationToken ct)
    {
        // Drain the notifications before taking the snapshot, never after: a change landing in
        // between would otherwise be cleared while this pass publishes the older state, and an
        // already-published session would keep showing it until something else changed it.
        List<string> notified;
        bool everything;
        lock (gate)
        {
            notified = [.. dirty];
            everything = allDirty;
            dirty.Clear();
            allDirty = false;
        }
        var live = source.Snapshot().ToDictionary(s => s.Id, StringComparer.Ordinal);
        List<string> work;
        List<(string Id, string Node)> gone;
        lock (gate)
        {
            gone = [.. published.Where(p => !live.ContainsKey(p.Key)).Select(p => (p.Key, p.Value.Node))
                .Concat(starting.Where(p => !live.ContainsKey(p.Key)).Select(p => (p.Key, p.Value)))];
            work = everything ? [.. live.Keys] : [.. notified.Where(live.ContainsKey).Concat(live.Keys.Where(id => !published.ContainsKey(id))).Distinct()];
        }
        foreach (var (id, node) in gone)
        {
            lock (gate)
            {
                published.Remove(id);
                starting.Remove(id);
                retiring.TryAdd(node, new Retiring());
            }
            log($"Home Assistant connector: session {id[..Math.Min(8, id.Length)]} ended; its entities go once its card has left the dashboard");
        }
        if (gone.Count > 0) { SaveLedger(); WatchSetChanged(); }
        for (var i = 0; i < work.Count; i++)
        {
            try { await SessionAsync(live[work[i]], now, ct); }
            catch
            {
                // Tried again on the next pass, with everything after it this pass would have done.
                lock (gate) { foreach (var id in work.Skip(i)) { dirty.Add(id); } }
                throw;
            }
        }
    }

    async Task SessionAsync(Session s, DateTimeOffset now, CancellationToken ct)
    {
        var full = source.Get(s.Id) ?? s;
        var name = HaPayloads.SessionName(s);
        var node = HaContract.NodeId(s.Id);
        var t = HaContract.Topics(node);
        Published? p;
        lock (gate) { published.TryGetValue(s.Id, out p); }
        if (p is null)
        {
            lock (gate) { starting[s.Id] = node; }
            // On disk before the first write: a host stopped part-way, whose session then ends
            // while it is down, still retires what that write left behind.
            SaveLedger();
            // Availability first and retained, so a session never appears already stale.
            await ha.PublishAsync(t.Availability, "online", true, ct);
            if (ServesAnswers)
            {
                await PublishControlsAsync(t, name, ct);
            }
            else
            {
                await PublishSensorConfigsAsync(t, name, ct);
            }
            foreach (var topic in UnservedSessionTopics(node)) { await ha.PublishAsync(topic, "", true, ct); }
            if (ServesAnswers)
            {
                // Before anything is armed on them: a select is driven by the id the dashboard uses
                // (Publish-CopilotMqttSession forces them at once, after a moment to register).
                await Task.Delay(options.FieldPinDelay, time, ct);
                await PinNowAsync(SessionIds(node), now, ct);
            }
            else
            {
                AddPin($"{node}_status", $"sensor.{node}_status", now);
                AddPin($"{node}_activity", $"sensor.{node}_activity", now);
            }
            p = new Published { Node = node, Name = name, Kind = HaPayloads.Kind(s.Agent) };
            lock (gate)
            {
                starting.Remove(s.Id);
                published[s.Id] = p;
                retiring.Remove(node);
                ApplyLaunchedDriver(s.Id, p, now);
            }
            SaveLedger();
            WatchSetChanged();
        }
        else if (p.Name != name)
        {
            // Update-CopilotMqttSessionName: the device follows a rename through the two sensors,
            // the only entities with nothing to lose by being republished.
            await PublishSensorConfigsAsync(t, name, ct);
            p.Name = name;
        }

        // A new turn (a new message from the person or an agent) decides who drives it.
        var turn = $"{full.Activity.TurnCount}\n{full.Activity.UserMessage}";
        if (p.TurnKey is not null && p.TurnKey != turn) { p.TurnStarted(now, options.DriverArm); }
        else { p.TurnSeen(full.Activity); }
        p.TurnKey = turn;

        var status = HaPayloads.Status(s);
        var statusAttributes = HaPayloads.StatusAttributes(s, name, options.MachineName, now);
        var statusSignature = status + "\n" + Signature(statusAttributes);
        if (statusSignature != p.Status)
        {
            await ha.PublishAsync(t.StatusState, status, true, ct);
            await PublishAsync(t.StatusAttributes, statusAttributes, true, ct);
            p.Status = statusSignature;
        }

        var summary = HaPayloads.ActivitySummary(full);
        var detail = HaPayloads.ActivityAttributes(full, name, options.MachineName, verbose, now, driver: p.Driver,
            thinking: p.Observe(full.Activity.Response, full.Activity.Reasoning), answerConsumedAt: p.LastPayloadStamp);
        // Get-DaemonCardSummary: while End session waits for its second press, the card keeps
        // asking for it over whatever the session does meanwhile.
        if (p.Stop.Asking(now)) { summary = HaStop.ConfirmNote; detail["hint"] = p.Stop.Hint; }
        var activitySignature = summary + "\n" + Signature(detail);
        if (activitySignature != p.Activity)
        {
            await ha.PublishAsync(t.ActivityState, summary, true, ct);
            await PublishAsync(t.ActivityAttributes, detail, true, ct);
            p.Activity = activitySignature;
        }
        p.Detail = detail;

        await QuestionAsync(full, p, name, now, ct);
    }

    // Publish-CopilotMqttSession: every entity of a session's card, the selector parked on Idle.
    async Task PublishControlsAsync(SessionTopics t, string name, CancellationToken ct)
    {
        var machine = options.MachineName;
        var configs = new List<(string Topic, JsonObject Config)>
        {
            HaContract.DecisionConfig(t, name, machine, [HaDecision.Idle], enabledByDefault: true),
            HaContract.ReplyConfig(t, name, machine),
            HaContract.ReplyPayloadConfig(t, name, machine),
            HaContract.StatusConfig(t, name, machine),
            HaContract.ActivityConfig(t, name, machine),
        };
        for (var i = 1; i <= HaContract.MaxFields; i++) { configs.Add(HaContract.FieldConfig(t, name, machine, i, $"Field {i}", [HaDecision.Idle])); }
        configs.Add(HaContract.SubmitConfig(t, name, machine));
        if (ServesStops) { configs.Add(HaContract.StopConfig(t, name, machine)); }
        foreach (var (topic, config) in configs) { await PublishAsync(topic, config, true, ct); }
    }

    // The ids a session's entities are pinned to, by unique_id.
    Dictionary<string, string> SessionIds(string node)
    {
        var ids = new Dictionary<string, string>(HaContract.SessionSensorIds(node), StringComparer.Ordinal);
        if (ServesAnswers) { foreach (var (u, e) in HaContract.SessionControlIds(node)) { ids[u] = e; } }
        if (ServesStops) { ids[$"{node}_stop"] = SessionEntities.Stop(node); }
        return ids;
    }

    // Pins now what has registered, and queues the rest for PinsAsync.
    async Task PinNowAsync(IReadOnlyDictionary<string, string> wanted, DateTimeOffset now, CancellationToken ct)
    {
        var registry = await ha.ListEntityRegistryAsync(ct);
        var byUniqueId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in registry) { if (e.UniqueId is { } u) { byUniqueId[u] = e.EntityId; } }
        foreach (var (uniqueId, entityId) in wanted)
        {
            if (!byUniqueId.TryGetValue(uniqueId, out var current)) { AddPin(uniqueId, entityId, now); continue; }
            if (current == entityId) { continue; }
            try { await ha.RenameEntityAsync(current, entityId, ct); }
            catch (HaCommandException e) { log($"Home Assistant connector could not give {current} the id {entityId}: {e.Code}"); }
        }
    }

    async Task PublishSensorConfigsAsync(SessionTopics t, string name, CancellationToken ct)
    {
        var (statusTopic, statusConfig) = HaContract.StatusConfig(t, name, options.MachineName);
        var (activityTopic, activityConfig) = HaContract.ActivityConfig(t, name, options.MachineName);
        await PublishAsync(statusTopic, statusConfig, true, ct);
        await PublishAsync(activityTopic, activityConfig, true, ct);
    }

    // The machine's session list: on a change, and every GlobalReassert regardless, so a Home
    // Assistant that lost retained state has it back within the window (Publish-DaemonGlobalStatus).
    async Task GlobalAsync(DateTimeOffset now, CancellationToken ct)
    {
        List<HaPayloads.Descriptor> descriptors;
        lock (gate)
        {
            descriptors = [.. published.Values.OrderBy(p => p.Node, StringComparer.Ordinal)
                .Select(p => new HaPayloads.Descriptor(p.Name, options.MachineName, p.Node, p.Kind))];
        }
        var capabilities = HaPayloads.Capabilities(helperExists, options.Dev, LaunchCapabilities());
        var attributes = HaPayloads.GlobalAttributes(descriptors, options.MachineName, Slug, capabilities, now, source.Held?.Reason ?? "");
        var signature = Signature(attributes);
        if (signature == globalSignature && now - globalAt < HaContract.GlobalReassert) { return; }
        var (topic, config) = HaContract.SessionsConfig(Slug, options.MachineName);
        await PublishAsync(topic, config, true, ct);
        await ha.PublishAsync(HaContract.GlobalStateTopic(Slug), descriptors.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), true, ct);
        await PublishAsync(HaContract.GlobalAttributesTopic(Slug), attributes, true, ct);
        if (globalSignature is null) { AddPin($"agent_bridge_{Slug}_sessions", HaContract.MachineEntityId("sensor", "sessions", Slug), now); }
        globalSignature = signature;
        globalAt = now;
    }

    async Task HeartbeatAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (now < nextBeat) { return; }
        // Deliberately not retained (Publish-CopilotMqttMachineHeartbeat).
        foreach (var beat in HaContract.Heartbeat(Slug, now)) { await ha.PublishAsync(beat.Topic, beat.Payload, beat.Retain, ct); }
        nextBeat = now + HaContract.HeartbeatInterval;
    }

    async Task UsageAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (usage is null) { return; }
        var (report, version) = usage();
        if (version == usageVersion) { return; }
        foreach (var agent in report.Agents)
        {
            if (HaPayloads.Usage(agent, options.MachineName, Slug, now) is not { } u) { continue; }
            // A further login's reading goes on its own numbered sensor, and only one this host was
            // configured with: the rest were withdrawn when it started.
            var further = u.Key != u.Client;
            if (further && !options.UsageAccountSensors.Contains(u.Key)) { continue; }
            var signature = u.State + "\n" + Signature(u.Attributes);
            if (usageSignatures.TryGetValue(u.Key, out var previous) && previous == signature) { continue; }
            var (topic, config) = HaContract.UsageConfig(Slug, options.MachineName, u.Key, u.Label, further ? agent.Account ?? "" : null);
            await PublishAsync(topic, config, true, ct);
            await ha.PublishAsync(HaContract.UsageStateTopic(Slug, u.Key), u.State, true, ct);
            await PublishAsync(HaContract.UsageAttributesTopic(Slug, u.Key), u.Attributes, true, ct);
            if (previous is null) { AddPin($"agent_bridge_{Slug}_usage_{u.Key}", HaContract.MachineEntityId("sensor", $"usage_{u.Key}", Slug), now); }
            usageSignatures[u.Key] = signature;
        }
        usageVersion = version;
    }

    void AddPin(string uniqueId, string entityId, DateTimeOffset now)
    {
        lock (gate) { pins[uniqueId] = new Pin(entityId, now + options.PinDelay, 0); }
    }

    // Home Assistant names an MQTT entity after its device and ignores object_id, so every
    // entity is forced onto the id the dashboard and the other machines use, through the entity
    // registry, once it has registered (Set-CopilotMqttMachineEntityId, Set-CopilotMqttEntityIds).
    async Task PinsAsync(DateTimeOffset now, CancellationToken ct)
    {
        Dictionary<string, Pin> due;
        lock (gate) { due = pins.Where(p => p.Value.NotBefore <= now).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal); }
        if (due.Count == 0) { return; }
        var registry = await ha.ListEntityRegistryAsync(ct);
        var byUniqueId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in registry) { if (e.UniqueId is { } u) { byUniqueId[u] = e.EntityId; } }
        foreach (var (uniqueId, pin) in due)
        {
            if (!byUniqueId.TryGetValue(uniqueId, out var current))
            {
                lock (gate)
                {
                    if (pin.Attempts + 1 >= options.PinAttempts)
                    {
                        pins.Remove(uniqueId);
                        log($"Home Assistant connector: {uniqueId} never registered, so it could not be given the id {pin.EntityId}");
                    }
                    else { pins[uniqueId] = pin with { NotBefore = now + options.PinDelay, Attempts = pin.Attempts + 1 }; }
                }
                continue;
            }
            if (current != pin.EntityId)
            {
                try { await ha.RenameEntityAsync(current, pin.EntityId, ct); }
                catch (HaCommandException e)
                {
                    // Most often the id is held by an entity left from before; the dashboard
                    // then reads that one, which is worth saying once rather than retrying for ever.
                    log($"Home Assistant connector could not give {current} the id {pin.EntityId}: {e.Code}");
                }
            }
            lock (gate) { pins.Remove(uniqueId); }
        }
    }

    // The machine's Detailed activity switch, created by agent-ha-bridge (per machine since its
    // 1.2x releases). Its absence is not "off": the configured fallback applies, as
    // Test-VerboseStreaming does, and the dashboard draws no switch for this machine.
    async Task DetailedAsync(CancellationToken ct)
    {
        var state = await ha.GetStateAsync(HaContract.MachineEntityId("input_boolean", "detailed_activity", Slug), ct);
        var exists = state is { State: "on" or "off" };
        var value = state?.State switch { "on" => true, "off" => false, _ => options.DetailedActivityFallback };
        if (value != verbose)
        {
            verbose = value;
            lock (gate) { allDirty = true; }
        }
        helperExists = exists;
    }

    // Ended sessions' entities, once the dashboard served to browsers no longer renders them.
    // The served config is read as text and a node anywhere in it holds its retirement: that is
    // stricter than "rendered" (a mention in display text holds too), which errs the safe way,
    // and needs no trust in who wrote the config. A dashboard that cannot be read holds them all.
    async Task RetireAsync(DateTimeOffset now, CancellationToken ct)
    {
        List<string> nodes;
        lock (gate)
        {
            var live = published.Values.Select(p => p.Node).ToHashSet(StringComparer.Ordinal);
            foreach (var node in retiring.Keys.Where(live.Contains).ToList()) { retiring.Remove(node); }
            nodes = [.. retiring.Keys];
        }
        if (nodes.Count == 0) { return; }
        var dashboard = await ha.ReadDashboardAsync(options.DashboardUrlPath, ct);
        var served = dashboard.Found ? dashboard.Json ?? "" : "";
        var retired = 0;
        foreach (var node in nodes)
        {
            Retiring r;
            lock (gate) { if (!retiring.TryGetValue(node, out r!)) { continue; } }
            if (served.Contains(node, StringComparison.Ordinal)) { r.AbsentSince = null; continue; }
            if (r.AbsentSince is null) { r.AbsentSince = now; continue; }
            if (now - r.AbsentSince < options.RetireGrace) { continue; }
            foreach (var m in HaContract.SessionRemoval(node)) { await ha.PublishAsync(m.Topic, m.Payload, m.Retain, ct); }
            lock (gate) { retiring.Remove(node); }
            retired++;
            log($"Home Assistant connector retired {node}");
        }
        if (retired > 0) { SaveLedger(); }
    }

    Task PublishAsync(string topic, JsonNode payload, bool retain, CancellationToken ct) =>
        ha.PublishAsync(topic, HaContract.Serialize(payload), retain, ct);

    // A payload's identity without its stamp: "updated" changes on every build of it, so
    // comparing with it would republish everything on every pass.
    static string Signature(JsonObject payload)
    {
        var copy = (JsonObject)payload.DeepClone();
        copy.Remove("updated");
        return HaContract.Serialize(copy);
    }

    // The nodes waiting for retirement and the nodes whose first publication had begun but not
    // finished: both have entities in Home Assistant that nothing else may remember.
    IEnumerable<string> LoadLedger()
    {
        if (options.StateFile is not { } path || !File.Exists(path)) { return []; }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { return []; }
            return [.. new[] { "retiring", "starting" }
                .SelectMany(key => doc.RootElement.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToArray() : [])
                .Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)
                .Where(n => n.StartsWith("agent_bridge_", StringComparison.Ordinal))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            log($"Home Assistant connector could not read {path} ({e.Message}); the orphan sweep finds what it named");
            return [];
        }
    }

    void SaveLedger()
    {
        if (options.StateFile is not { } path) { return; }
        string text;
        lock (gate)
        {
            text = HaContract.Serialize(new JsonObject
            {
                ["retiring"] = new JsonArray([.. retiring.Keys.Order(StringComparer.Ordinal).Select(n => (JsonNode)n)]),
                ["starting"] = new JsonArray([.. starting.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(n => (JsonNode)n)]),
            });
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Problem("ledger", $"Home Assistant connector could not write {path}: {e.Message}");
        }
    }

    // Said once per kind of failure until it clears, so a Home Assistant that is down for an hour
    // is one line in the log rather than thousands.
    void Problem(string key, string message)
    {
        lock (gate)
        {
            if (problems.TryGetValue(key, out var said) && said == message) { return; }
            problems[key] = message;
        }
        log(message);
    }

    void Resolved(string key)
    {
        bool had;
        lock (gate) { had = problems.Remove(key); }
        if (had) { log($"Home Assistant connector {key} recovered"); }
    }

    // Exception text from HTTP and WebSocket failures carries no secret (the token is only ever
    // a header), but a type name says more than "an error occurred".
    static string Describe(Exception e) => $"{e.GetType().Name}: {e.Message}";

    public async ValueTask DisposeAsync()
    {
        source.Changed -= OnChanged;
        if (stop is not null)
        {
            await stop.CancelAsync();
            try { await loop; } catch (OperationCanceledException) { }
            stop.Dispose();
        }
        await StopRepliesAsync();
        // Nothing is withdrawn on the way out, as the daemon withdraws nothing: liveness lapses on
        // its own (expire_after), the cards stay, and whichever bridge starts next takes them over.
        SaveLedger();
        passes.Dispose();
    }

    // agent-ha-bridge is running for this user: the message says how it was found.
    sealed class AgentHaBridgeRunning(string why) : Exception(why);

    // Home Assistant with the G5 check in front of every write. Reads pass straight through:
    // they change nothing the daemon owns.
    sealed class Guarded(IHomeAssistant inner, Func<string?> agentHaBridge) : IHomeAssistant
    {
        void Check()
        {
            if (agentHaBridge() is { } active) { throw new AgentHaBridgeRunning(active); }
        }

        public Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken = default)
        {
            Check();
            return inner.PublishAsync(topic, payload, retain, cancellationToken);
        }

        public Task RenameEntityAsync(string entityId, string newEntityId, CancellationToken cancellationToken = default)
        {
            Check();
            return inner.RenameEntityAsync(entityId, newEntityId, cancellationToken);
        }

        public Task CallServiceAsync(string domain, string service, JsonObject data, CancellationToken cancellationToken = default)
        {
            Check();
            return inner.CallServiceAsync(domain, service, data, cancellationToken);
        }

        public Task DeleteImageAsync(string imageId, CancellationToken cancellationToken = default)
        {
            Check();
            return inner.DeleteImageAsync(imageId, cancellationToken);
        }

        public Task<byte[]?> DownloadImageAsync(string imageId, long maxBytes, CancellationToken cancellationToken = default) => inner.DownloadImageAsync(imageId, maxBytes, cancellationToken);
        public IAsyncEnumerable<HaTrigger> WatchAsync(IReadOnlyCollection<string> entityIds, CancellationToken cancellationToken = default) => inner.WatchAsync(entityIds, cancellationToken);
        public Task<IReadOnlyList<HaMessage>> ReadTopicsAsync(IReadOnlyList<string> filters, TimeSpan window, CancellationToken cancellationToken = default) => inner.ReadTopicsAsync(filters, window, cancellationToken);

        public Task<HaState?> GetStateAsync(string entityId, CancellationToken cancellationToken = default) => inner.GetStateAsync(entityId, cancellationToken);
        public Task<IReadOnlyList<HaState>> GetStatesAsync(CancellationToken cancellationToken = default) => inner.GetStatesAsync(cancellationToken);
        public Task<IReadOnlyList<HaRegistryEntry>> ListEntityRegistryAsync(CancellationToken cancellationToken = default) => inner.ListEntityRegistryAsync(cancellationToken);
        public Task<HaDashboard> ReadDashboardAsync(string urlPath, CancellationToken cancellationToken = default) => inner.ReadDashboardAsync(urlPath, cancellationToken);
    }
}
