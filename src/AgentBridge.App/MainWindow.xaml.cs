using System.Runtime.InteropServices;
using AgentBridge.App.Core;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace AgentBridge.App;

public sealed partial class MainWindow : Window
{
    bool active;
    // Where the window is kept between runs (<profile>-window.json); null in the self-test, whose
    // window is sized for its pictures.
    readonly string? placementFile;
    // The last place the window had while neither maximised nor minimised: a maximised window is
    // kept as maximised over where it goes back to.
    PixelRect? normal;
    DispatcherQueueTimer? saveSoon;
    // Maximised when first shown, not when placed: maximising shows a window, and one started in the
    // background at sign-in must stay hidden.
    bool maximizeWhenShown;

    internal MainWindow(MainViewModel viewModel, LaunchViewModel launch, bool selfTest, string? placementFile = null)
    {
        ViewModel = viewModel;
        Launch = launch;
        this.placementFile = selfTest ? null : placementFile;
        InitializeComponent();
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        CurrentHandle = Handle;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AgentBridge.ico"));

        // The title bar is the window's own (ui.md §7), over Mica; the caption buttons stay the
        // system's, as tall as the bar and in the app's theme.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;

        if (selfTest) { AppWindow.Resize(new SizeInt32(1100, 1400)); }
        else { Place(); }
        KeepSmallest();
        Root.Loaded += (_, _) => { if (Root.XamlRoot is { } x) { x.Changed += (_, _) => KeepSmallest(); } };
        AppWindow.Changed += OnWindowChanged;

        AppWindow.Closing += (_, e) => CloseRequested?.Invoke(e);
        Activated += (_, e) =>
        {
            active = e.WindowActivationState != WindowActivationState.Deactivated;
            if (active && maximizeWhenShown && AppWindow.Presenter is OverlappedPresenter p)
            {
                maximizeWhenShown = false;
                p.Maximize();
            }
        };
        UsagePane.ViewModel = viewModel.Usage;
        LaunchPane.ViewModel = launch;
        if (viewModel.Settings is { } s) { SettingsPane.ViewModel = s; }
        Nav.SelectedItem = SessionsItem;
        viewModel.OpenRequested += Reveal;
    }

    public MainViewModel ViewModel { get; }
    public LaunchViewModel Launch { get; }
    public nint Handle { get; }
    // The window file pickers belong to: an unpackaged app must say which.
    internal static nint CurrentHandle { get; private set; }
    public event Action<AppWindowClosingEventArgs>? CloseRequested;

    // In front and visible: a toast would only repeat what the window shows.
    public bool IsInFront => active && AppWindow.IsVisible;

    internal ItemsControl CardList => Cards;
    internal FrameworkElement RootElement => Root;
    internal NavigationView Rail => Nav;
    internal TitleBar Bar => AppTitleBar;
    internal AutoSuggestBox Search => SearchBox;
    internal string CurrentPage => (Nav.SelectedItem as NavigationViewItem)?.Tag as string ?? "";

