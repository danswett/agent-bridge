using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace AgentBridge.App;

public sealed partial class SettingsView : UserControl
{
    SettingsViewModel? viewModel;

    public SettingsView() => InitializeComponent();

    public SettingsViewModel ViewModel
    {
        get => viewModel!;
        set
        {
            if (viewModel is not null) { viewModel.UpdateStatusAnnounced -= AnnounceUpdate; viewModel.PropertyChanged -= OnViewModelChanged; }
            viewModel = value;
            if (viewModel is not null) { viewModel.UpdateStatusAnnounced += AnnounceUpdate; viewModel.PropertyChanged += OnViewModelChanged; }
            Bindings.Update();
        }
    }

    void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is "" or nameof(SettingsViewModel.AgentOffers)) { AnnounceOffers(); }
    }

    // After the binding has put the new words in.
    void AnnounceUpdate() =>
        DispatcherQueue.TryEnqueue(() => Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(UpdateStatusText)?.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged));

    // The page came into view: the log is read then, not on every host event.
    public void Opened()
    {
        viewModel?.RefreshLog();
        AnnounceOffers();
    }

    // A card for an agent installed later is said once, politely, and only while Settings is open
    // (ui.md §9): one found meanwhile is said when the page next opens. Its group is drawn afresh
    // from each status, so the change is raised on it once it is drawn, as a row's status is.
    readonly HashSet<string> offersSaid = new(StringComparer.Ordinal);
    // What was raised, as Narrator names it, for the self-test to see.
    internal List<string> OffersAnnounced { get; } = [];

    void AnnounceOffers()
    {
        if (viewModel is null) { return; }
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            OfferList.UpdateLayout();
            var cards = Descendants(OfferList).OfType<AgentOfferGroup>().Select(g => (Group: g, Name: AutomationProperties.GetName(g))).ToList();
            // A card that is gone is forgotten, so one that returns at the same version (after a failed
            // setup, say) is said again.
            offersSaid.IntersectWith(cards.Select(c => c.Name));
            if (Visibility != Visibility.Visible) { return; }
            foreach (var (group, name) in cards)
            {
                if (name.Length == 0 || !offersSaid.Add(name)) { continue; }
                if ((FrameworkElementAutomationPeer.FromElement(group) ?? FrameworkElementAutomationPeer.CreatePeerForElement(group)) is { } peer)
                {
                    peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                    OffersAnnounced.Add(peer.GetName());
                }
            }
        });
    }

    internal ItemsControl Offers => OfferList;

    // Not now. The card is gone once the page is drawn again, so focus goes to the next card's Set
    // up, or else the first control of the agents below (ui.md §9, A4).
    void OnNotNowAgent(object sender, RoutedEventArgs e)
    {
        if (viewModel is not { } v || sender is not Button { Tag: string id }) { return; }
        v.DismissAgentOffer(id);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            OfferList.UpdateLayout();
            var next = Descendants(OfferList).OfType<Button>().FirstOrDefault(b => AutomationProperties.GetAutomationId(b).StartsWith("AgentOfferSetUp_", StringComparison.Ordinal));
            (next ?? CheckAgentsButton).Focus(FocusState.Programmatic);
        });
    }

    public static string HaNote(string? why) => $"{why}. {Product.Name} only watches while it runs: answering, replying and ending sessions stay with agent-ha-bridge until you move over.";
    // The section is already about the host, so it needs no product name (brand.md N1).
    public static string HostSection => "Host on this machine";
    public static string HostLine(string version, string profile) => version.Length > 0 ? $"Version {version} · profile {profile}" : $"Profile {profile}";
    public static string AppLine(string version, string installed) => $"App {version} · {installed}";

    async void OnStart(object sender, RoutedEventArgs e) { if (viewModel is { } v) { await v.StartHostAsync(); v.RefreshLog(); } }
    async void OnStop(object sender, RoutedEventArgs e) { if (viewModel is { } v) { await v.StopHostAsync(); v.RefreshLog(); } }
    async void OnRestart(object sender, RoutedEventArgs e) { if (viewModel is { } v) { await v.RestartHostAsync(); v.RefreshLog(); } }
    async void OnSignIn(object sender, RoutedEventArgs e) { if (viewModel is { } v) { await v.SignInAsync(); } }
    async void OnCancelSignIn(object sender, RoutedEventArgs e) { if (viewModel is { } v) { await v.CancelSignInAsync(); } }
    async void OnSaveName(object sender, RoutedEventArgs e) { if (viewModel is { } v) { await v.SaveNameAsync(); } }
    async void OnCheckUpdates(object sender, RoutedEventArgs e) { if (viewModel is { } v) { await v.CheckForUpdatesAsync(); } }
    async void OnCheckAgents(object sender, RoutedEventArgs e) { if (viewModel is { } v) { await v.CheckAgentsAsync(); } }
    async void OnSetUpAgent(object sender, RoutedEventArgs e) => await ConfirmAgentAsync(sender, remove: false);
    async void OnRemoveAgent(object sender, RoutedEventArgs e) => await ConfirmAgentAsync(sender, remove: true);
    async void OnCancelAgentInstall(object sender, RoutedEventArgs e) { if (viewModel is { } v) { await v.CancelAgentInstallAsync(); } }

    async void OnSignInAgent(object sender, RoutedEventArgs e)
    {
        if (viewModel is not { CanSignInAgents: true } v || sender is not Button { Tag: string id }) { return; }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Open {v.Agents.FirstOrDefault(a => a.Id == id)?.Label ?? id} sign-in?",
            Content = $"Open this CLI's own sign-in flow in a visible terminal, using the same agent home as new sessions. Complete or cancel in that window, including any browser approval. {Product.Name} does not collect credentials. This can change the account used by future sessions, but does not confirm access or entitlement, change hook or folder trust, or sign the bridge in to its relay.",
            PrimaryButtonText = "Open sign-in",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) { await v.SignInAgentAsync(id); }
    }

    async void OnInstallAgent(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) { return; }
        // While its installation runs, a press does nothing (ui.md §9.1 A4).
        var row = viewModel?.Agents.FirstOrDefault(a => a.Id == id);
        if (viewModel is not { CanInstallAgents: true } v || row is not { CanInstall: true }) { return; }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Install {v.Agents.FirstOrDefault(a => a.Id == id)?.Label ?? id} and its required runtimes?",
            Content = "Use the Windows Package Manager community source and accept its source and package agreements. Required runtimes may request Windows administrator approval; declining leaves setup incomplete. Existing CLIs are not upgraded. No hook setup, sign-in, folder trust or restart is requested. Agency is installed through your organization.",
            PrimaryButtonText = "Install CLI and runtimes",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) { await v.InstallAgentAsync(id); }
    }

    async Task ConfirmAgentAsync(object sender, bool remove)
    {
        if (viewModel is not { CanManageAgents: true } v || sender is not Button { Tag: string id }
            || v.Agents.FirstOrDefault(a => a.Id == id) is not { } row) { return; }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = remove ? row.RemoveQuestion : row.SetUpQuestion,
            Content = remove
                ? "Remove only this installation's hook registrations and owned guidance blocks. Other settings, instructions, CLI sign-in and hook trust stay unchanged. Existing sessions keep their hooks until they end."
                : "Register hooks and guidance owned by this installation, preserving other settings and instructions. Copilot CLI and Agency share their hooks. You will need to restart the host and start a new agent session. Codex also asks you to trust its hooks at this machine. Nothing restarts or accepts trust automatically.",
            PrimaryButtonText = remove ? "Remove connection" : row.SetUpVerb ?? "Set up",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) { return; }
        announcements.Begin(id);
        var pending = v.SetUpAgentAsync(id, remove);
        Announce(id, StatusOf(v, id));
        await pending;
        Announce(id, StatusOf(v, id));
    }

    static string StatusOf(SettingsViewModel v, string id) => v.Agents.FirstOrDefault(a => a.Id == id)?.Status ?? "";

    readonly AgentAnnouncements announcements = new();

    // The row's status is a polite live region, but its rows are drawn afresh from each status, so
    // the TextBlock that says it is new and Narrator would not hear a change: the change is raised
    // on it once it is drawn, for "Setting up…" and again for the result, unless the row has moved
    // on from what it said when the raise was queued (AgentAnnouncements).
    internal void Announce(string id, string expected) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
    {
        AgentList.UpdateLayout();
        if (AgentStatusOf(id) is { } status && announcements.Take(id, expected, status.Text)
            && (FrameworkElementAutomationPeer.FromElement(status) ?? FrameworkElementAutomationPeer.CreatePeerForElement(status)) is { } peer)
        {
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            Announced.Add((id, peer.GetName()));
        }
    });

    // What was raised for Narrator, by agent, as Narrator names it, for the self-test to see.
    internal List<(string Id, string Text)> Announced { get; } = [];

    internal ItemsControl AgentRows => AgentList;

    // A row's status in its tone, a failure in Danger, styled with a ThemeResource (the styles in
    // SettingsView.xaml): it follows the theme the row is drawn in, which Ui.ToneBrush (the app's)
    // doesn't, and high contrast, which can come on without ActualTheme changing.
    void OnAgentStatusLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBlock { DataContext: AgentReadinessRow row } status) { status.Style = (Style)Resources[$"AgentStatus{Ui.ToneKey(row.StatusTone)}"]; }
    }

    // A row's status line as drawn (the self-test reads it too).
    internal TextBlock? AgentStatusOf(string id) => Descendants(AgentList).OfType<TextBlock>()
        .FirstOrDefault(t => AutomationProperties.GetAutomationId(t) == $"AgentStatus_{id}");

    static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            yield return child;
            foreach (var inner in Descendants(child)) { yield return inner; }
        }
    }
    async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (viewModel is not { Busy: false } v) { return; }
        // Told what a restart would interrupt, just before it does (#516). It shows, never throws.
        await v.InstallUpdateAsync(RestartDialog.For(XamlRoot));
    }
    void OnRefreshLog(object sender, RoutedEventArgs e) => viewModel?.RefreshLog();

    // The device code goes to the clipboard and GitHub's page opens: the person pastes and
    // approves there, and the host finishes signing in by itself.
    async void OnOpenSignIn(object sender, RoutedEventArgs e)
    {
        if (viewModel is not { SignInCode: { } code, SignInUri: { } uri }) { return; }
        try
        {
            ClipboardText.Put(code);
            if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps) { await Windows.System.Launcher.LaunchUriAsync(u); }
        }
        // async void: an escape would end the app. The code and the page are on screen anyway.
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { }
    }
}
