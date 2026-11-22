using System.Runtime.InteropServices;
using AgentBridge.App.Core;
using AgentBridge.App.Core.Grants;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AgentBridge.App;

public sealed partial class MainWindow
{
    bool grantIssueOpen;

    internal void BindGrantReceiver(IGrantIssueReceiver receiver)
    {
        Action open = () => _ = ShowGrantIssueAsync(receiver);
        GrantsPage.NewGrantRequested += open;
        GrantsPage.ViewModel = new GrantListViewModel(receiver);
        Closed += (_, _) => GrantsPage.NewGrantRequested -= open;
    }

    async Task ShowGrantIssueAsync(IGrantIssueReceiver receiver)
    {
        if (grantIssueOpen || Root.XamlRoot is not { } root) { return; }
        var focus = FocusManager.GetFocusedElement(root) as Control;
        grantIssueOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = "New grant",
                Content = new GrantIssueView { ViewModel = new GrantIssueViewModel(receiver) },
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close
            };
            await dialog.ShowAsync();
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        { AppLog.Write($"grants: cannot open issue dialog: {error.Message}"); }
        finally
        {
            grantIssueOpen = false;
            if (focus is { IsLoaded: true, IsEnabled: true, Visibility: Visibility.Visible })
            { focus.Focus(FocusState.Programmatic); }
        }
    }
}
