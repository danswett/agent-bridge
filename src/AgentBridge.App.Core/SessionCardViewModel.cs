using System.Collections.ObjectModel;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// What every card shares: who answers, the host's capabilities, where folds are kept.
public sealed class CardContext
{
    public required Deliveries Deliveries { get; init; }
    public required FoldStore Folds { get; init; }
    public required Func<IReadOnlyList<string>> Capabilities { get; init; }
    public Func<string?> MachineName { get; init; } = () => null;
    // Whether a card names its machine: once the app shows more than one.
    public Func<bool> ShowMachine { get; init; } = () => false;
    public Func<string?> ReadOnlyWhy { get; init; } = () => null;
    public Func<bool> ShowDetail { get; init; } = () => true;
    public HostConnection? Host { get; init; }
    public IImageShrinker? Shrinker { get; init; }
    // Another machine's: the relay's blob store, and that machine's relay device id, for files over
    // 256 KiB (sessions.attach.blob). Null for this machine, whose pipe takes them.
    public IBlobStore? Blobs { get; init; }
    public string? BlobsFor { get; init; }
    public Func<string, Task<Session?>>? FetchDetail { get; init; }
    // session.get { id, agent }: one delegate in full (Attribution.FetchDelegateAsync); null once
    // it has gone. Throws as the connection does.
    public Func<string, string, Task<Subagent?>>? FetchDelegate { get; init; }
    // Where what a fetch brings back is applied: the UI thread. Inline when unset.
    public IUiDispatcher? Ui { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;
    // Set by the card's Machine (#269): whether its rows are its host's word now, to be acted on;
    // and what a row says while the machine is away, null while it is not.
    public Func<bool> Current { get; internal set; } = () => true;
    public Func<DateTimeOffset, StatusLine?> Away { get; internal set; } = _ => null;
}

public sealed record ToolRow(StatusGlyph Glyph, string Status, string Name, string? Summary, string When);

// One session's card (pwa/js/ui/session-card.js, which follows agent-ha-bridge's card): its
// status as a glyph and words (docs/design/ui.md §2.2, StatusRules), a header that stays when
// folded, and below it the question, the response, reasoning, the last message, the reply box,
// the tool history, the facts and End session.
public sealed partial class SessionCardViewModel : Observable
{
    static readonly Dictionary<AgentKind, string> AgentNames = new() { [AgentKind.Copilot] = "Copilot", [AgentKind.Claude] = "Claude", [AgentKind.Codex] = "Codex", [AgentKind.Unknown] = "Agent" };

    readonly CardContext ctx;
    Session session;
    bool folded;
    QuestionViewModel? question;
    ReplyViewModel? reply;
    EndSessionViewModel? end;
    string? orphanText;
    string? orphanNote;
    string? receiptKey;
    string? lastHistoryAt;
    bool historyLoading;
    string historyNote = "";
    string capabilitiesSeen = "";
    IReadOnlyList<string> lastCaps = [];
    ReplyViewModel? parkedReply;
    bool synced;

    public SessionCardViewModel(Session session, CardContext ctx)
    {
        this.session = session;
        this.ctx = ctx;
        folded = ctx.Folds.IsFolded(session.Id);
        Delegates = new DelegatesViewModel(session, ctx, () => reply);
        ctx.Deliveries.Changed += OnDelivery;
        Update(session);
    }

    public Session Session => session;
    // Its delegates (ui.md §6.3): the strip, the Delegates tab, the inspector and the
    // conversation's one-line events.
    public DelegatesViewModel Delegates { get; }
    public string Id => session.Id;
    public AgentKind Agent => session.Agent;
    public string AgentName => AgentNames.GetValueOrDefault(session.Agent, "Agent");
    public string Title => string.IsNullOrEmpty(session.Title) ? "Untitled session" : session.Title;
    public string Meta => string.Join(" · ", new[] { ctx.ShowMachine() ? ctx.MachineName() : null, AgentName, session.Repository ?? Words.LastSegment(session.Cwd), session.Branch, session.Model }.Where(s => !string.IsNullOrEmpty(s)));

