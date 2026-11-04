using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    public static string HostSection => $"{Product.HostName} on this machine";
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
            Content = "Open this CLI's own sign-in flow in a visible terminal, using the same agent home as new sessions. Complete or cancel in that window, including any browser approval. Agent Bridge does not collect credentials. This can change the account used by future sessions, but does not confirm access or entitlement, change hook or folder trust, or sign the bridge in to its relay.",
            PrimaryButtonText = "Open sign-in",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) { await v.SignInAgentAsync(id); }
    }

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
        if (viewModel is not { CanManageAgents: true } v || sender is not Button { Tag: string id }) { return; }
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = remove ? "Remove this agent's connection?" : "Connect this agent?",
            Content = remove
                ? "Remove only this installation's hook registrations and owned guidance blocks. Other settings, instructions, CLI sign-in and hook trust stay unchanged. Existing sessions keep their hooks until they end."
                : "Register hooks and guidance owned by this installation, preserving other settings and instructions. Copilot and Agency share their hooks. You will need to restart the host and start a new agent session. Codex also asks you to trust its hooks at this machine. Nothing restarts or accepts trust automatically.",
            PrimaryButtonText = remove ? "Remove connection" : "Set up hooks",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) { await v.SetUpAgentAsync(id, remove); }
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
