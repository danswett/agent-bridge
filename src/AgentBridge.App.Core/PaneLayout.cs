using System.ComponentModel;

namespace AgentBridge.App.Core;

// The Sessions page's panes by window width (ui.md §5.2, #180), at design/tokens.json's
// breakpoints, the PWA's (DesignLayout):
//   Three (≥ 1280): list · session · inspector (Details and Tools), the inspector hideable;
//   Two (760–1279): list · session, with Conversation, Delegates and Tools tabs;
//   One (< 760): the list, or the session with a way back to the list.
// The session shown is MainViewModel.Selected, so a resize changes only how it is shown.
public enum PaneMode { One, Two, Three }

public enum SessionTab { Conversation, Delegates, Tools }

public sealed class PaneLayoutViewModel : Observable
{
    readonly MainViewModel main;
    PaneMode mode = PaneMode.Three;
    bool inspectorWanted = true;
    SessionTab tab;
    SessionCardViewModel? watched;
    // The card whose tool history is open, loaded and kept current while on screen.
    SessionCardViewModel? history;

    public PaneLayoutViewModel(MainViewModel main)
    {
        this.main = main;
        main.PropertyChanged += OnMain;
        Watch(main.Selected);
        SyncHistory();
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
    public void ToggleInspector() => InspectorWanted = !inspectorWanted;
    public string InspectorToggleName => ShowInspector ? "Hide the inspector" : "Show the inspector";

    // The tabs: two panes and one column, where there is no inspector. Delegates only while the
    // host reports some (U8, #176, lists them).
    public bool ShowTabs => mode != PaneMode.Three && Has;
    public bool ShowDelegatesTab => main.Selected?.HasDelegates == true;
    // Tools only where the machine can send a session's history (CardContext.ShowDetail).
    public bool ShowToolsTab => main.Selected?.ShowHistory == true;
    public SessionTab Tab
    {
        get => tab;
        set { if (Set(ref tab, value)) { Changed(); } }
    }
    // The tab in force: Delegates falls back to Conversation once the session has none.
    SessionTab Shown => !ShowTabs ? SessionTab.Conversation
        : tab == SessionTab.Delegates && !ShowDelegatesTab ? SessionTab.Conversation
        : tab == SessionTab.Tools && !ShowToolsTab ? SessionTab.Conversation
        : tab;
    public bool ShowConversation => Has && Shown == SessionTab.Conversation;
    public bool ShowDelegates => Has && Shown == SessionTab.Delegates;
    public bool ShowTools => Has && Shown == SessionTab.Tools;
    // The tool history is wanted where it is on screen: the inspector or the Tools tab.
    public bool WantsTools => (ShowInspector || ShowTools) && ShowToolsTab;

    public void Back() => main.Selected = null;

    void OnMain(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainViewModel.Selected) or "" or null)) { return; }
        Watch(main.Selected);
        Raise(nameof(Selected));
        Changed();
    }

    void Watch(SessionCardViewModel? card)
    {
        if (watched == card) { return; }
        if (watched is not null) { watched.PropertyChanged -= OnCard; }
        watched = card;
        if (card is not null) { card.PropertyChanged += OnCard; }
    }

    void OnCard(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionCardViewModel.HasDelegates) or nameof(SessionCardViewModel.ShowHistory) or "" or null) { Changed(); }
    }

    void Changed()
    {
        foreach (var p in new[]
        {
            nameof(ShowList), nameof(ShowSession), nameof(ShowBack), nameof(ShowEmpty), nameof(CanToggleInspector), nameof(ShowInspector),
            nameof(InspectorToggleName), nameof(ShowTabs), nameof(ShowDelegatesTab), nameof(ShowToolsTab), nameof(ShowConversation), nameof(ShowDelegates),
            nameof(ShowTools), nameof(WantsTools),
        }) { Raise(p); }
        SyncHistory();
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
