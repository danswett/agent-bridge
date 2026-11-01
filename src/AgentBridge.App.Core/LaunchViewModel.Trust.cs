using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// A launched agent asking whether to trust its folder (docs/protocol/v1-launch.md, "Folder
// trust"), as the Launch page shows it: the agent, the whole folder, the repository root it
// applies to and the agent's own words, never shortened. Where the machine allows it, Trust and
// Quit answer it with launch.trust.answer and a command this app signs as a person's device for
// that machine, question and choice; otherwise it says to answer at the machine, and how to allow
// it there. Like a transfer, an answer only ever goes through the relay: for this machine, to its
// own host through the relay (Fleet.Loopback), never the pipe, which agents reach.
public sealed class TrustCard : Observable
{
    readonly LaunchViewModel page;

    internal TrustCard(LaunchViewModel page, string machineKey, TrustQuestion question)
    {
        this.page = page;
        MachineKey = machineKey;
        Question = question;
    }

    internal string MachineKey { get; }
    internal TrustQuestion Question { get; private set; }
    public string Id => Question.Id;
    public string Title { get; private set; } = "";
    public string Folder => Question.Folder;
    public string RootNote => Question.RootNote ?? "Trusting applies to the repository root:";
    public string Root => Question.Root ?? "";
    public bool ShowRoot => Question.Root is { Length: > 0 };
    public string Warning => Question.Warning ?? "";
    public bool ShowWarning => Question.Warning is { Length: > 0 };
    public bool CanAnswer { get; private set; }
    public bool ShowResend { get; private set; }
    public string AtMachine { get; private set; } = "";
    public bool ShowAtMachine => AtMachine.Length > 0;
    public bool ShowAllowHere { get; private set; }
    public string StatusText { get; private set; } = "";
    public bool ShowStatus => StatusText.Length > 0;
    public Tone StatusTone { get; private set; }
    // For tests and the self-test: waiting, sending, failed, or the answer's outcome.
    public string State { get; private set; } = "waiting";

    public void Trust() => page.AnswerTrust(this, TrustProtocol.Trust);
    public void Quit() => page.AnswerTrust(this, TrustProtocol.Quit);
    public void Resend() => page.ResendTrust(this);
    public Task AllowHereAsync() => page.AllowHereAsync();

    internal void Update(TrustQuestion question, string title, bool canAnswer, bool resend, string atMachine, bool allowHere, (string Text, Tone Tone) status, string state)
    {
        var changed = !ReferenceEquals(question, Question);
        Question = question;
        if (changed)
        {
            foreach (var p in new[] { nameof(Folder), nameof(RootNote), nameof(Root), nameof(ShowRoot), nameof(Warning), nameof(ShowWarning) }) { Raise(p); }
        }
        if (Title != title) { Title = title; Raise(nameof(Title)); }
        if (CanAnswer != canAnswer) { CanAnswer = canAnswer; Raise(nameof(CanAnswer)); }
        if (ShowResend != resend) { ShowResend = resend; Raise(nameof(ShowResend)); }
        if (AtMachine != atMachine) { AtMachine = atMachine; Raise(nameof(AtMachine)); Raise(nameof(ShowAtMachine)); }
        if (ShowAllowHere != allowHere) { ShowAllowHere = allowHere; Raise(nameof(ShowAllowHere)); }
        // Only a change is news: the status line is announced to screen readers when raised.
        if (StatusText != status.Text || StatusTone != status.Tone)
        {
            (StatusText, StatusTone) = status;
            Raise(nameof(StatusText));
            Raise(nameof(StatusTone));
            Raise(nameof(ShowStatus));
        }
        if (State != state) { State = state; Raise(nameof(State)); }
    }
}

public sealed partial class LaunchViewModel
{
    const string TrustKey = "trust:";
    // The host waits up to a minute to see what the answer did, after typing it.
    static readonly TimeSpan TrustTimeout = TimeSpan.FromSeconds(120);

    // An answer pressed here: the machine the question is on, the connection it went through (the
    // machine's own, or this machine's loopback), and the request, which a resend repeats.
    sealed record TrustAnswer(string Machine, Machine Via, TrustQuestion Question, string Choice, string Request, Func<string, object> Data);

    // By "<machine key>/<question id>": kept across machines picked, so an answer still on its way
    // shows when its machine is picked again.
    readonly Dictionary<string, TrustAnswer> trustAnswers = new(StringComparer.Ordinal);
    readonly Dictionary<string, TrustCard> trustCards = new(StringComparer.Ordinal);
    // This machine's questions as its host lists them through the relay, where each says whether
    // this app may answer it: the pipe's never can. Null until asked, or when it could not be.
    IReadOnlyList<TrustQuestion>? localTrust;
    // Answers whose outcome was acted on (a session opened), by request.
    readonly HashSet<string> trustHandled = new(StringComparer.Ordinal);
    string trustNote = "";