    // What the card says about the session: ui.md §2.2's glyph and words (StatusRules); while its
    // machine is away, that it is offline (or reconnecting) instead, since what it last said may
    // no longer be so (#269).
    public StatusLine Line => ctx.Away(ctx.Time.GetUtcNow()) ?? StatusRules.For(session);
    // Its machine is away: the row is what the session last said, and nothing on it can be done.
    public bool Away => Line.Rule == StatusRules.Away;
    public (string View, string Text) State => (Line.View, Line.Text);

    public string View => Line.View;
    public string StatusText => Line.Text;
    public StatusGlyph Glyph => Line.Glyph;
    public StatusTone StatusTone => Line.Tone;
    // The glyph's accessible name, as a row reads (ui.md §9).
    public string GlyphLabel => Line.Label(Title, ctx.MachineName());

    // An agent launched it, or answered or replied to it, and nobody has taken it back since: the
    // violet diamond on the glyph (rule 7), said in words too. An older host never says so, and
    // then it is the person's.
    public bool Driven => session.Driver == DriverKind.Agent && !Away;
    public string DriverText => Driven ? "Driven by an agent" : "";
    // Which agent, when the host knows ("homeassistant:<user id>").
    public string? DriverPrincipal => Driven ? session.DriverPrincipal : null;
    public string SinceText { get; private set; } = "";
    public string UpdatedText { get; private set; } = "";

    public bool HasTool => session.Activity.CurrentTool is not null;
    public string ToolName => session.Activity.CurrentTool?.Name ?? "";
    public string ToolSummary => session.Activity.CurrentTool?.Summary ?? "";
    // A running tool turns, as working does; a finished one is done or failed.
    public StatusGlyph ToolGlyph => ToolGlyphOf(session.Activity.CurrentTool?.Status ?? ToolStatus.Running);
    // The tool glyph's accessible name: the words beside it are the tool's name and summary, so
    // only the glyph says whether it is running, done or failed (ui.md §9), as a history row's does.
    public string ToolState => (session.Activity.CurrentTool?.Status ?? ToolStatus.Running).ToString().ToLowerInvariant();
    internal static StatusGlyph ToolGlyphOf(ToolStatus s) => s switch { ToolStatus.Running => StatusGlyph.Working, ToolStatus.Failed => StatusGlyph.Failed, _ => StatusGlyph.Done };

    // The main agent's own words, which the session pane labels "Main agent" (§6.4). The top-level
    // text is whoever wrote last: where activity.source names a delegate for it, activity.own
    // carries the main agent's, and own's stands in when the top-level text is absent
    // (docs/design/subagents.md, "New clients"; Attribution.MainVoiceOf, the PWA's rule). A
    // delegate's words are only ever on its own row, strip item or inspector (Delegates).
    MainVoice Voice => Attribution.MainVoiceOf(session);
    bool ResponseByDelegate => !string.IsNullOrEmpty(session.Activity.Source?.Response) || string.IsNullOrEmpty(session.Activity.Response);
    // Whether the host says whose the words are (pwa/js/attribution.js): source, own or a delegate
    // list. A host that doesn't (Claude and Codex until #84's later slices, an older host on another
    // machine) may send a delegate's words as the top-level response (v1-sessions.md), so they are
    // labelled with the agent's name, never "Main agent": a delegate's text, after reading something
    // untrusted, must not pass as the main agent's.
    public bool Attributed => Voice.Attributed;
    public string Speaker => Attributed ? "Main agent" : AgentName;
    public string Response => Voice.Response ?? "";
    public bool HasResponse => Response.Length > 0;
    public bool ResponseTruncated => Voice.ResponseTruncated;
    // The main agent had a response, but the session budget left it out to make room for its
    // delegates (own.response dropped, own.responseTruncated kept): said so, rather than looking
    // like no answer, and never filled with the delegate's text.
    public bool ResponseDropped => Voice.ResponseDropped;
    // The label line shows for the main agent's words, or for the note that they were left out.
    public bool HasVoice => HasResponse || ResponseDropped;
    // When the main agent last wrote what is shown: own.at for its own text beside a delegate's,
    // which moves the session's updatedAt without touching the main agent's answer; else the
    // session's age, as on its row.
    public string ResponseAgeText => ResponseByDelegate && session.Activity.Own?.At is { } at ? Words.Age(at, ctx.Time.GetUtcNow()) : AgeText;
    public string Reasoning => Voice.Reasoning ?? "";
    public bool HasReasoning => ctx.ShowDetail() && Reasoning.Length > 0;
    public string UserMessage => session.Activity.UserMessage ?? "";
    // The person's own last message, as a bubble; a prompt they didn't type is the Prompt line
    // instead (#489).
    public bool HasUserMessage => !string.IsNullOrEmpty(session.Activity.UserMessage) && Prompt is null;

