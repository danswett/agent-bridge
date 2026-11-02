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
        SizeChanged += (_, _) => Measure();
        Loaded += (_, _) => { Measure(); SyncList(); };
    }

    public MainViewModel ViewModel
    {
        get => viewModel ?? throw new InvalidOperationException("The Sessions page has no view model yet.");
        set
        {
            viewModel = value;
            Layout = new PaneLayoutViewModel(value);
            Session.Layout = Layout;
            value.PropertyChanged += OnMain;
            value.Machines.CollectionChanged += (_, _) => BuildMachineTabs();
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

    // The window's width, as the panes' adaptive triggers read it.
    void Measure()
    {
        if (Layout is not null && XamlRoot is { } root) { Layout.Width = root.Size.Width; }
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
        if (want is not null) { List.ScrollIntoView(want); }
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
    // session: it lets go of it there, rather than listening to a session no longer listed, and
    // listens again once it is given one.
    void OnRowRecycled(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer?.ContentTemplateRoot is not SessionRow row) { return; }
        if (args.InRecycleQueue) { row.Detach(); }
        else if (row.IsLoaded) { row.Attach(); }
    }
    // Alt+Up and Alt+Down on a row move it within its group (#164).
    void OnListKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Up or VirtualKey.Down)) { return; }
        var alt = e.KeyStatus.IsMenuKeyDown || Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!alt || RowOf(e.OriginalSource as DependencyObject) is not { } row) { return; }
        e.Handled = true;
        row.MoveBy(e.Key == VirtualKey.Up ? -1 : 1);
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

    // --- The machine tabs (§6.1): built here, as a SelectorBar takes no items source. ---

    void BuildMachineTabs()
    {
        if (viewModel is null) { return; }
        syncing = true;
        try
        {
            MachineTabs.Items.Clear();
            foreach (var old in named) { old.PropertyChanged -= OnChoiceNamed; }
            named.Clear();
            foreach (var choice in viewModel.Machines)
            {
                var item = new SelectorBarItem { Tag = choice };
                NameTab(item, choice);
                MachineTabs.Items.Add(item);
                // A machine is named once its host says its name, after its tab is made.
                choice.PropertyChanged += OnChoiceNamed;
                named.Add(choice);
            }
        }
        finally { syncing = false; }
        SyncMachineTab();
    }

    readonly List<MachineChoice> named = [];

    static void NameTab(SelectorBarItem item, MachineChoice choice)
    {
        item.Text = choice.Short;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, choice.Label);
        ToolTipService.SetToolTip(item, choice.Label);
    }

    void OnChoiceNamed(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is MachineChoice choice && MachineTabs.Items.FirstOrDefault(i => i.Tag == choice) is { } item) { NameTab(item, choice); }
    }
    void SyncMachineTab()
    {
        if (viewModel is null) { return; }
        var want = MachineTabs.Items.FirstOrDefault(i => i.Tag == viewModel.SelectedMachine) ?? MachineTabs.Items.FirstOrDefault();
        if (MachineTabs.SelectedItem == want) { return; }
        syncing = true;
        try { MachineTabs.SelectedItem = want; }
        finally { syncing = false; }
    }

    void OnMachineTab(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (syncing || viewModel is null || sender.SelectedItem?.Tag is not MachineChoice choice) { return; }
        viewModel.SelectedMachine = choice;
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
}