    public IReadOnlyList<TrustCard> TrustQuestions { get; private set; } = [];
    public bool ShowTrust => TrustQuestions.Count > 0;
    // What went wrong allowing answers here, when it did.
    public string TrustNote => trustNote;

    static string AnswerKey(string machine, string question) => $"{machine}/{question}";

    // The connection an answer to m's questions goes through: m's own, through the relay; for this
    // machine, its loopback.
    Machine? TrustVia(Machine m) => m.IsLocal ? watchedLoopback : m;

    bool Answers(Machine? via) => via is not null && main.Fleet is not null && via.Host.Can(TrustProtocol.Capability);

    IEnumerable<TrustQuestion> TrustFor(Machine m)
    {
        var seen = new Dictionary<string, TrustQuestion>(StringComparer.Ordinal);
        void Add(TrustQuestion? q) { if (q is not null) { seen.TryAdd(q.Id, q); } }
        foreach (var q in (m.IsLocal ? localTrust ?? options?.Trust : options?.Trust) ?? []) { Add(q); }
        if (lastMachine == m && lost is null && last is { State: DeliveryState.Done } e && e.Request != supersededResult) { Add(Result(e)?.Trust); }
        // What the machine says waits now: an answer remembered here for a question it no longer
        // lists is shown for its outcome, but never offered again.
        liveTrust = [.. seen.Keys];
        foreach (var a in trustAnswers.Values.Where(a => a.Machine == m.Key)) { Add(a.Question); }
        return seen.Values;
    }

    HashSet<string> liveTrust = new(StringComparer.Ordinal);

    // A launch's outcome can name a question the machine's listing did not have yet. It counts only
    // until a listing asked for after it arrives: from then that listing says whether it still
    // waits, so one answered at the machine or closed meanwhile is not offered again (review of
    // #137). The outcome's request, its machine and the load counter when it arrived.
    (string Request, Machine Machine, int Load)? resultSeen;
    string? supersededResult;

    void SeenResult(Machine m, Delivery d) => resultSeen = (d.Request, m, loads);

    void Listed(Machine m, int load)
    {
        if (resultSeen is { } r && r.Machine == m && load > r.Load) { supersededResult = r.Request; }
    }

    void DrawTrust()
    {
        var m = machine;
        var questions = m is null ? [] : TrustFor(m).ToList();
        var cards = new List<TrustCard>();
        foreach (var q in questions)
        {
            var key = AnswerKey(m!.Key, q.Id);
            if (!trustCards.TryGetValue(key, out var card)) { trustCards[key] = card = new TrustCard(this, m.Key, q); }
            UpdateCard(m, card, q);
            cards.Add(card);
        }
        // The same cards, kept: a redraw does not recreate what is on screen, or move focus.
        if (!cards.SequenceEqual(TrustQuestions))
        {
            TrustQuestions = cards;
            Raise(nameof(TrustQuestions));
            Raise(nameof(ShowTrust));
        }
        foreach (var key in trustCards.Keys.Where(k => !trustAnswers.ContainsKey(k) && cards.All(c => AnswerKey(c.MachineKey, c.Id) != k)).ToList()) { trustCards.Remove(key); }
    }

    void UpdateCard(Machine m, TrustCard card, TrustQuestion q)
    {
        var via = TrustVia(m);
        var answer = trustAnswers.GetValueOrDefault(AnswerKey(m.Key, q.Id));
        var d = answer is null ? null : answer.Via.Deliveries.Get(TrustKey + q.Id) is { } got && got.Request == answer.Request ? got : null;
        // The connection it went through was let go (another account) before it said.
        var orphaned = answer is not null && answer.Via != m && answer.Via != watchedLoopback && d?.State is not DeliveryState.Done;
        var outcome = d is { State: DeliveryState.Done } done ? TrustOutcome(done) : null;
        var settled = orphaned || outcome is "started" or "declined" or "unconfirmed";
        var pending = !orphaned && d?.State is DeliveryState.Sending or DeliveryState.Waiting;
        var retry = !orphaned && d?.State == DeliveryState.Failed;
        var live = liveTrust.Contains(q.Id);
        var canAnswer = q.Answerable && live && Answers(via) && !settled && !pending && !retry;
        var name = m.Name;
        var atMachine = canAnswer || settled || pending || retry ? ""
            : !live && (options is not null || localTrust is not null) ? $"{name} no longer lists this question as waiting."
            : q.AtMachine ?? (q.Answerable ? $"Answer at the machine: this app cannot answer it on {name} now." : "Answer at the machine.");
        var status = orphaned
            ? ("Not confirmed: answer at the machine. The relay connection changed before it said what the answer did.", Tone.Warn)
            : answer is null || d is null ? ("", Tone.None) : DescribeTrust(d, answer, q.Label, name);
        var state = orphaned ? "unconfirmed" : d is null ? "waiting" : d.State == DeliveryState.Done ? outcome! : d.State.ToString().ToLowerInvariant();
        card.Update(q, $"{q.Label} on {name} is asking whether to trust a folder", canAnswer, retry, atMachine, !canAnswer && live && !settled && !pending && !retry && AllowsHere(m, q), status, state);
    }