    // A prompt the person didn't type (#489, ui.md §6.4): never a bubble, but a label line in agent
    // colour at the column's start (a clock for a schedule, the driven diamond otherwise), two
    // lines of it, and Show prompt. What this card saw of the session's prompts folds a schedule's
    // repeat and dates it (PromptMemory).
    readonly PromptMemory prompts = new();
    PromptMemory.Seen promptSeen;
    bool promptOpen;
    public PromptOrigin? Prompt => PromptOrigin.Of(session, promptSeen.Repeat);
    public bool HasPrompt => Prompt is not null;
    public bool PromptIsSchedule => Prompt?.Schedule == true;
    public bool PromptIsDriven => Prompt is { Schedule: false };
    public string PromptLabel => Prompt?.Label ?? "";
    public string PromptBy => Prompt?.By ?? "";
    public bool HasPromptBy => Prompt?.By is not null;
    // A repeat says so instead of a preview: its words are the last's, which the person has seen.
    public string PromptPreview => Prompt is { Repeat: false } p ? p.Preview : "";
    public bool HasPromptPreview => Prompt is { Repeat: false } && !promptOpen;
    public string PromptText => Prompt?.Text ?? "";
    public string PromptAgeText => promptSeen.At is { } at ? Words.Age(at, ctx.Time.GetUtcNow()) : "";
    public bool HasPromptAge => promptSeen.At is not null;
    public string PromptToggleName => Prompt?.DisclosureName(promptOpen) ?? "";
    // Show prompt: a new prompt starts collapsed; an update to the same one keeps it as the person
    // left it.
    public bool PromptOpen
    {
        get => promptOpen;
        set
        {
            if (!Set(ref promptOpen, value)) { return; }
            Raise(nameof(HasPromptPreview));
            Raise(nameof(PromptToggleName));
            Raise(nameof(PromptToggleText));
        }
    }
    public string PromptToggleText => promptOpen ? "Hide prompt" : "Show prompt";
    public bool ShowHistory => ctx.ShowDetail();

    public string Facts
    {
        get
        {
            var facts = new List<string>();
            var b = session.Activity.Background;
            if (b?.Agents > 0) { facts.Add(Words.Plural(b.Agents, "background agent", "background agents")); }
            if (b?.Shells > 0) { facts.Add(Words.Plural(b.Shells, "background command", "background commands")); }
            if (session.Activity.TurnCount is { } t) { facts.Add($"{t} turns"); }
            if (session.Activity.Compactions is > 0 and var c) { facts.Add($"{c} compaction{(c == 1 ? "" : "s")}"); }
            if (session.ProcessId is { } pid) { facts.Add($"pid {pid}"); }
            if (session.Cwd is { Length: > 0 } cwd) { facts.Add(cwd); }
            return string.Join(" · ", facts);
        }
    }

