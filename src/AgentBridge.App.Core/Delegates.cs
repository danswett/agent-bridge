using System.Collections.ObjectModel;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// One delegate as the strip, the Delegates tab and the inspector's nested list show it (ui.md
// §6.3), or, in the strip, the "N finished" item its finished delegates fold into. Kept and
// changed in place as the session changes, so a row keeps its place, focus and selection.
public sealed class DelegateItem : Observable
{
    string name = "";
    StatusGlyph glyph = StatusGlyph.Idle;
    string words = "";
    string time = "";
    string line = "";
    int depth;
    int nested;
    string? parentName;
    bool selected;
    bool open;

    DelegateItem(string id, bool fold)
    {
        Id = id;
        IsFold = fold;
    }

    internal static DelegateItem For(string id) => new(id, false);
    internal static DelegateItem Fold() => new("finished", true) { glyph = StatusGlyph.Done };

    public string Id { get; }
    // The "N finished" item: pressed, it shows or hides the finished delegates.
    public bool IsFold { get; }
    public bool IsDelegate => !IsFold;

    public string Name { get => name; internal set { if (Set(ref name, value)) { Changed(); } } }
    public StatusGlyph Glyph { get => glyph; internal set => Set(ref glyph, value); }
    public string Words { get => words; internal set { if (Set(ref words, value)) { Changed(); } } }
    public string Time { get => time; internal set { if (Set(ref time, value)) { Changed(); } } }
    // Its latest response or reasoning, its own words on its own row (ui.md §1 principle 3).
    public string Line { get => line; internal set { if (Set(ref line, value)) { Raise(nameof(HasLine)); } } }
    public bool HasLine => line.Length > 0;
    public int Depth { get => depth; internal set { if (Set(ref depth, value)) { Raise(nameof(Indent)); Changed(); } } }
    // The indent stops at three levels; deeper ones say whose they are in words (UnderText).
    public double Indent => Math.Min(depth, 3) * 16;
    // Delegates it started, at any depth: the strip's "N nested".
    public int Nested { get => nested; internal set { if (Set(ref nested, value)) { Changed(); } } }
    public string? ParentName { get => parentName; internal set { if (Set(ref parentName, value)) { Changed(); } } }
    public bool Selected { get => selected; internal set { if (Set(ref selected, value)) { Raise(nameof(NotSelected)); Changed(); } } }
    public bool NotSelected => !selected;
    // The fold: whether its finished delegates are shown.
    public bool Open { get => open; internal set { if (Set(ref open, value)) { Changed(); } } }

    public string NestedText => nested > 0 ? $"{nested} nested" : "";
    // The strip item's second line (§6.3): the elapsed time or when it finished, and how many it
    // started.
    public string Facts => string.Join(" · ", new[] { time, NestedText }.Where(s => s.Length > 0));
    // Who started it, shown where the indent can't say it: a parent in the other group, or deeper
    // than the indent goes.
    public string UnderText => parentName is not null && (depth == 0 || depth > 3) ? $"under {parentName}" : "";
    // Read aloud (ui.md §9): what the glyph, the indent and the facts show.
    public string Label => IsFold
        ? $"{name}, {(open ? "shown" : "hidden")}"
        : string.Join(", ", new[] { name, words.ToLowerInvariant(), time, NestedText, parentName is null ? null : $"under {parentName}", selected ? "selected" : null }.Where(s => !string.IsNullOrEmpty(s)));

    void Changed()
    {
        Raise(nameof(Facts));
        Raise(nameof(NestedText));
        Raise(nameof(UnderText));
        Raise(nameof(Label));
    }

    // What a list item is called when nothing else names it: UI Automation reads a ListViewItem's
    // item as text.
    public override string ToString() => Label;
}

// A delegate in the conversation (ui.md §6.4): one meta line, "Started 3 delegates · 26m" or
// "Delegate *name* reported back · 22m", the name linking to the delegate and the word Delegate
// before it, so no name reads as the main agent's (DelegateLabel). Only the main agent's own
// delegates, which report to it; a nested one reports to its parent, in the inspector. Drawn as
// its words before the name, the name, and its words after. Kept per key and changed in place, as
// its age moves on each minute: a replaced item would take a focused link with it.
public sealed class DelegateEvent : Observable
{
    StatusGlyph? glyph;
    string before = "";
    string? name;
    string? agentId;
    string after = "";