    public void ShowInFront()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) { p.Restore(); }
        Activate();
        TrayIcon.SetForegroundWindow(Handle);
    }

    public void HideToTray()
    {
        SavePlacement();
        AppWindow.Hide();
    }

    internal void ShowPage(string tag) =>
        Nav.SelectedItem = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>().First(i => (string)i.Tag == tag);

    void OnNavigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        SessionsPage.Visibility = Ui.Show(tag == "sessions");
        MachinesPage.Visibility = Ui.Show(tag == "machines");
        UsagePane.Visibility = Ui.Show(tag == "usage");
        LaunchPane.Visibility = Ui.Show(tag == "launch");
        if (tag == "launch") { Launch.Opened(); } else { Launch.Closed(); }
        SettingsPane.Visibility = Ui.Show(tag == "settings");
        if (tag == "settings") { SettingsPane.Opened(); }
    }

    void OnMachineChosen(object sender, SelectionChangedEventArgs e)
    {
        if (MachinePicker.SelectedItem is MachineChoice choice) { ViewModel.SelectedMachine = choice; }
    }

    // A machine's row on the Machines page: its sessions, on the Sessions page.
    void OnShowMachineSessions(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MachineRowViewModel row || ViewModel.Machines.FirstOrDefault(c => c.Key == row.Key) is not { } choice) { return; }
        ViewModel.SelectedMachine = choice;
        ShowPage("sessions");
    }

    static MachineRowViewModel? RowOf(object sender) => (sender as FrameworkElement)?.Tag as MachineRowViewModel;
    void OnCheckHere(object sender, RoutedEventArgs e) => ViewModel.CheckLocal?.Invoke();
    void OnCheckMachine(object sender, RoutedEventArgs e) => RowOf(sender)?.Update?.Check();
    void OnInstallMachine(object sender, RoutedEventArgs e) => RowOf(sender)?.Update?.Install();
    void OnForgetMachine(object sender, RoutedEventArgs e) => RowOf(sender)?.Offline?.Press();

    // A toast, the tray or the search box asked for a session: its card, on screen.
    void Reveal(SessionCardViewModel card)
    {
        Nav.SelectedItem = SessionsItem;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Cards.ContainerFromItem(card) is UIElement element)
            {
                element.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true, VerticalAlignmentRatio = 0 });
            }
        });
    }

    // --- Search (ui.md §7, §9): every machine's sessions, opened as a toast opens one. ---

    void OnFocusSearch(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Keyboard);
    }

    void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) { SearchFor(sender.Text); }
    }

    internal void SearchFor(string text) => SearchBox.ItemsSource = ViewModel.Search(text);

    void OnSearchChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is SearchHit hit) { OpenHit(hit); }
    }

    // Enter opens the suggestion picked, or the first one found.
    void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var hit = args.ChosenSuggestion as SearchHit ?? ViewModel.Search(args.QueryText).FirstOrDefault();
        if (hit is not null) { OpenHit(hit); }
    }

    internal void OpenHit(SearchHit hit)
    {
        SearchBox.Text = "";
        SearchBox.ItemsSource = null;
        ViewModel.Open(hit);
    }

    // --- Size and place (#180): down to 360×600, and back where it was left. ---

    double Scale => GetDpiForWindow(Handle) is var dpi and > 0 ? dpi / 96.0 : 1;

    // The smallest window ui.md allows, at the scale of the display the window is on now.
    void KeepSmallest()
    {
        if (AppWindow.Presenter is not OverlappedPresenter p) { return; }
        var (w, h) = WindowPlacement.Smallest(Scale);
        p.PreferredMinimumWidth = w;
        p.PreferredMinimumHeight = h;
    }

    void Place()
    {
        var saved = WindowPlacement.Load(placementFile);
        var areas = DisplayArea.FindAll();
        var displays = new List<Display>();
        for (var i = 0; i < areas.Count; i++) { displays.Add(Of(areas[i])); }
        var primary = Of(DisplayArea.Primary);
        var at = WindowPlacement.Place(saved, displays, primary);
        AppWindow.MoveAndResize(new RectInt32(at.X, at.Y, at.Width, at.Height));
        normal = at.Rect;
        maximizeWhenShown = at.Maximized;
    }

    static Display Of(DisplayArea area)
    {
        var w = area.WorkArea;
        var scale = 1.0;
        var monitor = Win32Interop.GetMonitorFromDisplayId(area.DisplayId);
        if (GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0) { scale = dpi / 96.0; }
        return new Display(new PixelRect(w.X, w.Y, w.Width, w.Height), scale);
    }

    void OnWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange && !args.DidSizeChange && !args.DidPresenterChange) { return; }
        if (args.DidPositionChange || args.DidSizeChange) { KeepSmallest(); }
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            normal = new PixelRect(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        }
        if (placementFile is null) { return; }
        // Kept once the window settles, not at every step of a drag.
        saveSoon ??= DispatcherQueue.CreateTimer();
        saveSoon.Interval = TimeSpan.FromSeconds(1);
        saveSoon.IsRepeating = false;
        saveSoon.Tick -= OnSaveSoon;
        saveSoon.Tick += OnSaveSoon;
        saveSoon.Start();
    }

    void OnSaveSoon(DispatcherQueueTimer sender, object args) => SavePlacement();

    // Also on hiding to the tray and on quitting. A minimised window keeps where it was before.
    internal void SavePlacement()
    {
        if (placementFile is null || normal is not { } r || AppWindow.Presenter is not OverlappedPresenter p || p.State == OverlappedPresenterState.Minimized) { return; }
        try { new WindowPlacement(r.X, r.Y, r.Width, r.Height, p.State == OverlappedPresenterState.Maximized).Save(placementFile); }
        // A nicety: the window opens at the first start's place instead.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Write($"window placement not kept: {e.Message}"); }
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
}
