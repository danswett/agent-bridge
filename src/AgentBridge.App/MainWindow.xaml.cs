using AgentBridge.App.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace AgentBridge.App;

public sealed partial class MainWindow : Window
{
    bool active;

    internal MainWindow(MainViewModel viewModel, bool selfTest)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        CurrentHandle = Handle;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AgentBridge.ico"));
        AppWindow.Resize(new SizeInt32(selfTest ? 1100 : 960, selfTest ? 1400 : 900));
        AppWindow.Closing += (_, e) => CloseRequested?.Invoke(e);
        Activated += (_, e) => active = e.WindowActivationState != WindowActivationState.Deactivated;
        UsagePane.ViewModel = viewModel.Usage;
        if (viewModel.Settings is { } s) { SettingsPane.ViewModel = s; }
        Nav.SelectedItem = SessionsItem;
        viewModel.OpenRequested += Reveal;
    }

    public MainViewModel ViewModel { get; }
    public nint Handle { get; }
    // The window file pickers belong to: an unpackaged app must say which.
    internal static nint CurrentHandle { get; private set; }
    public event Action<AppWindowClosingEventArgs>? CloseRequested;

    // In front and visible: a toast would only repeat what the window shows.
    public bool IsInFront => active && AppWindow.IsVisible;

    internal ItemsControl CardList => Cards;
    internal FrameworkElement RootElement => Root;

    public void ShowInFront()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) { p.Restore(); }
        Activate();
        TrayIcon.SetForegroundWindow(Handle);
    }

    public void HideToTray() => AppWindow.Hide();

    internal void ShowPage(string tag) =>
        Nav.SelectedItem = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>().First(i => (string)i.Tag == tag);

    void OnNavigate(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        SessionsPane.Visibility = Ui.Show(tag == "sessions");
        UsagePane.Visibility = Ui.Show(tag == "usage");
        SettingsPane.Visibility = Ui.Show(tag == "settings");
        if (tag == "settings") { SettingsPane.Opened(); }
    }

    // A toast or the tray asked for a session: its card, on screen.
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
}
