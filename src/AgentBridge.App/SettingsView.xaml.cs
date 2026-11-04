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
            viewModel = value;
            Bindings.Update();
        }
    }

    // The page came into view: the log is read then, not on every host event.
    public void Opened() => viewModel?.RefreshLog();

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

    async void OnInstallAgent(object sender, RoutedEventArgs e)
    {
        if (viewModel is not { CanInstallAgents: true } v || sender is not Button { Tag: string id }) { return; }
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
                : "Register hooks and guidance owned by this installation, preserving other settings and instructions. Copilot and Agency share their hooks. You will need to restart the host and start a new agent session. Codex also asks you to trust its hooks at this machine. Nothing restarts or accepts trust automatically.",
            PrimaryButtonText = remove ? "Remove connection" : row.SetUpVerb ?? "Set up",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) { return; }
        var pending = v.SetUpAgentAsync(id, remove);
        Announce(id);
        await pending;
        Announce(id);
    }

    // The row's status is a polite live region, but its rows are drawn afresh from each status, so
    // the TextBlock that says it is new and Narrator would not hear a change: the change is raised
    // on it once it is drawn, for "Setting up…" and again for the result.
    void Announce(string id) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
    {
        AgentList.UpdateLayout();
        if (AgentStatusOf(id) is { Text.Length: > 0 } status
            && (FrameworkElementAutomationPeer.FromElement(status) ?? FrameworkElementAutomationPeer.CreatePeerForElement(status)) is { } peer)
        {
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            Announced.Add((id, status.Text));
        }
    });

    // What was raised for Narrator, by agent, for the self-test to see.
    internal List<(string Id, string Text)> Announced { get; } = [];

    internal ItemsControl AgentRows => AgentList;

    // A row's status in its tone, a failure in Danger, from the brushes of the theme the row is drawn
    // in: Ui.ToneBrush gives the app's, and a window can be drawn in the other.
    void OnAgentStatusLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBlock status) { return; }
        PaintStatus(status, null);
        status.ActualThemeChanged -= PaintStatus;
        status.ActualThemeChanged += PaintStatus;
    }

    static void PaintStatus(FrameworkElement sender, object? _)
    {
        if (sender is TextBlock { DataContext: AgentReadinessRow row } status) { status.Foreground = Ui.ThemedBrush(Ui.ToneKey(row.StatusTone), status.ActualTheme); }
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
    async void OnInstallUpdate(object sender, RoutedEventArgs e) { if (viewModel is { } v) { await v.InstallUpdateAsync(); } }
    void OnRefreshLog(object sender, RoutedEventArgs e) => viewModel?.RefreshLog();

    // The device code goes to the clipboard and GitHub's page opens: the person pastes and
    // approves there, and the host finishes signing in by itself.
    async void OnOpenSignIn(object sender, RoutedEventArgs e)
    {
        if (viewModel is not { SignInCode: { } code, SignInUri: { } uri }) { return; }
        try
        {
            var data = new DataPackage();
            data.SetText(code);
            Clipboard.SetContent(data);
            if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps) { await Windows.System.Launcher.LaunchUriAsync(u); }
        }
        // async void: an escape would end the app. The code and the page are on screen anyway.
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { }
    }
}
