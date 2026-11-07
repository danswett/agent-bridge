using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

static partial class SelfTest
{
    // #516: an update downloaded waits for the person. The banner over every page offers Restart
    // to update, and with the demo's sessions waiting on the person, pressing it first says what a
    // restart would interrupt: Not now restarts nothing, and the banner stays. The demo never
    // updates, so a stand-in release is ready here and the restart only counted.
    static async Task CheckUpdateBanner(MainWindow window, MainViewModel main, Action<bool, string> check, string outFile)
    {
        var restarts = 0;
        var host = main.MachineOf(null)!.Host;
        var before = main.AppUpdate;
        // As AutoUpdater.RestartToUpdateAsync asks: last, and only with a session here busy.
        main.AppUpdate = new AppUpdateViewModel(() => "0.1.999", async confirm =>
        {
            if (RestartWarning.For(host) is { } warning && confirm is not null && !await confirm(warning)) { return AutoUpdater.NotNow; }
            restarts++;
            return null;
        });
        try
        {
            var bar = ById<InfoBar>(window.RootElement, "UpdateReady");
            var shown = await Until(() => bar is { IsOpen: true } && ById<Button>(bar, "RestartToUpdate") is { IsEnabled: true }, TimeSpan.FromSeconds(5));
            check(shown && bar!.Message == "Version 0.1.999 is ready. Restart to update.",
                $"shows a downloaded update in a banner over the page, with Restart to update ({bar?.Message})");
            var button = bar is null ? null : ById<Button>(bar, "RestartToUpdate");
            check(button is not null && AutomationProperties.GetName(button) == "Restart to update" && AutomationProperties.GetAutomationId(button) == "RestartToUpdate", "names the banner's action Restart to update");
            // ui.md §9.1 A6: the bar's words changing are said, politely (Creative, #719).
            check(bar is not null && AutomationProperties.GetLiveSetting(bar) == AutomationLiveSetting.Polite, "makes the update bar a polite live region");
            if (bar is not null) { await Picture(bar, Path.ChangeExtension(outFile, ".update-ready.png")); }
            if (button is null) { return; }

            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
            ContentDialog? Dialog() => VisualTreeHelper.GetOpenPopupsForXamlRoot(window.RootElement.XamlRoot)
                .Select(p => p.Child).Where(c => c is not null)
                .SelectMany(c => c is ContentDialog d ? (IEnumerable<ContentDialog>)[d] : Find<ContentDialog>(c)).FirstOrDefault();
            var asked = await Until(() => Dialog() is not null, TimeSpan.FromSeconds(5));
            var dialog = Dialog();
            check(asked && dialog is { Title: "Restart to update now?", PrimaryButtonText: "Restart to update", CloseButtonText: "Not now", DefaultButton: ContentDialogButton.Close }
                && dialog.Content is string words && words.Contains("waiting on you", StringComparison.Ordinal),
                $"says what a restart would interrupt before restarting, with sessions waiting ({dialog?.Content})");
            if (dialog is not null) { await Picture(dialog, Path.ChangeExtension(outFile, ".update-warning.png")); }
            dialog?.Hide();
            var closed = await Until(() => Dialog() is null, TimeSpan.FromSeconds(5));
            await Task.Delay(300);
            check(closed && restarts == 0 && bar is { IsOpen: true, Message: "Version 0.1.999 is ready. Restart to update." } && button.IsEnabled,
                $"restarts nothing on Not now, and keeps the banner's offer and its action ({bar?.Message})");
        }
        finally { main.AppUpdate = before; }
    }
}