    public bool Folded
    {
        get => folded;
        set
        {
            if (!Set(ref folded, value)) { return; }
            ctx.Folds.SetFolded(session.Id, value);
        }
    }

    public QuestionViewModel? Question { get => question; private set => Set(ref question, value); }
    public ReplyViewModel? Reply { get => reply; private set => Set(ref reply, value); }
    public EndSessionViewModel? End { get => end; private set => Set(ref end, value); }

    // An answer typed for a question that changed or went before it was sent: kept, not lost.
    public string? OrphanText { get => orphanText; private set { if (Set(ref orphanText, value)) { Raise(nameof(HasOrphan)); } } }
    public string? OrphanNote { get => orphanNote; private set => Set(ref orphanNote, value); }
    public bool HasOrphan => orphanText is not null;
    public void DismissOrphan() { OrphanText = null; OrphanNote = null; }

    // What became of an answer whose question has gone from the card.
    public string ReceiptText { get; private set; } = "";
    public Tone ReceiptTone { get; private set; }
    public bool HasReceipt => ReceiptText.Length > 0;

    // Why nothing on this card can be answered, replied to or ended from here, if so.
    public string? ReadOnlyWhy => ctx.ReadOnlyWhy();

    public ObservableCollection<ToolRow> History { get; } = [];
    public string HistoryNote { get => historyNote; private set => Set(ref historyNote, value); }
    public bool HistoryOpen { get; set; }

    // Someone is typing in this card (its reply or answer box has focus): the list holds still.
    public bool Editing { get; set; }

    // Its place in the person's order (#164), set by MainViewModel: "Pinned" or "Sessions" over the
    // first card of each group once anything is pinned; whether it is pinned; and whether its
    // session has news since the card was last open (the "new activity" dot).
    string? groupHeader;
    bool pinned;
    bool unread;
    bool wantsFocus;
    bool wantsRowFocus;
    public string? GroupHeader { get => groupHeader; internal set { if (Set(ref groupHeader, value)) { Raise(nameof(HasGroupHeader)); } } }
    public bool HasGroupHeader => groupHeader is not null;
    public bool Pinned { get => pinned; internal set => Set(ref pinned, value); }
    public bool Unread
    {
        get => unread;
        internal set
        {
            if (!Set(ref unread, value)) { return; }
            Raise(nameof(ShowAge));
            Raise(nameof(RowLabel));
        }
    }
    public RowActions? Row { get; internal set; }
    // Opened to be answered (Needs you): keyboard focus goes into the session, so the next Tab is
    // in its question, not back in the lane. The session pane takes it, and clears it once it has.
    public bool WantsFocus { get => wantsFocus; set => Set(ref wantsFocus, value); }
    // A keyboard or menu move, or a drag: the row's list item takes focus again once it is drawn in
    // its new place, so the next Alt+Up/Down moves the same row. Apart from WantsFocus: below 760 px
    // the list is hidden while a session is open, and focusing a row there went nowhere.
    public bool WantsRowFocus { get => wantsRowFocus; set => Set(ref wantsRowFocus, value); }

    public void Update(Session next)
    {
        var first = !synced;
        synced = true;
        session = next;
        promptSeen = prompts.Observe(next);
        if (promptSeen.Fresh) { promptOpen = false; }
        Sync();
        Delegates.Update(next, ctx.Time.GetUtcNow());
        // A new question opens a folded card: it waits on you, and must not stay folded away.
        if (ctx.Folds.NoteQuestion(next.Id, next.Question?.Id) || first) { Set(ref folded, ctx.Folds.IsFolded(next.Id), nameof(Folded)); }
        RefreshTimes();
        RaiseAll();
        if (HistoryOpen && lastHistoryAt != next.UpdatedAt.ToString("O")) { _ = LoadHistoryAsync(); }
    }

