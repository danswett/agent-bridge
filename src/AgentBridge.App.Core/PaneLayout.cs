using System.ComponentModel;

namespace AgentBridge.App.Core;

// The Sessions page's panes by window width (ui.md §5.2, #180), at design/tokens.json's
// breakpoints, the PWA's (DesignLayout):
//   Three (≥ 1280): list · session · inspector, the inspector hideable: the delegate selected in
//     the session's strip, or with none the session's Details and Tools;
//   Two (760–1279): list · session, with Conversation, Delegates and Tools tabs;
//   One (< 760): the list, or the session with a way back to the list.
// The session shown is MainViewModel.Selected, so a resize changes only how it is shown.
public enum PaneMode { One, Two, Three }

public enum SessionTab { Conversation, Delegates, Tools }

// What the panes did that motion shows (ui.md §5.3): a push or a pop in one column, the inspector
// shown by its toggle, a tab chosen, or a question arriving on the session shown, which settles
// anything still travelling at once (an approval's command never slides). Never a resize across
// the breakpoints, a first draw, or a tab the app changed: those show at once.
public enum PaneMotion { Push, Pop, Inspector, Tab, Settle }

// Travels: whether the content slides as it fades. A session asking a question only fades in, so
// its command never moves under the person's pointer.
public readonly record struct PaneMove(PaneMotion Kind, bool Travels);

public sealed class PaneLayoutViewModel : Observable
{
    readonly MainViewModel main;
    PaneMode mode = PaneMode.Three;
    bool inspectorWanted = true;
    SessionTab tab;
    bool onScreen = true;
    SessionCardViewModel? watched;
    // The card whose tool history is open, loaded and kept current while on screen.
    SessionCardViewModel? history;
    // The card whose selected delegate is on screen, its words fetched.
    SessionCardViewModel? delegateShown;

    public PaneLayoutViewModel(MainViewModel main)
    {
        this.main = main;
        main.PropertyChanged += OnMain;
        main.OpenRequested += OnOpened;
        Watch(main.Selected);
        SyncHistory();
        SyncDelegate();
    }

    public static PaneMode ModeFor(double width) =>
        width >= DesignLayout.ThreePane ? PaneMode.Three : width >= DesignLayout.TwoPane ? PaneMode.Two : PaneMode.One;

    // The window's width in effective pixels, as the panes' visual states read it.
    public double Width
    {
        set
        {
            if (!double.IsFinite(value) || value <= 0) { return; }
            if (Set(ref mode, ModeFor(value), nameof(Mode))) { Changed(); }
        }
    }

    public PaneMode Mode => mode;
    public SessionCardViewModel? Selected => main.Selected;
    bool Has => main.Selected is not null;

    public bool ShowList => mode != PaneMode.One || !Has;
    public bool ShowSession => mode != PaneMode.One || Has;
    // Back to the list, in one column.
    public bool ShowBack => mode == PaneMode.One && Has;
    public bool ShowEmpty => mode != PaneMode.One && !Has;

    // The inspector: three panes only, hidden by the person or with nothing selected.
    public bool CanToggleInspector => mode == PaneMode.Three && Has;
    public bool InspectorWanted
    {
        get => inspectorWanted;
        set { if (Set(ref inspectorWanted, value)) { Changed(); } }
    }
    public bool ShowInspector => mode == PaneMode.Three && inspectorWanted && Has;
    public void ToggleInspector()
    {
        InspectorWanted = !inspectorWanted;
        // Shown, it slides in from the trailing edge; hidden, its column closes at once.
        if (ShowInspector) { Moved?.Invoke(new PaneMove(PaneMotion.Inspector, true)); }
    }
    public string InspectorToggleName => ShowInspector ? "Hide the inspector" : "Show the inspector";

