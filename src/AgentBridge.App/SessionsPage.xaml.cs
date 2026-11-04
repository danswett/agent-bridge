using System.Collections.Specialized;
using System.ComponentModel;
using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace AgentBridge.App;

// The Sessions page (ui.md §5.2, §6.1, #180): the list, the session chosen in it, and its
// inspector. Which session is MainViewModel.Selected, and which panes show is
// PaneLayoutViewModel's, from the window's width; this keeps the list's selection, the machine
// tabs and the sort menu in step with them, and gives rows their keyboard.
public sealed partial class SessionsPage : UserControl
{
    MainViewModel? viewModel;
    // Set while the list is being told what is selected, so its event is not taken for a choice.
    bool syncing;

    public SessionsPage()
    {
        InitializeComponent();
        ListColumn.Width = new GridLength(DesignLayout.List);
        Motion.Banner(FleetNote);
        ListPane.SizeChanged += OnListPaneSized;
        MoreMachines.SizeChanged += OnTabSized;
        SizeChanged += (_, _) => Measure();
        Loaded += (_, _) => { Measure(); SyncList(); Ready(); if (!LaneItems.IsHitTestVisible) { GuardLane(); } };
        // A lane held as the page went keeps no timer running; it is held afresh as the page comes back.
        Unloaded += (_, _) => laneTimer?.Stop();
        // The list shown for the first time (one column, after a session opened at start).
        List.SizeChanged += (_, _) => Ready();
        List.Loaded += (_, _) => WatchListScroll();
        List.GettingFocus += (_, e) => (focusItem, focusRow) = e.NewFocusedElement is ListViewItem item && (item.Content ?? List.ItemFromContainer(item)) is SessionCardViewModel card ? (item, card) : (null, null);
        List.LayoutUpdated += KeepFocusWithSession;
        WatchReorder();
    }

    // The list item given keyboard focus last, and the session it drew then. Taken as focus moves
    // (GettingFocus), not after: GotFocus comes a tick later, after a layout pass that may have
    // handed the item to another session.
    ListViewItem? focusItem;
    SessionCardViewModel? focusRow;