    // The host's capabilities changed (it restarted, or agent-ha-bridge started or stopped).
    public void CapabilitiesChanged()
    {
        Sync();
        Delegates.Update(session, ctx.Time.GetUtcNow());
        RaiseAll();
    }

    // Its machine went away, came back, or its new snapshot confirmed this card (#269): what the
    // row says, and whether its question, reply and End session can be used.
    internal void CurrentChanged()
    {
        question?.Refresh();
        reply?.Refresh();
        end?.Refresh();
        RaiseAll();
    }

    void Sync()
    {
        var caps = ctx.Capabilities();
        // A host away for a moment (restarting, updating) says nothing about what it takes: the
        // card keeps what it had, drafts and all, until the host says otherwise.
        if (caps.Count == 0 && lastCaps.Count > 0) { caps = lastCaps; }
        lastCaps = caps;
        var capsKey = string.Join(",", caps);
        var capsChanged = capsKey != capabilitiesSeen;
        capabilitiesSeen = capsKey;
        var name = ctx.MachineName();

        var q = session.Question;
        QuestionViewModel? rebuilt = null;
        var carried = "";
        // The same question can come back saying something else, or answerable differently: a host
        // updated to refuse what it used to offer (#220). Its controls are rebuilt from what it says
        // now, never kept from what it said.
        var reworded = question is not null && q is not null && q.Id == question.Id && !QuestionViewModel.SaysTheSame(q, question.Question);
        if (question is not null && (q is null || q.Id != question.Id || capsChanged || reworded))
        {
            // The question changed or went: text typed and not sent stays on the card, and an
            // answer that went keeps saying what became of it. The same question rebuilt for new
            // capabilities takes its draft along instead.
            var draft = question.UnsentText();
            if (q is not null && q.Id == question.Id) { rebuilt = question; carried = draft; }
            else if (draft.Length > 0)
            {
                OrphanText = draft;
                OrphanNote = "The question changed before you sent your answer. It was answered at the machine, or the agent moved on. What you typed is kept here.";
            }
            else if (ctx.Deliveries.Get(question.Key) is not null) { receiptKey = question.Key; }
            question.Detach();
            Question = null;
        }
        if (q is not null && question is null)
        {
            var next = new QuestionViewModel(q, session.Id, session.Agent, caps, ctx.Deliveries, name, ctx.Current);
            // Only the same question rebuilt for new capabilities takes its draft along. One that came
            // back saying something else takes nothing chosen for what it said: an option kept by its
            // value could now be "Delete files" where it was "Keep files". What was typed stays on the
            // card instead, and an answer that failed to go is not sent again (Reworded).
            if (rebuilt is not null && !reworded) { next.TakeDraft(rebuilt); }
            if (reworded)
            {
                next.Reworded();
                if (carried.Length > 0)
                {
                    OrphanText = carried;
                    OrphanNote = "The question changed while you were answering it. What you typed is kept here.";
                }
            }
            Question = next;
        }
        if (q is not null && receiptKey is not null && ctx.Deliveries.Get(receiptKey) is { State: DeliveryState.Done }) { receiptKey = null; }
        UpdateReceipt();

        var replies = caps.Contains(Capabilities.SessionsReply);
        var files = caps.Contains(Capabilities.SessionsAttach);
        var large = files && caps.Contains(Capabilities.SessionsAttachDirect);
        var blobMax = files && ctx.BlobsFor is not null ? LargeAttachments.Max(caps, ctx.Blobs) : 0;
        if (replies && reply is null)
        {
            Reply = new ReplyViewModel(session, files, ctx.Deliveries, ctx.Host, ctx.Shrinker, name, large, ctx.Current) { Blobs = ctx.Blobs, BlobsFor = ctx.BlobsFor };
            if (parkedReply is not null) { reply!.TakeDraft(parkedReply); parkedReply = null; }
        }
        else if (!replies && reply is not null)
        {
            // Kept aside, not dropped: replies may come back (agent-ha-bridge stopped, say).
            reply.Detach();
            parkedReply = reply;
            Reply = null;
        }
        reply?.Update(session, files, large, blobMax);

        var stops = caps.Contains(Capabilities.SessionsStop);
        if (stops && end is null) { End = new EndSessionViewModel(session, ctx.Deliveries, name, ctx.Current); }
        else if (!stops && end is not null) { end.Dispose(); End = null; }
        end?.Update(session);
    }