    // The tabs: two panes and one column, where there is no inspector. Delegates while the host
    // lists some or reports counts.
    public bool ShowTabs => mode != PaneMode.Three && Has;
    public bool ShowDelegatesTab => main.Selected is { } c && (c.HasDelegates || c.Delegates.Listed);
    // Tools only where the machine can send a session's history (CardContext.ShowDetail).
    public bool ShowToolsTab => main.Selected?.ShowHistory == true;
    // Set by the person choosing a tab: the new content fades in.
    public SessionTab Tab
    {
        get => tab;
        set
        {
            var shown = Shown;
            if (!Set(ref tab, value)) { return; }
            Changed();
            if (Shown != shown) { Moved?.Invoke(new PaneMove(PaneMotion.Tab, false)); }
        }
    }
    // The tab in force: Delegates falls back to Conversation once the session has none.
    SessionTab Shown => !ShowTabs ? SessionTab.Conversation
        : tab == SessionTab.Delegates && !ShowDelegatesTab ? SessionTab.Conversation
        : tab == SessionTab.Tools && !ShowToolsTab ? SessionTab.Conversation
        : tab;
    public bool ShowConversation => Has && Shown == SessionTab.Conversation;
    public bool ShowDelegates => Has && Shown == SessionTab.Delegates;
    public bool ShowTools => Has && Shown == SessionTab.Tools;
    // The tool history is wanted where it is on screen: the inspector's session details or the
    // Tools tab.
    public bool WantsTools => onScreen && (ShowSessionInspector || ShowTools) && ShowToolsTab;

    // Delegates (ui.md §6.3). At three panes, the strip under the header, and the inspector shows
    // the delegate selected there, or the session's Details and Tools with none. Below that, the
    // Delegates tab: the list, and a delegate opened from it pushed in its place. A host that lists
    // none (status rule 8) has no strip or list, only its counts in the tab.
    bool Listed => main.Selected?.Delegates.Listed == true;
    bool Chosen => main.Selected?.Delegates.HasSelected == true;
    public bool ShowStrip => mode == PaneMode.Three && Listed;
    public bool ShowDelegateInspector => ShowInspector && Chosen;
    public bool ShowSessionInspector => ShowInspector && !Chosen;
    public bool ShowDelegateList => ShowDelegates && Listed && !Chosen;
    public bool ShowDelegateDetail => ShowDelegates && Listed && Chosen;
    public bool ShowDelegateCounts => ShowDelegates && !Listed;
    // The selected delegate's words are fetched, and kept current, only while on screen.
    public bool WantsDelegate => onScreen && (ShowDelegateInspector || ShowDelegateDetail);

    // A delegate opened from the strip, the list or the conversation's events: shown in the
    // inspector, which comes back if it was hidden, or pushed in the Delegates tab.
    public void OpenDelegate(string id)
    {
        if (main.Selected is not { } card) { return; }
        card.Delegates.Select(id);
        if (!card.Delegates.HasSelected) { return; }
        if (mode == PaneMode.Three)
        {
            InspectorWanted = true;
            // Choosing a delegate brings its pane in from the trailing edge (ui.md §5.3).
            Moved?.Invoke(new PaneMove(PaneMotion.Inspector, true));
        }
        else { Tab = SessionTab.Delegates; }
    }

    // Back from a delegate to the list, or to the session's own details.
    public void CloseDelegate() => main.Selected?.Delegates.Select(null);

    // A strip item pressed: "N finished" shows or hides the finished; the delegate the inspector
    // shows is let go, back to the session's details; any other, or the selected one while the
    // inspector is hidden, is shown in it.
    public void PressStripItem(DelegateItem item)
    {
        if (main.Selected is not { } card) { return; }
        if (item.IsFold) { card.Delegates.Press(item); }
        else if (item.Selected && ShowDelegateInspector) { CloseDelegate(); }
        else { OpenDelegate(item.Id); }
    }

    // The Sessions page on screen: its page shown in a window that is. The tool history is asked
    // for only then, not for a page left for another or a window hidden in the tray.
    public bool OnScreen
    {
        get => onScreen;
        set { if (Set(ref onScreen, value)) { Changed(); } }
    }

    // The tab the session shows: the one picked, unless it has gone (no delegates, no history).
    public SessionTab ShownTab => Shown;

    public void Back() => main.Selected = null;