    internal DelegateEvent(string key) { Key = key; }

    public string Key { get; }
    public StatusGlyph? Glyph { get => glyph; internal set { if (Set(ref glyph, value)) { Raise(nameof(Started)); Raise(nameof(HasGlyph)); Raise(nameof(GlyphShown)); } } }
    public string Before { get => before; internal set { if (Set(ref before, value)) { Raise(nameof(Text)); } } }
    public string? Name { get => name; internal set { if (Set(ref name, value)) { Raise(nameof(HasName)); Raise(nameof(Text)); } } }
    public string? AgentId { get => agentId; internal set => Set(ref agentId, value); }
    public string After { get => after; internal set { if (Set(ref after, value)) { Raise(nameof(Text)); } } }
    public DateTimeOffset At { get; internal set; }

    public bool Started => Glyph is null;
    public bool HasName => Name is not null;
    public bool HasGlyph => Glyph is not null;
    public StatusGlyph GlyphShown => Glyph ?? StatusGlyph.Idle;
    public string Text => string.Join(" ", new[] { Before, Name, After }.Where(s => !string.IsNullOrEmpty(s)));

    public override string ToString() => Text;
}

// The delegate the inspector shows (§6.3), or the Delegates tab's pushed view: its name and glyph;
// Status, Model, Kind and ID; its Task, its Latest words and its Reasoning; its Tools; and the
// delegates it started, nested under it. Its own words come from session.get { id, agent }; until
// they arrive, the list's latest line.
public sealed class DelegateDetail : Observable
{
    string name = "";
    StatusGlyph glyph = StatusGlyph.Idle;
    string task = "";
    string latest = "";
    bool latestTruncated;
    string reasoning = "";
    string note = "";
    bool canAsk;

    public string Name { get => name; internal set { if (Set(ref name, value)) { Raise(nameof(Heading)); } } }
    // The heading read aloud: the name under the inspector's fixed "Delegate" overline, which a
    // name can't stand in for (DelegateLabel).
    public string Heading => DelegateLabel.Titled(name);
    public StatusGlyph Glyph { get => glyph; internal set => Set(ref glyph, value); }
    public string GlyphLabel => Properties.FirstOrDefault(p => p.Label == "Status")?.Value ?? "";
    public ObservableCollection<DetailRow> Properties { get; } = [];
    public string Task { get => task; internal set { if (Set(ref task, value)) { Raise(nameof(HasTask)); } } }
    public bool HasTask => task.Length > 0;
    public string Latest { get => latest; internal set { if (Set(ref latest, value)) { Raise(nameof(HasLatest)); } } }
    public bool HasLatest => latest.Length > 0;
    public bool LatestTruncated { get => latestTruncated; internal set => Set(ref latestTruncated, value); }
    public string Reasoning { get => reasoning; internal set { if (Set(ref reasoning, value)) { Raise(nameof(HasReasoning)); } } }
    public bool HasReasoning => reasoning.Length > 0;
    public ObservableCollection<ToolRow> Tools { get; } = [];
    public bool HasTools => Tools.Count > 0;
    // The delegates it started, at any depth, each indented under its own parent.
    public ObservableCollection<NestedDelegate> Nested { get; } = [];
    public bool HasNested => Nested.Count > 0;
    // Loading, a fetch that failed, or a delegate the machine no longer has.
    public string Note { get => note; internal set => Set(ref note, value); }
    // "Ask main agent to stop it" (§6.3, subagents.md §6): offered while it can still run, where the
    // session takes replies.
    public bool CanAskToStop { get => canAsk; internal set => Set(ref canAsk, value); }

    internal void Raised()
    {
        Raise(nameof(GlyphLabel));
        Raise(nameof(HasTools));
        Raise(nameof(HasNested));
    }
}

// A delegate in another's inspector, indented under its own parent there.
public sealed record NestedDelegate(DelegateItem Item, double Indent)
{
    public override string ToString() => Item.Label;
}

