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
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

public sealed record ToolRow(StatusGlyph Glyph, string Status, string Name, string? Summary, string When);

// One session's card (pwa/js/ui/session-card.js, which follows agent-ha-bridge's card): its
// status as a glyph and words (docs/design/ui.md §2.2, StatusRules), a header that stays when
// folded, and below it the question, the response, reasoning, the last message, the reply box,
// the tool history, the facts and End session.
public sealed class SessionCardViewModel : Observable
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
        ctx.Deliveries.Changed += OnDelivery;
        Update(session);
    }

    public Session Session => session;
    public string Id => session.Id;
    public AgentKind Agent => session.Agent;
    public string AgentName => AgentNames.GetValueOrDefault(session.Agent, "Agent");
    public string Title => string.IsNullOrEmpty(session.Title) ? "Untitled session" : session.Title;
    public string Meta => string.Join(" · ", new[] { ctx.ShowMachine() ? ctx.MachineName() : null, AgentName, session.Repository ?? Words.LastSegment(session.Cwd), session.Branch, session.Model }.Where(s => !string.IsNullOrEmpty(s)));

    // What the card says about the session: ui.md §2.2's glyph and words (StatusRules).
    public StatusLine Line => StatusRules.For(session);
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
    public bool Driven => session.Driver == DriverKind.Agent;
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
    static StatusGlyph ToolGlyphOf(ToolStatus s) => s switch { ToolStatus.Running => StatusGlyph.Working, ToolStatus.Failed => StatusGlyph.Failed, _ => StatusGlyph.Done };

    public string Response => session.Activity.Response ?? "";
    public bool HasResponse => !string.IsNullOrEmpty(session.Activity.Response);
    public bool ResponseTruncated => session.Activity.ResponseTruncated == true && HasResponse;
    public string Reasoning => session.Activity.Reasoning ?? "";
    public bool HasReasoning => ctx.ShowDetail() && !string.IsNullOrEmpty(session.Activity.Reasoning);
    public string UserMessage => session.Activity.UserMessage ?? "";
    public bool HasUserMessage => !string.IsNullOrEmpty(session.Activity.UserMessage);
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
    public string? GroupHeader { get => groupHeader; internal set { if (Set(ref groupHeader, value)) { Raise(nameof(HasGroupHeader)); } } }
    public bool HasGroupHeader => groupHeader is not null;
    public bool Pinned { get => pinned; internal set => Set(ref pinned, value); }
    public bool Unread { get => unread; internal set => Set(ref unread, value); }
    public RowActions? Row { get; internal set; }
    // A keyboard move: the card's title takes focus again once it is drawn in its new place.
    public bool WantsFocus { get => wantsFocus; set => Set(ref wantsFocus, value); }

    public void Update(Session next)
    {
        var first = !synced;
        synced = true;
        session = next;
        Sync();
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
        if (question is not null && (q is null || q.Id != question.Id || capsChanged))
        {
            // The question changed or went: text typed and not sent stays on the card, and an
            // answer that went keeps saying what became of it. The same question rebuilt for new
            // capabilities takes its draft along instead.
            var draft = question.UnsentText();
            if (q is not null && q.Id == question.Id) { rebuilt = question; }
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
            var next = new QuestionViewModel(q, session.Id, session.Agent, caps, ctx.Deliveries, name);
            if (rebuilt is not null) { next.TakeDraft(rebuilt); }
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
            Reply = new ReplyViewModel(session, files, ctx.Deliveries, ctx.Host, ctx.Shrinker, name, large) { Blobs = ctx.Blobs, BlobsFor = ctx.BlobsFor };
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
        if (stops && end is null) { End = new EndSessionViewModel(session, ctx.Deliveries, name); }
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
        Raise(nameof(SinceText));
        Raise(nameof(UpdatedText));
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