    void UpdateReceipt()
    {
        var e = receiptKey is null ? null : ctx.Deliveries.Get(receiptKey);
        if (e is null) { (ReceiptText, ReceiptTone) = ("", Tone.None); }
        else
        {
            var (text, tone) = QuestionViewModel.Describe(e, ctx.MachineName() is { Length: > 0 } m ? $" on {m}" : "");
            (ReceiptText, ReceiptTone) = ($"Your answer: {text}", tone);
        }
        Raise(nameof(ReceiptText));
        Raise(nameof(ReceiptTone));
        Raise(nameof(HasReceipt));
    }

    void OnDelivery(Delivery e)
    {
        if (e.Key == receiptKey) { UpdateReceipt(); }
    }

    public void RefreshTimes()
    {
        var now = ctx.Time.GetUtcNow();
        // Idle needs no clock; delegating does, since it is waiting on something.
        SinceText = View == "idle" ? "" : $"for {Words.Duration(session.StatusSince, now)}";
        UpdatedText = $"updated {Words.Ago(session.UpdatedAt, now)}";
        AgeText = Words.Age(session.UpdatedAt, now);
        Raise(nameof(SinceText));
        Raise(nameof(UpdatedText));
        Raise(nameof(AgeText));
        // The row's name says its age (ui.md §9.1 A1). The row sets it only when its words change,
        // once a minute at most, and it is no live region: Narrator reads it when the row is reached.
        Raise(nameof(RowLabel));
        Raise(nameof(ResponseAgeText));
        Raise(nameof(PromptAgeText));
        // "last seen 2m ago" moves on with the clock too.
        if (Away) { RaiseStatus(); }
        SyncDetails();
        Delegates.RefreshTimes(now);
    }

    static readonly string[] StatusProperties = [nameof(Line), nameof(State), nameof(View), nameof(StatusText), nameof(Glyph), nameof(StatusTone), nameof(GlyphLabel), nameof(RowStatus), nameof(RowFacts), nameof(RowLabel)];

    void RaiseStatus()
    {
        foreach (var p in StatusProperties) { Raise(p); }
    }

    // The tool history, from session.get, newest first.
    public async Task LoadHistoryAsync()
    {
        if (historyLoading || ctx.FetchDetail is null) { return; }
        historyLoading = true;
        if (History.Count == 0) { HistoryNote = "Loading…"; }
        try
        {
            var full = await ctx.FetchDetail(session.Id);
            lastHistoryAt = session.UpdatedAt.ToString("O");
            History.Clear();
            if (full is null) { HistoryNote = "This session has ended."; return; }
            var now = ctx.Time.GetUtcNow();
            foreach (var t in (full.Activity.History ?? []).TakeLast(50).Reverse())
            {
                History.Add(new ToolRow(ToolGlyphOf(t.Status), t.Status.ToString().ToLowerInvariant(), t.Name, t.Summary, Words.Ago(t.StartedAt, now)));
            }
            HistoryNote = History.Count == 0 ? "No tool calls yet." : "";
        }
        catch (Exception e) when (e is IOException or HostErrorException or TimeoutException)
        {
            HistoryNote = $"Couldn't load the history: {e.Message}";
        }
        finally { historyLoading = false; }
    }

    public void Detach()
    {
        ctx.Deliveries.Changed -= OnDelivery;
        question?.Detach();
        reply?.Detach();
        end?.Dispose();
    }
}
