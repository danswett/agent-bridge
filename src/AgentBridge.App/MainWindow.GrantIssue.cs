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
    Action? openGrant;

    internal Task RefreshGrantListAsync() => GrantsPage.ViewModel?.RefreshAsync() ?? Task.CompletedTask;

    internal void BindGrantReceiver(IGrantIssueReceiver receiver)
    {
        if (openGrant is { } previous) { GrantsPage.NewGrantRequested -= previous; }
        else { Closed += OnGrantsClosed; }
        openGrant = () => _ = ShowGrantIssueAsync(receiver);
        GrantsPage.NewGrantRequested += openGrant;
        GrantsPage.ViewModel = new GrantListViewModel(receiver);
    }

    void OnGrantsClosed(object sender, WindowEventArgs args)
    {
        if (openGrant is { } open) { GrantsPage.NewGrantRequested -= open; }
        openGrant = null;
    }

    async Task ShowGrantIssueAsync(IGrantIssueReceiver receiver)
    {
        if (grantIssueOpen || Root.XamlRoot is not { } root) { return; }
        var focus = FocusManager.GetFocusedElement(root) as Control;
        grantIssueOpen = true;
        try
        {
            var model = new GrantIssueViewModel(receiver);
            var view = new GrantIssueView { ViewModel = model };
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = "New grant",
                Content = view,
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close
            };
            dialog.Closing += (_, args) => args.Cancel = view.IsSubmitting;
            await dialog.ShowAsync();
            if ((model.Active || model.State == "Unconfirmed") && GrantsPage.ViewModel is { } list) { await list.RefreshAsync(); }
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        { AppLog.Write($"grants: cannot open issue dialog ({error.GetType().Name})"); }
        finally
        {
            grantIssueOpen = false;
            if (focus is { IsLoaded: true, IsEnabled: true, Visibility: Visibility.Visible })
            { focus.Focus(FocusState.Programmatic); }
        }
    }
}
