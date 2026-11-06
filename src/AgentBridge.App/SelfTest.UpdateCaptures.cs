using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

// The update captures Creative's acceptance of #719 asks for (6643004091): the bar over every page
// ready, restarting and failed, and Settings' updates (When an update is ready among them) with a release ready, in Light and Dark,
// and in high contrast on CI (CheckHighContrast). The demo never updates, so a stand-in release is
// ready here, and the restart is held, then refused, rather than carried out.
static partial class SelfTest
{
    sealed class StandInUpdater : IUpdater
    {
        public string? InstalledVersion => "0.1.0";
        public string? Ready => "0.1.999";
        public string Source => "self-test";
        public Task<string?> CheckAsync() => Task.FromResult<string?>("0.1.999");
        public Task DownloadAsync(Action<int> progress) => Task.CompletedTask;
        public void ApplyAndRestart(string version) => throw new InvalidOperationException("the self-test never updates");
    }

    static async Task CaptureUpdate(MainWindow window, MainViewModel main, Action<bool, string> check, string outFile, bool contrast = false)
    {
        var where = contrast ? " in high contrast" : "";
        string File(string what, string look) => Path.ChangeExtension(outFile, $".update-{what}{look}.png");
        // What an element sits on: the first opaque fill under it, else the theme's shell (as the
        // sign-in captures do), never a colour of the self-test's own.
        Windows.UI.Color Under(DependencyObject at)
        {
            for (var up = VisualTreeHelper.GetParent(at); up is not null; up = VisualTreeHelper.GetParent(up))
            {
                var fill = up switch { Panel p => p.Background, Border b => b.Background, Control c => c.Background, _ => null };
                if (fill is SolidColorBrush { Color.A: 255 } solid) { return solid.Color; }
            }
            var theme = window.RootElement.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
            return Application.Current.Resources.MergedDictionaries
                .Select(d => d.ThemeDictionaries.TryGetValue(theme, out var td) && td is ResourceDictionary rd && rd.TryGetValue("AbShell", out var b) && b is SolidColorBrush s ? s.Color : (Windows.UI.Color?)null)
                .FirstOrDefault(c => c is not null) ?? default;
        }
        async Task Looks(Func<string, Task> take)
        {
            if (contrast) { await Task.Delay(300); await take(".hc"); return; }
            foreach (var (theme, look) in new[] { (ElementTheme.Light, ""), (ElementTheme.Dark, ".dark") })
            {
                window.RootElement.RequestedTheme = theme;
                await Task.Delay(300);
                await take(look);
            }
            window.RootElement.RequestedTheme = ElementTheme.Light;
        }

        const string refused = "another profile's host (pid 77) runs from this installation";
        var before = main.AppUpdate;
        var settingsBefore = window.SettingsPage.ViewModel;
        var held = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = new AppUpdateViewModel(() => "0.1.999", _ => held.Task);
        SettingsViewModel? page = null;
        try
        {
            window.ShowPage("sessions");
            main.AppUpdate = update;
            var bar = ById<InfoBar>(window.RootElement, "UpdateReady");
            async Task Bar(string what, string words)
            {
                var drawn = await Until(() => bar is { IsOpen: true } && bar.Message == words && bar.ActualHeight > 0, TimeSpan.FromSeconds(5));
                check(drawn, $"captures the update bar {what}{where} ({bar?.Message})");
                if (drawn) { await Looks(look => Picture(bar!, File($"bar-{what}", look), Under(bar!))); }
            }

            Button? Action() => bar is null ? null : ById<Button>(bar, "RestartToUpdate");
            var accent = (Style)Application.Current.Resources["AccentButtonStyle"];
            await Bar("ready", "Version 0.1.999 is ready. Restart to update.");
            check(Action() is { } atRest && atRest.Style == accent && AutomationProperties.GetName(atRest) == "Restart to update", $"draws the update bar's Restart to update as the accent action at rest{where}");
            var restarting = update.RestartAsync(_ => Task.FromResult(true));
            await Bar("restarting", "Restarting to update\u2026");
            // Creative on #719 (6675195544): the button that started it shows its progress ring,
            // says so, stays focusable, and is not drawn at rest in accent.
            var busy = Action();
            check(busy is { IsEnabled: true, IsTabStop: true } && busy.Style != accent && AutomationProperties.GetItemStatus(busy) == "Restarting\u2026"
                && Find<ProgressRing>(busy).Any(r => r.IsActive && r.Visibility == Visibility.Visible), $"shows the restart under way on its button, which keeps focus{where}");
            held.SetResult(refused);
            await restarting;
            // A failed install: a warning, a kind of reason with no machine detail, and Try again.
            await Bar("failed", "The update didn't install because another bridge profile is running from this installation.");
            check(bar is { Severity: InfoBarSeverity.Warning } && !bar.Message.Any(char.IsDigit) && Action() is { } retry && AutomationProperties.GetName(retry) == "Try again" && retry.Style == accent,
                $"draws a failed install as a warning with Try again and no machine detail{where}");

            // Settings' update row, over the stand-in release.
            if (settingsBefore is not null)
            {
                page = settingsBefore.WithUpdater(new StandInUpdater());
                window.SettingsPage.ViewModel = page;
                window.ShowPage("settings");
                await page.CheckForUpdatesAsync();
                var row = ById<StackPanel>(window.SettingsPage, "UpdateRow");
                var status = row is null ? null : ById<TextBlock>(row, "UpdateStatus");
                var ready = await Until(() => row is { ActualHeight: > 0 } && status is not null && Text(status) == "Version 0.1.999 is ready. Restart to update."
                    && ById<Button>(row, "InstallUpdate") is { Visibility: Visibility.Visible }, TimeSpan.FromSeconds(5));
                check(ready, $"captures Settings' update row with a release ready{where} ({(status is null ? "" : Text(status))})");
                check(status is not null && AutomationProperties.GetLiveSetting(status) == AutomationLiveSetting.Polite, $"makes Settings' update line a polite live region{where}");
                // When an update is ready (#689, 6674007815), drawn with the row: Restart to update unless this machine was set otherwise.
                var mode = row is null ? null : ById<RadioButtons>(row, "WhenUpdateReady");
                check(mode is { SelectedIndex: var at } && AutomationProperties.GetName(mode) == "When an update is ready" && at == page.WhenReadyIndex && mode.Items.Count == 2, $"draws When an update is ready with this machine's choice in Settings' update row{where} ({mode?.SelectedIndex})");
                if (ready)
                {
                    row!.StartBringIntoView();
                    await Task.Delay(300);
                    await Looks(look => Picture(row, File("settings", look), Under(row)));
                }
            }
        }
        finally
        {
            held.TrySetResult(null);
            main.AppUpdate = before;
            if (settingsBefore is not null) { window.SettingsPage.ViewModel = settingsBefore; }
            page?.Detach();
            if (!contrast) { window.RootElement.RequestedTheme = ElementTheme.Default; }
            window.ShowPage("sessions");
        }
    }
}