    // Keyboard focus stays with a session, not with the list item that drew it (#410). A move
    // reaches the list as one Move, and in a layout pass or two it hands its items to sessions
    // again, keeping the moved session's old item off screen (y −14274) until it recycles it. Focus
    // stays with an item, so it was left on that item, or on an item given another session, and
    // the next Alt+Up/Down or arrow key went to the wrong row. Seen on a slow Arm64 runner, where a
    // pass can come after the row's own focus (SessionRow.TakeFocus). After each pass, focus still
    // on the item it was given, which no longer draws that session, goes to the item that does.
    void KeepFocusWithSession(object? sender, object e)
    {
        if (focusRow is not { } card || focusItem is not { } item || XamlRoot is null) { return; }
        if (!ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), item)) { return; }
        if (List.ContainerFromItem(card) is not ListViewItem drawn || ReferenceEquals(drawn, item)) { return; }
        if (drawn.Focus(item.FocusState == FocusState.Unfocused ? FocusState.Programmatic : item.FocusState)) { (focusItem, focusRow) = (drawn, card); }
    }

    // Nothing moves as the app starts (ui.md §5.3): not the first rows, not a session a toast
    // asked for as its card arrives with them, not the first Back to a list never yet shown.
    // Motion starts once the list has been laid out on screen with rows in it.
    bool ready;
    bool readying;
    void Ready()
    {
        if (ready || readying || viewModel is null) { return; }
        // Not queued (the dispatcher shutting down): the next layout asks again.
        readying = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            readying = false;
            ready = IsLoaded && viewModel.Sessions.Count > 0 && ListPane.Visibility == Visibility.Visible && List.ActualHeight > 0;
            Session.Ready = ready;
        });
    }

    public MainViewModel ViewModel
    {
        get => viewModel ?? throw new InvalidOperationException("The Sessions page has no view model yet.");
        set
        {
            viewModel = value;
            Layout = new PaneLayoutViewModel(value);
            Session.Layout = Layout;
            InspectorDelegate.Layout = Layout;
            Layout.Moved += OnMoved;
            Layout.PropertyChanged += OnLaneShown;
            value.PropertyChanged += OnMain;
            value.Machines.CollectionChanged += (_, _) => BuildMachineTabs();
            value.Sessions.CollectionChanged += OnSessionsChanged;
            value.NeedsYou.CollectionChanged += OnLaneChanged;
            WatchHeld(value);
            BuildMachineTabs();
            Bindings.Update();
            Measure();
        }
    }

    public PaneLayoutViewModel? Layout { get; private set; }
    public LaunchViewModel? Launch
    {
        get => launch;
        set { launch = value; Bindings.Update(); }
    }
    LaunchViewModel? launch;

    public event Action? NewRequested;

    internal ListView Rows => List;
    internal SessionView SessionPaneView => Session;

    // The window's width, as the panes' adaptive triggers read it, and the panes' widths at the
    // page's (SessionsPage.Dividers).
    void Measure()
    {
        if (Layout is not null && XamlRoot is { } root) { Layout.Width = root.Size.Width; }
        SizePanes();
    }

    void OnMain(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Selected) or "" or null) { SyncList(); }
        if (e.PropertyName is nameof(MainViewModel.SelectedMachine) or "" or null) { SyncMachineTab(); }
    }

    // The list shows MainViewModel.Selected, whoever chose it: a row, search, a toast or Back.
    void SyncList()
    {
        if (viewModel is null) { return; }
        var want = viewModel.Selected;
        if (List.SelectedItem == want) { return; }
        syncing = true;
        try { List.SelectedItem = want; }
        finally { syncing = false; }
        if (want is null) { return; }
        // The row brought into view moves the list, and Needs you with it (the lane is the list's
        // header): the second click of a habit that opened this from Needs you must not land on
        // another entry's Approve once.
        if (Layout?.ShowList == true) { GuardLane(); }
        // Scrolled to smoothly (ui.md §5.3, Scroll to a question) when its row is drawn; one far
        // off screen has no row yet, and the list jumps to it.
        if (List.ContainerFromItem(want) is ListViewItem row) { row.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = ready && Motion.Scrolls }); }
        else { List.ScrollIntoView(want); }
    }

    // --- Motion (ui.md §5.3): what the panes did, and rows and entries arriving. ---

    void OnMoved(PaneMove move)
    {
        if (move.Kind == PaneMotion.Settle) { Session.Moved(move); return; }
        // The list, and its lane, back under a pointer that was on the session, motion or none.
        if (move.Kind == PaneMotion.Pop) { GuardLane(); }
        if (!ready) { return; }
        switch (move.Kind)
        {
            case PaneMotion.Push or PaneMotion.Tab:
                Session.Moved(move);
                break;
            case PaneMotion.Pop:
                // The list's content slides in from the leading side. Its Needs you lane is there at
                // once: it sits in the list's header, outside the rows' panel, unless a template
                // puts it inside, when the rows stay still too.
                var from = -Motion.Trailing(this);
                foreach (var part in new UIElement[] { ListHead, MachineBar, ListNotes }) { Motion.Enter(part, "Push", from); }
                if (List.ItemsPanelRoot is { } panel && !Motion.Within(LaneItems, panel)) { Motion.Enter(panel, "Push", from); }
                break;
            case PaneMotion.Inspector:
                Motion.Enter(InspectorContent, "Pane", Motion.Trailing(this));
                break;
        }
    }

    // A session a host added fades in where it lands; its neighbours take their places at once.
    void OnArrived(SessionCardViewModel card)
    {
        if (!ready || Layout?.ShowList != true) { return; }
        Motion.AfterLayout(List, card, row => Motion.Enter(row, "ListItem"));
    }

    // A question asked: only its entry's mark fades in. The entry and an approval's command are
    // there at once, and never fade or move.
    void OnLaneChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Any change to the lane can put Approve once under a pointer aimed at something else: an
        // entry appearing, the entries moving, or those after a removed one shifting up into its
        // place. The lane takes no click for a moment (AnswerGuard).
        GuardLane();
        if (!ready || viewModel is null || Layout?.ShowList != true || e.Action != NotifyCollectionChangedAction.Add) { return; }
        foreach (NeedsYouEntry entry in e.NewItems ?? Array.Empty<object>())
        {
            if (!viewModel.IsArriving(entry)) { continue; }
            Motion.AfterLayout(LaneItems, entry, item =>
            {
                if (Glyph(item) is { } mark) { Motion.Enter(mark, "ListItem"); }
            });
        }
    }

    static StatusGlyphView? Glyph(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is StatusGlyphView glyph) { return glyph; }
            if (Glyph(child) is { } found) { return found; }
        }
        return null;
    }

    void OnRowChosen(object sender, SelectionChangedEventArgs e)
    {
        if (syncing || viewModel is null) { return; }
        if (List.SelectedItem is SessionCardViewModel card) { viewModel.Selected = card; return; }
        // The list lets go of its selection as rows move (a reorder is a remove and an insert), and
        // on Ctrl+click: the session stays open. Only Back, its end or its machine's tab closes it
        // (MainViewModel.Selected).
        if (viewModel.Selected is not null) { DispatcherQueue.TryEnqueue(SyncList); }
    }
    // A row put aside for reuse stays loaded in the list's recycle queue, still holding its old
    // session, and is loaded again there: it lets go of that session, rather than listening to one
    // no longer listed, and listens again only once it is given one to show.
    void OnRowRecycled(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer?.ContentTemplateRoot is not SessionRow row) { return; }
        // A row put aside or given to another session shows that one as it is.
        Motion.Settle(args.ItemContainer);
        if ((drag is not null || settling.Count > 0) && args.ItemContainer is ListViewItem item) { ReorderRecycled(item); }
        if (args.InRecycleQueue) { row.Park(); }
        else { row.Unpark(); }
    }
    // A row whose session leaves the list (its machine's tab, its end) can stay loaded in the
    // list's panel, put aside for reuse without being told: it lets go of the session once the
    // list has settled. A session moved (a remove and an insert) is listed again by then.
    void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!ready) { Ready(); }
        // Only a person's own change reaches the list while a card is held (MainViewModel.Held).
        Abandon();
        if (e.Action == NotifyCollectionChangedAction.Add && viewModel is not null)
        {
            foreach (SessionCardViewModel card in e.NewItems ?? Array.Empty<object>())
            {
                if (viewModel.IsArriving(card)) { OnArrived(card); }
            }
        }
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Move) { return; }
        DispatcherQueue.TryEnqueue(ReleaseUnlisted);
    }

    void ReleaseUnlisted()
    {
        if (viewModel is null) { return; }
        foreach (var row in RowsUnder(List))
        {
            if (row.Card is { } card && !viewModel.Sessions.Contains(card)) { row.Park(); }
        }
    }

    static IEnumerable<SessionRow> RowsUnder(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is SessionRow row) { yield return row; continue; }
            foreach (var inner in RowsUnder(child)) { yield return inner; }
        }
    }
    // Alt+Up and Alt+Down on a row move it within its group (#164).
    void OnListKey(object sender, KeyRoutedEventArgs e)
    {
        var alt = e.KeyStatus.IsMenuKeyDown || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!alt && e.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown)
        {
            // After the list has moved focus for the key, in its own handler.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FollowFocus);
            return;
        }
        if (e.Key is not (VirtualKey.Up or VirtualKey.Down) || drag is not null) { return; }
        if (!alt || RowOf(e.OriginalSource as DependencyObject) is not { } row) { return; }
        e.Handled = true;
        row.MoveBy(e.Key == VirtualKey.Up ? -1 : 1);
    }

    // The list's own selection-follows-focus is off: with it, any keyboard focus on a row chose
    // it, so a row given focus back after a move, or a focused row drawn again as the list
    // reordered, took the open session from one opened by a toast, search or Needs you. Moving
    // through the rows with the keys still opens each row reached: once the list has moved focus
    // for the key, the row with focus is chosen.
    internal void FollowFocus()
    {
        if (viewModel is null || XamlRoot is null) { return; }
        if (FocusManager.GetFocusedElement(XamlRoot) is not ListViewItem { Content: SessionCardViewModel card } item || ItemsControl.ItemsControlFromItemContainer(item) != List) { return; }
        if (viewModel.Selected != card) { viewModel.Selected = card; }
    }
    // Shift+F10 or the menu key on a row: its menu, as right-click opens it.
    void OnListMenu(UIElement sender, ContextRequestedEventArgs e)
    {
        if (RowOf(e.OriginalSource as DependencyObject) is not { } row) { return; }
        e.Handled = true;
        row.OpenMenu();
    }

    // The row a key or menu request came from: the list item itself, which holds the row, or a
    // part of the row.
    static SessionRow? RowOf(DependencyObject? node)
    {
        for (var up = node; up is not null; up = VisualTreeHelper.GetParent(up))
        {
            if (up is SessionRow row) { return row; }
            if (up is ListViewItem item) { return item.ContentTemplateRoot as SessionRow; }
        }
        return null;
    }

    void OnNew(object sender, RoutedEventArgs e) => NewRequested?.Invoke();

    // --- The machine tabs (§6.1, #290): built here, as a SelectorBar takes no items source. ---
    // One tab per machine choice, kept while the choice is; the bar holds only those that fit
    // (MachineTabFit), in order, and More lists the rest. They never scroll sideways: a thin
    // scrollbar under them once hid machines past the pane's edge, and was hard to hit.

    readonly Dictionary<MachineChoice, SelectorBarItem> tabs = [];
    // The choices under More, in their order, as last arranged.
    List<MachineChoice> overflow = [];
    // A tab's own padding and border, beside its name: read from one drawn, until then the default
    // style's (SelectorBarItemPadding 12 each side, a 1 px border).
    double tabChrome = 26;
    bool chromeSeen;
    // Measures a name as a tab draws it, off the page; and More's words in More's own style.
    readonly TextBlock ruler = new() { TextWrapping = TextWrapping.NoWrap };
    readonly TextBlock moreRuler = new() { TextWrapping = TextWrapping.NoWrap };
    bool arrangeQueued;
    // The chosen tab, when it alone would push More past the bar's edge, and the most its name may then
    // take (MachineTabSplit.ChosenWidth less its padding); every other name takes DesignLayout.MachineTab.
    SelectorBarItem? cutTab;
    double cutName;

    void BuildMachineTabs()
    {
        if (viewModel is null) { return; }
        var gone = tabs.Keys.Except(viewModel.Machines).ToList();
        foreach (var old in gone)
        {
            old.PropertyChanged -= OnChoiceNamed;
            tabs.Remove(old);
        }
        // A machine forgotten, or kept offline past its time, while More is open: its line would
        // choose nothing (MainViewModel refuses a choice no longer listed), so the menu goes.
        if (gone.Count > 0 && MoreMachinesMenu.IsOpen) { MoreMachinesMenu.Hide(); }
        foreach (var choice in viewModel.Machines.Where(c => !tabs.ContainsKey(c)))
        {
            var item = new SelectorBarItem { Tag = choice };
            item.Loaded += OnTabLoaded;
            item.SizeChanged += OnTabSized;
            NameTab(item, choice);
            tabs[choice] = item;
            // A machine is named once its host says its name, after its tab is made.
            choice.PropertyChanged += OnChoiceNamed;
        }
        ArrangeMachineTabs();
    }

    static void NameTab(SelectorBarItem item, MachineChoice choice)
    {
        item.Text = choice.Short;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, choice.Label);
        ToolTipService.SetToolTip(item, choice.Label);
    }

    // A long name is cut short with an ellipsis, as a row's title is, where the tab's own text would
    // wrap onto a second line. The whole name is the tooltip and the accessible name. The tab's own
    // text keeps its pointer-over, pressed and disabled looks, which a Child of ours would lose.
    // The first tab drawn gives the tabs' real padding and face (ObserveTabChrome): until then they
    // were fitted by the default style's, so they are fitted again.
    void OnTabLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not SelectorBarItem item || NameOf(item) is not { } name) { return; }
        name.TextWrapping = TextWrapping.NoWrap;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.MaxWidth = NameCap(item);
        if (!chromeSeen) { QueueArrange(); }
    }

    double NameCap(SelectorBarItem item) => item == cutTab ? Math.Min(cutName, DesignLayout.MachineTab) : DesignLayout.MachineTab;

    // The tab's name as drawn: its template's text (the self-test reads it too).
    internal static TextBlock? NameOf(SelectorBarItem item) => Descendants(item).OfType<TextBlock>().FirstOrDefault(t => t.Name == "PART_TextVisual");

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            yield return child;
            foreach (var inner in Descendants(child)) { yield return inner; }
        }
    }

    internal IReadOnlyList<MachineChoice> MachinesUnderMore => overflow;
    internal Button MoreMachinesButton => MoreMachines;
    internal TextBlock MoreMachinesWords => MoreMachinesText;
    // How many times the tabs have been arranged, for the self-test to see them settle.
    internal int Arrangements { get; private set; }
    // What the tabs were last arranged from, for the self-test to say.
    internal string Arranged { get; private set; } = "";

    void OnChoiceNamed(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not MachineChoice choice || !tabs.TryGetValue(choice, out var item)) { return; }
        NameTab(item, choice);
        // Its line under More, open while the machine goes offline or its sessions change, says so as
        // its tab does; it was written only as the menu opened (#368 review).
        foreach (var line in MoreMachinesMenu.Items.OfType<MenuFlyoutItem>().Where(i => i.Tag == choice)) { NameLine(line, choice); }
        // A longer or shorter name may change what fits.
        if (e.PropertyName is nameof(MachineChoice.Short) or "" or null) { QueueArrange(); }
    }

    // The machine chosen may have come from the bar's own selection change, whose items are not
    // to be changed inside it.
    void SyncMachineTab() => QueueArrange();

    // The list pane's width, not the bar's: a bar whose tabs outgrow it is laid out at their width
    // and clipped, so its own width would let them stay too wide. Once per tick while a divider drags.
    void OnListPaneSized(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width != e.PreviousSize.Width) { QueueArrange(); }
    }

    // A tab or More drawn at another width with the pane's unchanged: Windows' text size changed under
    // them (Settings, Accessibility, Text size), or a name or More's count did. Their widths were fitted
    // by the old size, so More could be clipped at the narrowest list until something else refitted.
    // Fitting again is settled: it changes the bar only when what it measures has changed, so it raises
    // no new size of its own.
    void OnTabSized(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width != e.PreviousSize.Width) { QueueArrange(); }
    }

    // Later, so a change never lands inside the bar's own selection change; at low priority, after the
    // layout pass, so every size that pass changed is fitted once.
    void QueueArrange()
    {
        if (arrangeQueued) { return; }
        arrangeQueued = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { arrangeQueued = false; ArrangeMachineTabs(); })) { arrangeQueued = false; }
    }

    // Which tabs the bar holds, from their names' widths and the bar's own, and which it selects.
    void ArrangeMachineTabs()
    {
        if (viewModel is null) { return; }
        Arrangements++;
        var choices = viewModel.Machines.Where(tabs.ContainsKey).ToList();
        var chosen = Math.Max(0, choices.IndexOf(viewModel.SelectedMachine!));
        var room = ListPane.ActualWidth - ListPane.BorderThickness.Left - ListPane.BorderThickness.Right - MachineBar.Padding.Left - MachineBar.Padding.Right;
        // No width while the pane isn't drawn (not laid out yet, or collapsed behind a pushed
        // session): every tab stays in the bar, out of sight, until its SizeChanged arranges them.
        // Returning instead left the bar empty or stale for whatever reads it meanwhile.
        MachineTabSplit split;
        if (room <= 0 || choices.Count == 0) { split = new([.. Enumerable.Range(0, choices.Count)], []); }
        else
        {
            ObserveTabChrome();
            var widths = choices.Select(c => Math.Ceiling(Math.Min(TextWidth(c.Short), DesignLayout.MachineTab) + tabChrome)).ToList();
            // More's width with the most it could ever say, so the count changing never makes it outgrow its room.
            SayMore(choices.Count - 1);
            MoreMachines.Visibility = Visibility.Visible;
            MoreMachines.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            // Not drawn yet, its template may not be applied: its parts, then.
            var more = Math.Max(MoreMachines.DesiredSize.Width, MoreTextWidth() + MoreMachinesContent.Spacing + MoreMachinesChevron.Width + MoreMachines.Padding.Left + MoreMachines.Padding.Right);
            split = MachineTabFit.Split(widths, chosen, room, Math.Ceiling(more) + MachineBar.ColumnSpacing);
            Arranged = $"room {room:0}, More {more:0}, tabs {string.Join(" ", widths)}, chrome {tabChrome:0.#}";
        }
        overflow = [.. split.More.Select(i => choices[i])];
        cutTab = split.ChosenWidth is not null ? tabs[choices[chosen]] : null;
        cutName = split.ChosenWidth is { } cut ? Math.Max(0, Math.Floor(cut - tabChrome)) : DesignLayout.MachineTab;
        SayMore(overflow.Count);
        MoreMachines.Visibility = overflow.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // The fewest changes to the bar's items, so a tab with keyboard focus keeps it.
        var want = split.Shown.Select(i => tabs[choices[i]]).ToList();
        syncing = true;
        try
        {
            foreach (var gone in MachineTabs.Items.Except(want).ToList()) { MachineTabs.Items.Remove(gone); }
            for (var i = 0; i < want.Count; i++)
            {
                var at = MachineTabs.Items.IndexOf(want[i]);
                if (at == i) { continue; }
                if (at >= 0) { MachineTabs.Items.RemoveAt(at); }
                MachineTabs.Items.Insert(i, want[i]);
            }
            var selected = choices.Count > 0 ? tabs[choices[chosen]] : null;
            if (MachineTabs.SelectedItem != selected) { MachineTabs.SelectedItem = selected; }
        }
        finally { syncing = false; }
        // A tab loaded later takes its cap in OnTabLoaded; those already drawn take it here.
        foreach (var item in want) { if (NameOf(item) is { } name) { name.MaxWidth = NameCap(item); } }
    }

    void SayMore(int count)
    {
        MoreMachinesText.Text = $"{count} more";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(MoreMachines, $"{Words.Plural(count, "more machine", "more machines")}");
    }

    double TextWidth(string text)
    {
        ruler.Text = text;
        ruler.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return ruler.DesiredSize.Width;
    }

    double MoreTextWidth()
    {
        moreRuler.Style = MoreMachinesText.Style;
        moreRuler.FontFamily = MoreMachinesText.FontFamily;
        moreRuler.Text = MoreMachinesText.Text;
        moreRuler.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return moreRuler.DesiredSize.Width;
    }

    void ObserveTabChrome()
    {
        foreach (var item in MachineTabs.Items)
        {
            if (item.ActualWidth > 0 && NameOf(item) is { ActualWidth: > 0 } name)
            {
                tabChrome = item.ActualWidth - name.ActualWidth;
                chromeSeen = true;
                // The face the tab draws its name in, from its style: the ruler has none of its own.
                ruler.FontFamily = name.FontFamily;
                ruler.FontSize = name.FontSize;
                ruler.FontWeight = name.FontWeight;
                return;
            }
        }
    }

    void OnMachineTab(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (syncing || viewModel is null || sender.SelectedItem?.Tag is not MachineChoice choice) { return; }
        viewModel.SelectedMachine = choice;
        // A tab chosen: its rows fade in, without sliding (ui.md §5.3, Tab switch).
        if (ready && List.ItemsPanelRoot is { } panel) { Motion.Enter(panel, "Tab"); }
    }

    // More: the machines that don't fit, each with its state, as its tab's tooltip says it.
    void OnMoreMachinesOpening(object sender, object e)
    {
        MoreMachinesMenu.Items.Clear();
        foreach (var choice in overflow)
        {
            var item = new MenuFlyoutItem { Tag = choice };
            NameLine(item, choice);
            item.Click += (_, _) => ChooseFromMore(choice);
            MoreMachinesMenu.Items.Add(item);
        }
    }

    static void NameLine(MenuFlyoutItem line, MachineChoice choice)
    {
        line.Text = choice.Label;
        ToolTipService.SetToolTip(line, choice.Label);
    }

    // A machine chosen under More is a tab chosen like any other: its rows fade in, without sliding
    // (ui.md §5.3, Tab switch), as OnMachineTab has them. The bar's own selection follows later
    // (SyncMachineTab), while syncing, so OnMachineTab doesn't see it.
    void ChooseFromMore(MachineChoice choice)
    {
        if (viewModel is null || viewModel.SelectedMachine == choice) { return; }
        viewModel.SelectedMachine = choice;
        if (viewModel.SelectedMachine == choice && ready && List.ItemsPanelRoot is { } panel) { Motion.Enter(panel, "Tab"); }
    }

    // --- The sort menu: Your order, Most recent, Machine (#164), the one in force checked. ---

    void OnSortOpening(object sender, object e)
    {
        if (viewModel is null) { return; }
        SortMenu.Items.Clear();
        for (var i = 0; i < MainViewModel.SortChoices.Count; i++)
        {
            var index = i;
            var item = new RadioMenuFlyoutItem { Text = MainViewModel.SortChoices[i], GroupName = "Sort", IsChecked = viewModel.SortIndex == i };
            item.Click += (_, _) => viewModel.SortIndex = index;
            SortMenu.Items.Add(item);
        }
    }

    // Needs you's Approve once and Deny take no click for a moment after an entry appears or the
    // list comes back (App.Core's AnswerGuard): the lane is out of hit testing meanwhile, while
    // keyboard focus and keys work as ever.
    readonly AnswerGuard laneGuard = new();
    Microsoft.UI.Dispatching.DispatcherQueueTimer? laneTimer;

    // Each step of the list's own scroll, while the lane is held, holds it until the list is still.
    ScrollViewer? listScroll;

    void WatchListScroll()
    {
        if (listScroll is not null || Find<ScrollViewer>(List) is not { } scroll) { return; }
        listScroll = scroll;
        scroll.ViewChanged += (_, _) =>
        {
            laneGuard.Scrolled(DateTimeOffset.UtcNow);
            OnListScrolled();
        };
    }

    static T? Find<T>(DependencyObject node) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if (child is T found) { return found; }
            if (Find<T>(child) is { } deeper) { return deeper; }
        }
        return null;
    }

    // The window activated again (brought back from behind another, not only restored): what it
    // shows is under a pointer aimed at what was in front of it.
    internal void Reactivated()
    {
        if (Layout is { ShowList: true, OnScreen: true }) { GuardLane(); }
        Session.Reactivated();
    }

    // The list back on screen (the Sessions page chosen again, the window shown from the tray, the
    // list popped back) puts the lane under a pointer aimed at what was there.
    void OnLaneShown(object? sender, PropertyChangedEventArgs e)
    {
        if (Layout is null || e.PropertyName is not (nameof(PaneLayoutViewModel.ShowList) or nameof(PaneLayoutViewModel.OnScreen) or "" or null)) { return; }
        if (laneGuard.Shown(Layout.ShowList && Layout.OnScreen, DateTimeOffset.UtcNow)) { GuardLane(); }
    }

    void GuardLane()
    {
        laneGuard.Arm(DateTimeOffset.UtcNow);
        LaneItems.IsHitTestVisible = false;
        if (laneTimer is null)
        {
            laneTimer = DispatcherQueue.CreateTimer();
            laneTimer.IsRepeating = false;
            laneTimer.Tick += (t, _) =>
            {
                var left = laneGuard.Remaining(DateTimeOffset.UtcNow);
                if (left > TimeSpan.Zero) { t.Interval = left; t.Start(); return; }
                LaneItems.IsHitTestVisible = true;
            };
        }
        laneTimer.Interval = laneGuard.Remaining(DateTimeOffset.UtcNow);
        laneTimer.Start();
    }
}