    // A session opened to be answered (Needs you, a toast, the tray, search) shows its question:
    // Tools or Delegates picked before would hide it. A row chosen, or a resize, keeps the tab.
    // Scrolled to as well: the conversation opens at its top, and a session already open keeps
    // where it was read to, either of which can leave its question out of sight.
    void OnOpened(SessionCardViewModel card)
    {
        if (card.Question is null || card != main.Selected) { return; }
        // The app's choice, not the person's: shown at once, under the push if there is one.
        if (Set(ref tab, SessionTab.Conversation, nameof(Tab))) { Changed(); }
        QuestionWanted?.Invoke(card);
    }

    // A session opened to be answered: the session pane brings its question into view.
    public event Action<SessionCardViewModel>? QuestionWanted;

    // Raised once the panes show the change, for the view to animate it (PaneMotion).
    public event Action<PaneMove>? Moved;

    void OnMain(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainViewModel.Selected) or "" or null)) { return; }
        var before = watched;
        Watch(main.Selected);
        Raise(nameof(Selected));
        Changed();
        if (mode != PaneMode.One || before == main.Selected) { return; }
        // One column: the session pushed over the list (or another session over this one), or
        // the list back from under it, whose Needs you lane the view never moves.
        if (main.Selected is { } card) { Moved?.Invoke(new PaneMove(PaneMotion.Push, card.Question is null)); }
        else { Moved?.Invoke(new PaneMove(PaneMotion.Pop, true)); }
    }

    void Watch(SessionCardViewModel? card)
    {
        if (watched == card) { return; }
        if (watched is not null) { watched.PropertyChanged -= OnCard; watched.Delegates.PropertyChanged -= OnDelegates; }
        watched = card;
        if (card is not null) { card.PropertyChanged += OnCard; card.Delegates.PropertyChanged += OnDelegates; }
    }

    void OnCard(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionCardViewModel.HasDelegates) or nameof(SessionCardViewModel.ShowHistory) or "" or null) { Changed(); }
        if (e.PropertyName is nameof(SessionCardViewModel.Question) && sender is SessionCardViewModel { Question: not null }) { Moved?.Invoke(new PaneMove(PaneMotion.Settle, false)); }
    }

    void OnDelegates(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DelegatesViewModel.Listed) or nameof(DelegatesViewModel.HasSelected) or "" or null) { Changed(); }
    }

    void Changed()
    {
        foreach (var p in new[]
        {
            nameof(ShowList), nameof(ShowSession), nameof(ShowBack), nameof(ShowEmpty), nameof(CanToggleInspector), nameof(ShowInspector),
            nameof(InspectorToggleName), nameof(ShowTabs), nameof(ShowDelegatesTab), nameof(ShowToolsTab), nameof(ShowConversation), nameof(ShowDelegates),
            nameof(ShowTools), nameof(WantsTools), nameof(ShownTab), nameof(ShowStrip), nameof(ShowDelegateInspector), nameof(ShowSessionInspector),
            nameof(ShowDelegateList), nameof(ShowDelegateDetail), nameof(ShowDelegateCounts), nameof(WantsDelegate),
        }) { Raise(p); }
        SyncHistory();
        SyncDelegate();
    }

    // The delegate's words likewise: asked for while its detail is on screen, on this card only.
    void SyncDelegate()
    {
        var want = WantsDelegate ? main.Selected : null;
        if (delegateShown == want) { return; }
        if (delegateShown is not null) { delegateShown.Delegates.DetailOpen = false; }
        delegateShown = want;
        if (want is not null) { want.Delegates.DetailOpen = true; }
    }

    // The history is loaded as it comes on screen, and kept current only while it is
    // (SessionCardViewModel.HistoryOpen): the card behind a hidden inspector stops asking.
    void SyncHistory()
    {
        var want = WantsTools ? main.Selected : null;
        if (history == want) { return; }
        if (history is not null) { history.HistoryOpen = false; }
        history = want;
        if (want is null) { return; }
        want.HistoryOpen = true;
        _ = want.LoadHistoryAsync();
    }
}