// A session's delegates (ui.md §6.3; docs/design/subagents.md §6): the strip at three panes, the
// Delegates tab's list below that, the delegate selected in either and its detail, and the
// conversation's one-line events. Which delegates, in what order and with which glyph is
// Attribution's, the PWA's rules. Replies only ever go to the main agent: nothing here types to a
// delegate, and its one action pre-fills the reply box and sends nothing.
public sealed class DelegatesViewModel : Observable
{
    // A running delegate's words are fetched again this often while shown: a host sends no
    // update when only its line changes (v1-sessions.md, Subagent). And at most this often as its
    // entry changes.
    static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);
    static readonly TimeSpan OnChange = TimeSpan.FromSeconds(2);
    // Starts this close together are one "Started N delegates" line.
    static readonly TimeSpan Together = TimeSpan.FromSeconds(60);

    readonly CardContext ctx;
    readonly Func<ReplyViewModel?> reply;
    readonly Dictionary<string, DelegateItem> items = new(StringComparer.Ordinal);
    readonly Dictionary<string, DelegateEvent> events = new(StringComparer.Ordinal);
    readonly DelegateItem fold = DelegateItem.Fold();
    Session session;
    DelegateGroups? groups;
    bool finishedOpen;
    string? selectedId;
    bool detailOpen;
    // The selected delegate's own words, as last fetched, and what its list entry was then.
    Subagent? fetched;
    // Its task and latest line from the list or a fetch, whichever came last: a list the session
    // budget trimmed leaves them out, and that is not the delegate saying nothing.
    string keptTask = "";
    string keptLine = "";
    string? listLine;
    int lineRev;
    string? listTask;
    int taskRev;
    string? entryKey;
    int generation;
    bool loading;
    bool again;
    bool due;
    DateTimeOffset lastLoad = DateTimeOffset.MinValue;

    internal DelegatesViewModel(Session session, CardContext ctx, Func<ReplyViewModel?> reply)
    {
        this.session = session;
        this.ctx = ctx;
        this.reply = reply;
    }

    // At three panes, under the session header: one item per delegate the main agent started,
    // then "N finished", then the finished ones while it is open.
    public ObservableCollection<DelegateItem> Strip { get; } = [];
    // The Delegates tab: every running and idle delegate, nested under its parent; the finished
    // ones folded under "N finished".
    public ObservableCollection<DelegateItem> Active { get; } = [];
    public ObservableCollection<DelegateItem> Finished { get; } = [];
    public ObservableCollection<DelegateEvent> Events { get; } = [];
    public DelegateDetail Detail { get; } = new();

    // The host lists its delegates: Copilot today. Otherwise (Claude, Codex, an older host) there
    // is no strip or list, only the counts (status rule 8).
    public bool Listed => groups is not null;
    public bool HasActive => Active.Count > 0;
    public bool HasFinished => Finished.Count > 0;
    public string FinishedText => $"{Finished.Count} finished";
    public int More => groups?.More ?? 0;
    public bool HasMore => More > 0;
    public string MoreText => More > 0 ? $"{Words.Plural(More, "more delegate", "more delegates")} not listed" : "";
    public bool HasEvents => Events.Count > 0;

    public bool FinishedOpen
    {
        get => finishedOpen;
        set
        {
            if (!Set(ref finishedOpen, value)) { return; }
            fold.Open = value;
            SyncStrip();
        }
    }

    public string? SelectedId => selectedId;
    public DelegateItem? Selected => selectedId is null ? null : items.GetValueOrDefault(selectedId);
    public bool HasSelected => selectedId is not null;

    // The detail is on screen (PaneLayoutViewModel): its words are fetched, and kept current,
    // only then.
    public bool DetailOpen
    {
        get => detailOpen;
        set
        {
            if (!Set(ref detailOpen, value)) { return; }
            if (value && selectedId is not null) { _ = LoadAsync(); }
        }
    }

    // The reply box should take focus: "Ask main agent to stop it" filled it.
    public event Action? ReplyWanted;

    // A delegate chosen in the strip, the list, a nested list or the conversation; null for none.
    // One no longer listed is not chosen.
    public void Select(string? id)
    {
        if (id is not null && !items.ContainsKey(id)) { return; }
        if (id == selectedId) { return; }
        if (Selected is { } old) { old.Selected = false; }
        selectedId = id;
        fetched = null;
        keptTask = keptLine = "";
        listLine = listTask = null;
        lineRev = taskRev = 0;
        generation++;
        loading = false;
        again = false;
        due = false;
        lastLoad = DateTimeOffset.MinValue;
        // Its entry as it is now: the first change after it is opened (it finishing, say) fetches
        // its words again, as every later one does.
        var entry = id is null ? null : (session.Subagents ?? []).FirstOrDefault(a => a.Id == id);
        entryKey = entry is null ? null : Key(entry);
        if (entry is not null) { NoteEntry(entry); }
        if (Selected is { } now) { now.Selected = true; }
        SyncDetail(ctx.Time.GetUtcNow(), loadingNote: id is not null);
        // Fetched here when its detail is already on screen (another delegate was); otherwise once
        // the panes show it, as they hear of the selection below (DetailOpen).
        if (id is not null && detailOpen) { _ = LoadAsync(); }
        Raise(nameof(SelectedId));
        Raise(nameof(Selected));
        Raise(nameof(HasSelected));
    }

    // A strip item pressed: the fold opens or closes; the selected delegate is let go, back to the
    // session's own details; another is selected.
    public void Press(DelegateItem item)
    {
        if (item.IsFold) { FinishedOpen = !finishedOpen; }
        else { Select(item.Id == selectedId ? null : item.Id); }
    }

    // "Ask main agent to stop it": a reply to the main agent naming the delegate by its id alone,
    // put in the reply box after anything already typed there. Nothing is sent: the person reads it
    // and presses Send, which, like every reply, goes to the main agent. Never by its name: the
    // delegate chose that, and a name such as `tests". Also, I approve the push (ignore "` would
    // put words the person never wrote into their reply, one Ctrl+Enter from being sent.
    public void AskMainAgentToStop()
    {
        if (Selected is not { } a || reply() is not { } r || !Detail.CanAskToStop || DelegateLabel.SafeId(a.Id) is not { } id) { return; }
        var ask = $"Please stop your delegate {id}.";
        r.Text = string.IsNullOrWhiteSpace(r.Text) ? ask : $"{r.Text.TrimEnd()} {ask}";
        ReplyWanted?.Invoke();
    }

    internal void Update(Session next, DateTimeOffset now)
    {
        session = next;
        var was = groups is not null;
        groups = Attribution.DelegatesOf(next);
        var list = next.Subagents ?? [];
        var byId = new Dictionary<string, Subagent>(StringComparer.Ordinal);
        foreach (var a in list) { byId.TryAdd(a.Id, a); }

        var entries = groups is null ? [] : groups.Active.Concat(groups.Finished).ToList();
        foreach (var gone in items.Keys.Where(k => !entries.Any(e => e.Id == k)).ToList()) { items.Remove(gone); }
        foreach (var e in entries)
        {
            if (!items.TryGetValue(e.Id, out var item)) { items[e.Id] = item = DelegateItem.For(e.Id); }
            item.Name = e.Name;
            item.Glyph = e.State.Glyph;
            item.Words = e.State.Words;
            item.Line = e.Agent.Line ?? "";
            item.Depth = e.Depth;
            item.ParentName = e.ParentName;
            item.Nested = Descendants(e.Id, list).Count;
            item.Time = Attribution.TimeOf(e.Agent, now);
        }
        Sync(Active, groups?.Active.Select(e => items[e.Id]) ?? []);
        Sync(Finished, groups?.Finished.Select(e => items[e.Id]) ?? []);
        fold.Name = FinishedText;
        SyncStrip();

        // The selected delegate left the list: back to the session's own details.
        if (selectedId is not null && !items.ContainsKey(selectedId)) { Select(null); }
        else if (selectedId is not null && byId.GetValueOrDefault(selectedId) is { } entry)
        {
            // Its entry changed (it finished, was woken, asked or said more): its words are fetched
            // again, at most every OnChange.
            var key = Key(entry);
            if (entryKey is not null && key != entryKey) { due = true; }
            entryKey = key;
            NoteEntry(entry);
            SyncDetail(now);
            if (due && detailOpen && now - lastLoad >= OnChange) { _ = LoadAsync(); }
        }
        SyncEvents(list, now);

        if (was != (groups is not null)) { Raise(nameof(Listed)); }
        foreach (var p in new[] { nameof(HasActive), nameof(HasFinished), nameof(FinishedText), nameof(More), nameof(HasMore), nameof(MoreText), nameof(HasEvents) }) { Raise(p); }
    }

    internal void RefreshTimes(DateTimeOffset now)
    {
        foreach (var a in session.Subagents ?? [])
        {
            if (items.TryGetValue(a.Id, out var item)) { item.Time = Attribution.TimeOf(a, now); }
        }
        SyncEvents(session.Subagents ?? [], now);
        if (selectedId is null) { return; }
        SyncDetail(now);
        if (!detailOpen || loading) { return; }
        var running = (session.Subagents ?? []).FirstOrDefault(a => a.Id == selectedId) is { Status: SubagentStatus.Working or SubagentStatus.Waiting };
        if ((due && now - lastLoad >= OnChange) || (running && now - lastLoad >= Poll)) { _ = LoadAsync(); }
    }

    // A tool starting or finishing is a change too: the detail's Tools list is fetched, not listed.
    // So is a new model: the inspector shows the fetched one, and an idle delegate is not polled.
    static string Key(Subagent a) => $"{a.Status}|{a.Outcome}|{a.Line}|{a.EndedAt:O}|{a.Model}|{a.CurrentTool?.Id}|{a.CurrentTool?.Status}";

    // The selected delegate's task and latest line as its list entry has them. A new line from the
    // list moves lineRev on, so a fetch sent before it can't put back an older one; one the same
    // as before leaves a newer fetched line be, since a line changing alone sends no update
    // (v1-sessions.md, Subagent). A list the budget trimmed keeps what was there.
    void NoteEntry(Subagent entry)
    {
        if (entry.Line is { } line && line != listLine)
        {
            keptLine = line;
            lineRev++;
        }
        listLine = entry.Line;
        // Its task the same way: one the list brought while a fetch was out is newer than the
        // fetch's, which was read before it.
        if (entry.Task is { } task && task != listTask)
        {
            keptTask = task;
            taskRev++;
        }
        listTask = entry.Task;
    }

    // Its descendants in the session's list, in list order, each with its depth below it. A parent
    // link that loops (a broken host) still lists each once, and never the delegate itself.
    static List<(Subagent Agent, int Depth)> Descendants(string id, IReadOnlyList<Subagent> list)
    {
        var output = new List<(Subagent, int)>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { id };
        void Visit(string parent, int depth)
        {
            foreach (var a in list.Where(a => a.Parent == parent && a.Id != parent))
            {
                if (!seen.Add(a.Id)) { continue; }
                output.Add((a, depth));
                Visit(a.Id, depth + 1);
            }
        }
        Visit(id, 0);
        return output;
    }

    void SyncStrip()
    {
        var want = new List<DelegateItem>();
        if (groups is not null)
        {
            want.AddRange(groups.Active.Where(e => e.Depth == 0).Select(e => items[e.Id]));
            if (groups.Finished.Count > 0)
            {
                want.Add(fold);
                if (finishedOpen) { want.AddRange(groups.Finished.Where(e => e.Depth == 0).Select(e => items[e.Id])); }
            }
        }
        Sync(Strip, want);
    }

    // Changed a move at a time, never rebuilt: a rebuilt list loses keyboard focus and the item
    // under the pointer.
    static void Sync<T>(ObservableCollection<T> target, IEnumerable<T> wanted) where T : class
    {
        var want = wanted.ToList();
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!want.Contains(target[i])) { target.RemoveAt(i); }
        }
        for (var i = 0; i < want.Count; i++)
        {
            var at = target.IndexOf(want[i]);
            if (at == i) { continue; }
            if (at >= 0) { target.Move(at, i); }
            else { target.Insert(i, want[i]); }
        }
    }

    // The conversation's events: the main agent's own delegates starting (together, as one line)
    // and reporting back, oldest first. Nested ones report to their parent, not the main agent.
    void SyncEvents(IReadOnlyList<Subagent> list, DateTimeOffset now)
    {
        var mine = list.Where(a => a.Parent is null).ToList();
        var wanted = new List<DelegateEvent>();
        void Event(string key, StatusGlyph? glyph, string before, string? name, string? agentId, string after, DateTimeOffset at)
        {
            // A host that lists one id twice (a broken one) gets one line for it, not two.
            if (wanted.Any(w => w.Key == key)) { return; }
            if (!events.TryGetValue(key, out var e)) { events[key] = e = new DelegateEvent(key); }
            e.Glyph = glyph;
            e.Before = before;
            e.Name = name;
            e.AgentId = agentId;
            e.After = after;
            e.At = at;
            wanted.Add(e);
        }
        var starts = mine.Where(a => a.StartedAt is not null).OrderBy(a => a.StartedAt).ToList();
        for (var i = 0; i < starts.Count;)
        {
            var batch = new List<Subagent> { starts[i] };
            while (++i < starts.Count && starts[i].StartedAt!.Value - batch[^1].StartedAt!.Value <= Together) { batch.Add(starts[i]); }
            var at = batch[0].StartedAt!.Value;
            var age = $"· {Words.Age(at, now)}";
            var name = NameOf(batch[0]);
            if (batch.Count == 1) { Event($"start|{batch[0].Id}", null, DelegateLabel.SaysDelegate(name) ? "Started" : "Started delegate", name, batch[0].Id, age, at); }
            else { Event($"start|{batch[0].Id}", null, $"Started {batch.Count} delegates {age}", null, null, "", at); }
        }
        foreach (var a in mine.Where(a => a.EndedAt is not null && a.Status is SubagentStatus.Idle or SubagentStatus.Ended))
        {
            var (glyph, said) = (a.Status, a.Outcome) switch
            {
                (_, SubagentOutcome.Failed) => (StatusGlyph.Failed, "failed"),
                (_, SubagentOutcome.Done) => (StatusGlyph.Done, "reported back"),
                (SubagentStatus.Ended, _) => (StatusGlyph.Ended, "ended"),
                _ => (StatusGlyph.Idle, "reported back"),
            };
            var name = NameOf(a);
            Event($"end|{a.Id}", glyph, DelegateLabel.SaysDelegate(name) ? "" : "Delegate", name, a.Id, $"{said} · {Words.Age(a.EndedAt!.Value, now)}", a.EndedAt!.Value);
        }
        foreach (var gone in events.Keys.Where(k => !wanted.Any(e => e.Key == k)).ToList()) { events.Remove(gone); }
        // Stable, so events at the same moment keep the order they were built in.
        Sync(Events, wanted.OrderBy(e => e.At));
        Raise(nameof(HasEvents));
    }

    static string NameOf(Subagent a) => DelegateLabel.For(a.Name, a.Id);

    // The detail from the selected delegate's list entry, and its own words once fetched.
    void SyncDetail(DateTimeOffset now, bool loadingNote = false)
    {
        var d = Detail;
        var entry = selectedId is null ? null : (session.Subagents ?? []).FirstOrDefault(a => a.Id == selectedId);
        if (entry is null)
        {
            d.Name = "";
            d.Properties.Clear();
            d.Task = d.Latest = d.Reasoning = d.Note = "";
            d.LatestTruncated = false;
            d.Tools.Clear();
            d.Nested.Clear();
            d.CanAskToStop = false;
            d.Raised();
            return;
        }
        var state = Attribution.StateOf(entry);
        var time = Attribution.TimeOf(entry, now);
        d.Name = NameOf(entry);
        d.Glyph = state.Glyph;
        var rows = new List<DetailRow> { new("Status", time.Length > 0 ? $"{state.Words} · {time}" : state.Words) };
        if ((fetched?.Model ?? entry.Model) is { Length: > 0 } model) { rows.Add(new("Model", model)); }
        rows.Add(new("Kind", entry.Kind == SubagentKind.Background ? "Background" : "Task"));
        rows.Add(new("ID", entry.Id, Mono: true));
        SyncRows(d.Properties, rows);
        d.Task = keptTask;
        var response = fetched?.Response;
        d.Latest = !string.IsNullOrEmpty(response) ? response : keptLine;
        d.LatestTruncated = !string.IsNullOrEmpty(response) && fetched?.ResponseTruncated == true;
        // "Show reasoning and tool history on cards" covers a delegate's as well as the session's.
        var showDetail = ctx.ShowDetail();
        d.Reasoning = showDetail ? fetched?.Reasoning ?? "" : "";
        var tools = !showDetail ? [] : fetched?.History is { Count: > 0 } history ? history.Reverse() : entry.CurrentTool is { } current ? [current] : [];
        SyncRows(d.Tools, [.. tools.Take(50).Select(t => new ToolRow(SessionCardViewModel.ToolGlyphOf(t.Status), t.Status.ToString().ToLowerInvariant(), t.Name, t.Summary, Words.Clock((t.EndedAt ?? now) - t.StartedAt)))]);
        var nested = Descendants(entry.Id, session.Subagents ?? []);
        var want = nested.Where(n => items.ContainsKey(n.Agent.Id)).Select(n => new NestedDelegate(items[n.Agent.Id], Math.Min(n.Depth, 3) * 16)).ToList();
        if (!d.Nested.SequenceEqual(want))
        {
            d.Nested.Clear();
            foreach (var n in want) { d.Nested.Add(n); }
        }
        d.CanAskToStop = entry.Status != SubagentStatus.Ended && reply() is not null && DelegateLabel.SafeId(entry.Id) is not null;
        if (loadingNote) { d.Note = ctx.FetchDelegate is null ? "" : "Loading…"; }
        d.Raised();
    }

    static void SyncRows<T>(ObservableCollection<T> target, IReadOnlyList<T> want)
    {
        for (var i = 0; i < want.Count; i++)
        {
            if (i < target.Count && EqualityComparer<T>.Default.Equals(target[i], want[i])) { continue; }
            if (i < target.Count) { target[i] = want[i]; }
            else { target.Add(want[i]); }
        }
        while (target.Count > want.Count) { target.RemoveAt(target.Count - 1); }
    }

    // session.get { id, agent }: the selected delegate in full. One at a time; one asked for
    // meanwhile goes once it is back. A result for a delegate no longer selected is dropped.
    async Task LoadAsync()
    {
        if (ctx.FetchDelegate is not { } fetch || selectedId is not { } agent) { return; }
        if (loading) { again = true; return; }
        loading = true;
        due = false;
        lastLoad = ctx.Time.GetUtcNow();
        var mine = generation;
        var (rev, revTask) = (lineRev, taskRev);
        Subagent? full = null;
        string? problem = null;
        try { full = await fetch(session.Id, agent); }
        catch (Exception e) when (e is IOException or HostErrorException or TimeoutException or System.Text.Json.JsonException or OperationCanceledException or ObjectDisposedException or InvalidOperationException) { problem = e.Message; }
        Post(() =>
        {
            if (mine != generation) { return; }
            loading = false;
            // A failed load is tried again, at most every OnChange, while the detail is open: an
            // idle or ended delegate is not polled, so its words would otherwise stay missing.
            if (problem is not null)
            {
                Detail.Note = $"Couldn't load the delegate: {problem}";
                due = true;
            }
            else if (full is null) { Detail.Note = "This delegate is no longer listed."; }
            else
            {
                fetched = full;
                if (full.Line is { } line && rev == lineRev) { keptLine = line; }
                if (full.Task is { } task && revTask == taskRev) { keptTask = task; }
                Detail.Note = "";
            }
            SyncDetail(ctx.Time.GetUtcNow());
            if (again && detailOpen)
            {
                again = false;
                _ = LoadAsync();
            }
        });
    }

    void Post(Action action)
    {
        if (ctx.Ui is { } ui) { ui.Post(action); }
        else { action(); }
    }
}