    // Whether a person here can allow answering this machine's question from this app: the
    // machine is this one, the question is only held back by its setting or this app not being
    // approved (not agent-ha-bridge, read-only, or a window that could not be read), and the app
    // has a device id to approve.
    bool AllowsHere(Machine m, TrustQuestion q)
    {
        if (!m.IsLocal || q.Answerable || main.Fleet?.DeviceId is not { } me) { return false; }
        if (m.Host.Status is not { } s || s.AgentHaBridge is not null || TrustEditor.For(m.Host).Settings is not { } now) { return false; }
        return !now.RemoteFolderTrust || !(now.RemoteFolderTrustDevices ?? []).Contains(me);
    }

    static string TrustOutcome(Delivery d) => d.Outcome is "started" or "declined" or "refused" or "unconfirmed" ? d.Outcome : "unconfirmed";

    static TrustAnswerResult? TrustResult(Delivery d)
    {
        if (d.Result.ValueKind != JsonValueKind.Object) { return null; }
        try { return d.Result.Deserialize<TrustAnswerResult>(ProtocolJson.Options); }
        catch (JsonException) { return null; }
    }

    // What an answer's progress or outcome says (describeTrust in the PWA).
    static (string Text, Tone Tone) DescribeTrust(Delivery d, TrustAnswer a, string label, string machineName)
    {
        var trust = a.Choice == TrustProtocol.Trust;
        switch (d.State)
        {
            case DeliveryState.Sending:
                return (trust ? $"Trusting the folder and waiting for {label} to start… This takes up to a minute." : $"Telling {label} not to trust the folder…", Tone.Progress);
            case DeliveryState.Waiting:
                return ($"Waiting for the connection to {machineName}. The answer goes as soon as it is back.", Tone.Progress);
            case DeliveryState.Failed:
                return ($"{machineName} returned an error: {Sentence(d.Reason ?? "no reason given")} The answer may have been typed: Try again sends that same answer, which {machineName} acts on once.", Tone.Warn);
        }
        return TrustOutcome(d) switch
        {
            "started" => ($"Trusted. {label} started.", Tone.Ok),
            "declined" => ($"Not trusted. {Sentence(d.Reason ?? $"{label} closed; nothing was launched")}", Tone.Ok),
            "refused" => ($"Not answered: {Sentence(d.Reason ?? "the machine refused it")}", Tone.Bad),
            _ => (d.Reason is { Length: > 0 } r && r.StartsWith("Not confirmed", StringComparison.Ordinal) ? Sentence(r) : $"Not confirmed: answer at the machine.{(d.Reason is { Length: > 0 } why ? $" {Sentence(why)}" : "")}", Tone.Warn),
        };
    }

    // A press: Trust or Quit, signed for that choice at each send with the delivery's request, which
    // the machine acts on once whatever is resent.
    internal void AnswerTrust(TrustCard card, string choice)
    {
        if (main.MachineOf(card.MachineKey) is not { } m || TrustVia(m) is not { } via || !Answers(via) || main.Fleet is not { } fleet || DeviceOf(m) is not { } machineId) { return; }
        var key = AnswerKey(m.Key, card.Id);
        if (trustAnswers.TryGetValue(key, out var known) && known.Via.Deliveries.Get(TrustKey + card.Id) is { State: not DeliveryState.Done } d && d.Request == known.Request) { return; }
        var question = card.Question;
        if (!question.Answerable) { return; }
        object Data(string request) => new TrustAnswerRequest
        {
            Question = question.Id,
            Choice = choice,
            Command = fleet.SignTrust(via.Host.PeerKey, machineId, question.Id, choice, request)
                ?? throw new HostNotRunningException($"not connected to {m.Name} through the relay"),
        };
        // Never replayed to a host that restarted: it remembers the request only in memory.
        var sent = via.Deliveries.Submit(TrustKey + question.Id, TrustProtocol.Answer, Data, outcome => outcome != "refused", payload: choice,
            replayAfterRestart: false, timeout: TrustTimeout, resendOnBusy: false);
        trustAnswers[key] = new TrustAnswer(m.Key, via, question, choice, sent.Request, Data);
        DrawTrust();
    }

    // After an error: that same request again, which the machine answers with its first outcome.
    internal void ResendTrust(TrustCard card)
    {
        if (!trustAnswers.TryGetValue(AnswerKey(card.MachineKey, card.Id), out var a)) { return; }
        a.Via.Deliveries.Resend(TrustKey + card.Id, TrustProtocol.Answer, a.Data);
    }

