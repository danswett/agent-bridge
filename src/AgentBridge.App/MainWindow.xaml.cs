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
    // Whether the window was maximised before it was last minimised: what it comes back as.
    bool wasMaximized;
    // Kept: the colour change is raised only while the UISettings that took the handler lives.
    readonly Windows.UI.ViewManagement.UISettings uiSettings = new();
    // The UI thread's, taken while on it: the change arrives on another.
    readonly DispatcherQueue ui = DispatcherQueue.GetForCurrentThread();
    bool closed;

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
        // system's, as tall as the bar and in the app's theme (PaintCaptionButtons).
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        PaintCaptionButtons();
        Root.ActualThemeChanged += (_, _) => PaintCaptionButtons();
        // Raised on a thread-pool thread: painted on the UI thread, as everything else is.
        uiSettings.ColorValuesChanged += OnColorValuesChanged;
        Closed += (_, _) =>
        {
            closed = true;
            uiSettings.ColorValuesChanged -= OnColorValuesChanged;
        };

        if (selfTest) { AppWindow.Resize(new SizeInt32(1100, 1400)); }
        else { Place(); }
        KeepSmallest();
        Root.Loaded += (_, _) => { if (Root.XamlRoot is { } x) { x.Changed += (_, _) => KeepSmallest(); } };
        Root.SizeChanged += (_, e) => FitSearch(e.NewSize.Width);
        AppWindow.Changed += OnWindowChanged;
        // Shown for the first time by Activate, which AppWindow.Changed doesn't always report.
        VisibilityChanged += (_, _) => SyncSessionsOnScreen();

        AppWindow.Closing += (_, e) => CloseRequested?.Invoke(e);
        Activated += (_, e) =>
        {
            active = e.WindowActivationState != WindowActivationState.Deactivated;
            SyncSessionsOnScreen();
            if (active && maximizeWhenShown && AppWindow.Presenter is OverlappedPresenter p)
            {
                maximizeWhenShown = false;
                p.Maximize();
            }
        };
        UsagePane.ViewModel = viewModel.Usage;
        LaunchPane.ViewModel = launch;
        if (viewModel.Settings is { } s) { SettingsPane.ViewModel = s; }
        SessionsView.ViewModel = viewModel;
        SessionsView.Launch = launch;
        SessionsView.NewRequested += () => ShowPage("launch");
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

    internal SessionsPage Sessions => SessionsView;
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
        SessionsView.Visibility = Ui.Show(tag == "sessions");
        SyncSessionsOnScreen();
        MachinesPage.Visibility = Ui.Show(tag == "machines");
        UsagePane.Visibility = Ui.Show(tag == "usage");
        LaunchPane.Visibility = Ui.Show(tag == "launch");
        if (tag == "launch") { Launch.Opened(); } else { Launch.Closed(); }
        SettingsPane.Visibility = Ui.Show(tag == "settings");
        if (tag == "settings") { SettingsPane.Opened(); }
    }

    // A machine's row on the Machines page: its sessions, on the Sessions page.
    void OnShowMachineSessions(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MachineRowViewModel row || ViewModel.Machines.FirstOrDefault(c => c.Key == row.Key) is not { } choice) { return; }
        ViewModel.SelectedMachine = choice;
        ShowPage("sessions");
    }

    static MachineRowViewModel? RowOf(object sender) => (sender as FrameworkElement)?.Tag as MachineRowViewModel;
    // This machine's updates are Settings': its check, progress, outcome and Update and restart.
    void OnCheckHere(object sender, RoutedEventArgs e)
    {
        ShowPage("settings");
        // Not while Settings is busy with another operation, as its own Check is disabled then: two
        // at once would each clear Busy as it ended, with the other still running.
        if (ViewModel.Settings is { Busy: false } s) { _ = s.CheckForUpdatesAsync(); }
    }
    void OnCheckMachine(object sender, RoutedEventArgs e) => RowOf(sender)?.Update?.Check();
    void OnInstallMachine(object sender, RoutedEventArgs e) => RowOf(sender)?.Update?.Install();
    void OnForgetMachine(object sender, RoutedEventArgs e) => RowOf(sender)?.Offline?.Press();

    // The Sessions page's tool history is asked for only while it is on screen: its page shown, in
    // a window that is neither hidden in the tray nor minimised.
    void SyncSessionsOnScreen()
    {
        if (SessionsView.Layout is { } layout)
        {
            layout.OnScreen = SessionsView.Visibility == Visibility.Visible && AppWindow.IsVisible
                && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        }
    }
    // A toast, the tray or the search box asked for a session: it is MainViewModel.Selected
    // already, so the Sessions page shows it, its row scrolled into view.
    void Reveal(SessionCardViewModel card) => Nav.SelectedItem = SessionsItem;

    // --- Search (ui.md §7, §9): every machine's sessions, opened as a toast opens one. ---

    void OnFocusSearch(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        FocusSearch();
    }

    // Programmatic, not Keyboard (#256): WinUI opens a control's tooltip when it takes keyboard
    // focus, so Ctrl+K opened "Search sessions (Ctrl+K)" under the box, naming the keys just
    // pressed. The box still shows it has focus and takes typing; Tab into it still shows the hint.
    internal bool FocusSearch() => SearchBox.Focus(FocusState.Programmatic);

    void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) { SearchFor(sender.Text); }
    }

    // Up to 400 px, centred; narrower with the window, so the mark, the name and the caption buttons
    // keep their room down to the smallest window.
    void FitSearch(double width) => SearchBox.Width = Math.Clamp(width - 400, 120, 400);

    internal void SearchFor(string text) => SearchBox.ItemsSource = ViewModel.Search(text);

    // Enter, a click or a tap opens the suggestion picked, or the first one found. Not on
    // SuggestionChosen: that comes as the arrow keys move through the suggestions, and opening
    // then would leave the keyboard no way past the first.
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

    // --- The caption buttons (#240) ---

    // In the window's theme, set here on the UI thread, and the system's own in high contrast. Not
    // through AppWindow.TitleBar.PreferredTheme: for that, Windows App SDK repaints the caption
    // buttons from UISettings.ColorValuesChanged on a thread-pool thread (AppWindowTitleBar::
    // OnUISettingsColorValuesChanged, CTitleBar::UpdateColors, PaintTitle), racing the UI thread's
    // own repaint of them and the window's teardown. A high-contrast switch then ended the app
    // in dcomp.dll: an access violation on x64, a CFG fail fast on Arm64, with no managed
    // exception. With the theme left Legacy, that handler paints nothing.
    void OnColorValuesChanged(Windows.UI.ViewManagement.UISettings sender, object args) =>
        ui.TryEnqueue(PaintCaptionButtons);

    internal void PaintCaptionButtons()
    {
        if (closed) { return; }
        var bar = AppWindow.TitleBar;
        var colours = CaptionColours(Root.ActualTheme);
        bar.ButtonForegroundColor = colours?.Text;
        bar.ButtonHoverForegroundColor = colours?.Text;
        bar.ButtonPressedForegroundColor = colours?.Text;
        bar.ButtonInactiveForegroundColor = colours?.Inactive;
        bar.ButtonHoverBackgroundColor = colours?.Hover;
        bar.ButtonPressedBackgroundColor = colours?.Pressed;
    }

    // The design's text and fills for a theme (Design/DesignResources.xaml); none in high
    // contrast, where the system's colours are the right ones.
    internal static (Windows.UI.Color Text, Windows.UI.Color Inactive, Windows.UI.Color Hover, Windows.UI.Color Pressed)? CaptionColours(ElementTheme theme)
    {
        if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast) { return null; }
        var design = Application.Current.Resources.MergedDictionaries.FirstOrDefault(d => d.Source?.ToString().EndsWith(Design.DesignKeys.Source["ms-appx:///".Length..], StringComparison.OrdinalIgnoreCase) == true);
        if (design?.ThemeDictionaries.TryGetValue(theme == ElementTheme.Dark ? "Dark" : "Light", out var d) != true || d is not ResourceDictionary themed) { return null; }
        Windows.UI.Color? Of(string key) => themed.TryGetValue(key, out var b) && b is Microsoft.UI.Xaml.Media.SolidColorBrush s ? s.Color : null;
        // A caption glyph's alpha is ignored (only the backgrounds take it), so the translucent
        // tertiary text is laid over the shell the title bar sits on, as it would be drawn.
        return Of("AbText") is { } text && Of("AbText3") is { } inactive && Of("AbShell") is { } shell && Of("AbSubtle") is { } hover && Of("AbSelected") is { } pressed
            ? (Over(text, shell), Over(inactive, shell), hover, pressed) : null;
    }

    static Windows.UI.Color Over(Windows.UI.Color top, Windows.UI.Color under)
    {
        byte Mix(byte t, byte u) => (byte)Math.Round((t * top.A + u * (255 - top.A)) / 255.0);
        return new Windows.UI.Color { A = 255, R = Mix(top.R, under.R), G = Mix(top.G, under.G), B = Mix(top.B, under.B) };
    }

    // --- Size and place (#180): down to 360×600, and back where it was left. ---

    double Scale => GetDpiForWindow(Handle) is var dpi and > 0 ? dpi / 96.0 : 1;

    // The smallest window ui.md allows, at the scale of the display the window is on now.
    void KeepSmallest()
    {
        if (AppWindow.Presenter is not OverlappedPresenter p) { return; }
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        var (w, h) = area is null ? WindowPlacement.Smallest(Scale) : WindowPlacement.Smallest(new Display(new PixelRect(area.WorkArea.X, area.WorkArea.Y, area.WorkArea.Width, area.WorkArea.Height), Scale));
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
        // Shown, hidden, minimised or restored: the history is asked for only while it can be seen.
        SyncSessionsOnScreen();
        if (!args.DidPositionChange && !args.DidSizeChange && !args.DidPresenterChange) { return; }
        if (args.DidPositionChange || args.DidSizeChange) { KeepSmallest(); }
        if (AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Minimized } shown) { wasMaximized = shown.State == OverlappedPresenterState.Maximized; }
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

    // Also on hiding to the tray and on quitting.
    internal void SavePlacement()
    {
        if (placementFile is null || normal is not { } r || AppWindow.Presenter is not OverlappedPresenter p) { return; }
        // Minimised, it keeps the place and state it had before, which it comes back to.
        var maximized = p.State == OverlappedPresenterState.Minimized ? wasMaximized : p.State == OverlappedPresenterState.Maximized;
        // A window started hidden in the tray keeps the maximised state it has yet to be shown in.
        try { new WindowPlacement(r.X, r.Y, r.Width, r.Height, maximizeWhenShown || maximized).Save(placementFile); }
        // A nicety: the window opens at the first start's place instead.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Write($"window placement not kept: {e.Message}"); }
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
}
