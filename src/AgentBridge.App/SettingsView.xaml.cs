using AgentBridge.App.Core;
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

    public static string HaNote(string? why) => $"{why}. Agent Bridge only watches while it runs: answering, replying and ending sessions stay with agent-ha-bridge until you move over.";
    public static string HostLine(string version, string profile) => version.Length > 0 ? $"Version {version} · profile {profile}" : $"Profile {profile}";
    public static string AppLine(string version, string installed) => $"App {version} · {installed}";

    async void OnStart(object sender, RoutedEventArgs e) { if (viewModel is { } v) { await v.StartHostAsync(); v.RefreshLog(); } }
    async void OnStop(object sender, RoutedEventArgs e) { if (viewModel is { } v) { await v.StopHostAsync(); v.RefreshLog(); } }
    async void OnRestart(object sender, RoutedEventArgs e) { if (viewModel is { } v) { await v.RestartHostAsync(); v.RefreshLog(); } }
    async void OnSignIn(object sender, RoutedEventArgs e) { if (viewModel is { } v) { await v.SignInAsync(); } }
    async void OnCancelSignIn(object sender, RoutedEventArgs e) { if (viewModel is { } v) { await v.CancelSignInAsync(); } }
    async void OnSaveName(object sender, RoutedEventArgs e) { if (viewModel is { } v) { await v.SaveNameAsync(); } }
    async void OnCheckUpdates(object sender, RoutedEventArgs e) { if (viewModel is { } v) { await v.CheckForUpdatesAsync(); } }
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
