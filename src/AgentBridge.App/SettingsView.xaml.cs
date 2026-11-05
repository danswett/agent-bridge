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

    void OnAddWorkspace(object sender, RoutedEventArgs e) => viewModel?.AddWorkspace();
    async void OnChooseWorkspace(object sender, RoutedEventArgs e)
    {
        if (viewModel is not { CanEditLaunchSettings: true } v) { return; }
        await v.ChooseWorkspaceAsync(async () =>
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, MainWindow.CurrentHandle);
            picker.FileTypeFilter.Add("*");
            var selected = await picker.PickSingleFolderAsync();
            return selected is null ? null : (selected.Name, selected.Path);
        });
    }
    void OnRemoveWorkspace(object sender, RoutedEventArgs e) { if (sender is Button { Tag: string path }) { viewModel?.RemoveWorkspace(path); } }
    async void OnReloadLaunchSettings(object sender, RoutedEventArgs e) { if (viewModel is { } v) { await v.ReloadLaunchSettingsAsync(); } }

    async void OnSaveLaunchSettings(object sender, RoutedEventArgs e)
    {
        if (viewModel is not { CanSaveLaunchSettings: true } v) { return; }
        var folders = v.Workspaces.Count == 0 ? "No folders will be approved." : string.Join("\n", v.Workspaces.Select(w => $"{w.Label}: {w.Path}{(w.Isolate ? " (isolated worktrees)" : "")}"));
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Save launch authorizations and defaults?",
            Content = new ScrollViewer
            {
                MaxHeight = 400,
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"{folders}\n\nLaunching: {(v.LaunchEnabled ? "enabled" : "off")}. Default agent: {v.DefaultAgent}. Default workspace: {(v.DefaultWorkspace.Length == 0 ? "none" : v.DefaultWorkspace)}.\n\n"
                        + (v.UnrestrictedPermissions ? "WARNING: supported agents will bypass permission prompts by default.\n\n" : "Standard CLI permission prompts remain.\n\n")
                        + "Approval is for these exact folders, or managed isolated worktrees, not their subfolders. CLI folder and hook trust stay separate. Nothing restarts or accepts trust automatically; restart the host when ready to apply these changes.",
                },
            },
            PrimaryButtonText = "Save launch settings",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) { await v.SaveLaunchSettingsAsync(confirmed: true); }
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