    // At this machine: turns on answering folder-trust questions from devices, with this app among
    // them, as the Settings page does and through the same editor, so it adds this app to the
    // host's list as it is then rather than to a status from before a change made in Settings.
    // This app is the person at the machine (the trusted local caller, #67), over the pipe.
    internal async Task AllowHereAsync()
    {
        var host = main.Local.Host;
        if (host.Status is null || main.Fleet?.DeviceId is not { } me) { return; }
        try
        {
            await TrustEditor.For(host).EditAsync(now =>
            {
                var devices = now.RemoteFolderTrustDevices ?? [];
                return now.RemoteFolderTrust && devices.Contains(me) ? null
                    : new SettingsRequest(RemoteFolderTrust: true, RemoteFolderTrustDevices: devices.Contains(me) ? null : [.. devices, me]);
            });
            SetTrustNote("");
        }
        // Behind an async void click handler: whatever goes wrong is shown, never thrown.
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SetTrustNote($"Couldn't allow it here: {Sentence(e.Message)}");
            return;
        }
        if (machine?.IsLocal == true) { await RefreshAsync(); }
    }

    void SetTrustNote(string text)
    {
        ui.Post(() =>
        {
            if (trustNote == text) { return; }
            trustNote = text;
            Raise(nameof(TrustNote));
        });
    }

    void OnTrustDelivery(Machine m, Delivery d)
    {
        DrawTrust();
        if (d.State != DeliveryState.Done) { return; }
        var answer = trustAnswers.Values.FirstOrDefault(a => a.Request == d.Request);
        if (answer is null || !trustHandled.Add(d.Request)) { return; }
        if (TrustOutcome(d) == "started")
        {
            // The launch from here that met this question has now started: its prompt went with it,
            // as for a launch that starts at once, and must not go again with the next launch. Only
            // that prompt: one typed since stays.
            if (lastMachine?.Key == answer.Machine && last is { State: DeliveryState.Done, Payload: LaunchRequest sent } && Result(last)?.Trust?.Id == answer.Question.Id
                && (string.IsNullOrWhiteSpace(prompt) ? null : prompt) == sent.Prompt) { Prompt = ""; }
            if (TrustResult(d)?.Session is { } id) { main.Open(answer.Machine, id, untilItArrives: true); }
        }
        // Its busy and its questions have changed.
        if (machine?.Key == answer.Machine && shown) { _ = RefreshAsync(); }
    }

    // The relay or this machine's loopback came up after this machine's questions were listed
    // through the pipe alone: list them through the relay now, so one this app may answer offers
    // Trust and Quit without a reload. One at a time.
    // A change that came while one was running asks again once it is done: the first may have
    // looked before the loopback's hello was in, and found nothing to list.
    bool relisting;
    bool relistAgain;

    void RelistLocalTrust()
    {
        if (!shown || machine is not { IsLocal: true } m || localTrust is not null || options?.Trust is not { Count: > 0 }) { return; }
        if (main.Fleet?.State != FleetState.Connected) { return; }
        if (relisting) { relistAgain = true; return; }
        relisting = true;
        relistAgain = false;
        _ = RelistAsync(m, loads);
    }

    // Started from the UI thread and continued there, so what it reads of the connection follows
    // the updates already queued for it (its hello, which says whether it answers), rather than
    // racing them from the thread pool.
    async Task RelistAsync(Machine m, int load)
    {
        IReadOnlyList<TrustQuestion>? got = null;
        try { got = await LocalTrustAsync(options); }
        finally
        {
            ui.Post(() =>
            {
                relisting = false;
                if (load == loads && m == machine && got is not null)
                {
                    localTrust = got;
                    DrawTrust();
                }
                if (relistAgain) { relistAgain = false; RelistLocalTrust(); }
            });
        }
    }

    // This machine's questions through the relay, where the pipe listed any: only then, since
    // launch.options is costly at the host.
    async Task<IReadOnlyList<TrustQuestion>?> LocalTrustAsync(LaunchOptions? piped)
    {
        if (piped?.Trust is not { Count: > 0 } || main.Fleet is not { } f) { return null; }
        var lb = f.Loopback();
        if (lb is null) { return null; }
        ui.Post(WatchLoopback);
        try
        {
            await lb.Host.WhenConnectedAsync().WaitAsync(TimeSpan.FromSeconds(20));
            if (!lb.Host.Can(TrustProtocol.Capability)) { return null; }
            var o = await lb.Host.CallAsync<LaunchOptions>(LaunchProtocol.Options, timeout: OptionsTimeout);
            return o.Trust ?? [];
        }
        catch (Exception e) when (e is IOException or HostErrorException or TimeoutException or JsonException) { return null; }
    }
}
