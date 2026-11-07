using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

// The update captures Creative's acceptance of #719 asks for (6643004091, 6677102646): the bar over
// every page ready, restarting and failed, and Settings' updates (When an update is ready among
// them) under the banner, and a refusal with no banner, in Light and Dark,
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
        public void ApplyAndRestart(string version, Action? handedOver = null) => throw new InvalidOperationException("the self-test never updates");
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
        SettingsViewModel? alone = null;
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

            // #1167: the Updating screen, the open window's while the release is handed to
            // Velopack, and the new version's Updated bar, and an install that didn't take.
            await CaptureUpdating(window, main, check, File, Under, Looks, where, contrast);

            // Settings' update row, over the stand-in release, under the banner offering it: the
            // banner says the offer, so Settings' line doesn't repeat it, and its Restart to update
            // stays (Creative, 6677102646, principle 6). Captured with the banner, the whole window.
            if (settingsBefore is not null)
            {
                page = settingsBefore.WithUpdater(new StandInUpdater());
                var offering = new AppUpdateViewModel(() => "0.1.999", _ => Task.FromResult<string?>(AutoUpdater.NotNow));
                main.AppUpdate = offering;
                page.ShareRestart(offering);
                window.SettingsPage.ViewModel = page;
                window.ShowPage("settings");
                await page.CheckForUpdatesAsync();
                var row = ById<StackPanel>(window.SettingsPage, "UpdateRow");
                var status = row is null ? null : ById<TextBlock>(row, "UpdateStatus");
                var ready = await Until(() => row is { ActualHeight: > 0 } && status is not null && Text(status) == ""
                    && bar is { IsOpen: true, Message: "Version 0.1.999 is ready. Restart to update." }
                    && ById<Button>(row, "InstallUpdate") is { Visibility: Visibility.Visible }, TimeSpan.FromSeconds(5));
                check(ready, $"captures Settings' update row with a release ready under the banner offering it, without repeating the offer{where} ({(status is null ? "" : Text(status))})");
                check(status is not null && AutomationProperties.GetLiveSetting(status) == AutomationLiveSetting.Polite, $"makes Settings' update line a polite live region{where}");
                // When an update is ready (#689, 6674007815), drawn with the row: Restart to update unless this machine was set otherwise.
                var mode = row is null ? null : ById<RadioButtons>(row, "WhenUpdateReady");
                check(mode is { SelectedIndex: var at } && AutomationProperties.GetName(mode) == "When an update is ready" && at == page.WhenReadyIndex && mode.Items.Count == 2, $"draws When an update is ready with this machine's choice in Settings' update row{where} ({mode?.SelectedIndex})");
                if (ready)
                {
                    row!.StartBringIntoView();
                    await Task.Delay(300);
                    await Looks(look => Picture(window.RootElement, File("settings", look), Under(window.RootElement)));
                }

                // A restart started from Settings and refused by another profile (Creative,
                // 6676653935, 6677102646): one restart, one state, so the banner shows the failure
                // as a warning with Try again, and Settings' line doesn't repeat it, nor is it an
                // error.
                var sharedRestart = new AppUpdateViewModel(() => "0.1.999", _ => Task.FromResult<string?>(refused));
                main.AppUpdate = sharedRestart;
                page.ShareRestart(sharedRestart);
                await page.InstallUpdateAsync(_ => Task.FromResult(true));
                const string refusedWords = "The update didn't install because another bridge profile is running from this installation.";
                var both = await Until(() => row is { ActualHeight: > 0 } && status is not null && Text(status) == ""
                    && bar is { IsOpen: true, Severity: InfoBarSeverity.Warning, Message: refusedWords } && Action() is { } again && AutomationProperties.GetName(again) == "Try again"
                    && page.Error is null, TimeSpan.FromSeconds(5));
                check(both, $"shows a restart from Settings that another profile refused on the banner, with Try again, said once, not as an error{where} ({(status is null ? "" : Text(status))})");
                if (both)
                {
                    await Looks(look => Picture(window.RootElement, File("settings-refused", look), Under(window.RootElement)));
                    await Looks(look => Picture(bar!, File("bar-settings-refused", look), Under(bar!)));
                }

                // With no banner (a copy whose app has no updater of its own), the refusal is said in
                // Settings, once, as a status.
                alone = settingsBefore.WithUpdater(new StandInUpdater());
                alone.RestartDownloaded = _ => Task.FromResult<string?>(refused);
                main.AppUpdate = new AppUpdateViewModel();
                window.SettingsPage.ViewModel = alone;
                await alone.InstallUpdateAsync(_ => Task.FromResult(true));
                row = ById<StackPanel>(window.SettingsPage, "UpdateRow");
                status = row is null ? null : ById<TextBlock>(row, "UpdateStatus");
                var once = await Until(() => row is { ActualHeight: > 0 } && status is not null && Text(status) == refusedWords
                    && bar is { IsOpen: false } && alone.Error is null, TimeSpan.FromSeconds(5));
                check(once, $"says a refused restart once in Settings when no banner shows it, not as an error{where} ({(status is null ? "" : Text(status))})");
                if (once) { await Looks(look => Picture(window.RootElement, File("settings-refused-no-banner", look), Under(window.RootElement))); }
            }
        }
        finally
        {
            held.TrySetResult(null);
            main.AppUpdate = before;
            if (settingsBefore is not null) { window.SettingsPage.ViewModel = settingsBefore; }
            page?.Detach();
            alone?.Detach();
            if (!contrast) { window.RootElement.RequestedTheme = ElementTheme.Default; }
            window.ShowPage("sessions");
        }
    }

    // #1167's captures: the Updating screen (one centred state, no buttons, its title a heading said
    // once, on the page's layer, which high contrast draws in the window colour), the Updated bar
    // with What's new, and a start still on the old version, as a warning with Try again.
    static async Task CaptureUpdating(MainWindow window, MainViewModel main, Action<bool, string> check, Func<string, string, string> file,
        Func<DependencyObject, Windows.UI.Color> under, Func<Func<string, Task>, Task> looks, string where, bool contrast)
    {
        var before = main.AppUpdate;
        try
        {
            // Focus somewhere under the screen first, as a press of Restart to update leaves it:
            // the screen takes it, and gives it back should the install not go ahead.
            window.FocusSearch();
            await Task.Delay(200);
            var updating = new AppUpdateViewModel(() => null, _ => Task.FromResult<string?>(null), () => "0.1.999");
            main.AppUpdate = updating;
            var pane = ById<Grid>(window.RootElement, "Updating");
            var heading = pane is null ? null : ById<ContentControl>(pane, "UpdatingTitle");
            var ring = pane is null ? null : ById<ProgressRing>(pane, "UpdatingProgress");
            var body = pane is null ? null : ById<TextBlock>(pane, "UpdatingBody");
            var drawn = await Until(() => pane is { Visibility: Visibility.Visible, ActualHeight: > 0 } && heading is { Content: TextBlock t } && Text(t) == "Updating to version 0.1.999", TimeSpan.FromSeconds(5));
            check(drawn, $"captures the Updating screen{where}");
            check(heading is not null && AutomationProperties.GetHeadingLevel(heading) == AutomationHeadingLevel.Level1 && AutomationProperties.GetName(heading) == "Updating to version 0.1.999"
                && AutomationProperties.GetLiveSetting(heading) == AutomationLiveSetting.Polite
                && ring is { IsActive: true, IsIndeterminate: true } && AutomationProperties.GetName(ring) == "Updating to version 0.1.999"
                && body is not null && Text(body) == AppUpdateViewModel.UpdatingBody
                && pane is not null && Find<Button>(pane).Count == 0,
                $"draws the Updating screen as one state, its title a polite heading, its ring named, with no buttons{where}");

            // Nothing under the screen in reach: the heading has focus, so its line is read; Tab and
            // Shift+Tab reach nothing in the rail or the pages; and they are not in the automation
            // tree a screen reader browses.
            var xamlRoot = window.RootElement.XamlRoot;
            bool Within(DependencyObject? at, DependencyObject root)
            {
                for (var up = at; up is not null; up = VisualTreeHelper.GetParent(up)) { if (ReferenceEquals(up, root)) { return true; } }
                return false;
            }
            var focusedHeading = await Until(() => ReferenceEquals(FocusManager.GetFocusedElement(xamlRoot), heading), TimeSpan.FromSeconds(3));
            var reached = new List<string>();
            foreach (var way in new[] { FocusNavigationDirection.Next, FocusNavigationDirection.Previous })
            {
                for (var step = 0; step < 6; step++)
                {
                    if (!FocusManager.TryMoveFocus(way, new FindNextElementOptions { SearchRoot = xamlRoot.Content })) { break; }
                    if (FocusManager.GetFocusedElement(xamlRoot) is DependencyObject now && Within(now, window.Rail)) { reached.Add((now as FrameworkElement)?.Name is { Length: > 0 } n ? n : now.GetType().Name); }
                }
                if (heading is not null) { heading.IsTabStop = true; heading.Focus(FocusState.Programmatic); }
            }
            // Out of the tree a screen reader browses, read as one reads it: from another process,
            // through UI Automation (Uia below), the rail's items and the update bar absent, the
            // heading there. In-process peers can't say it: they report offscreen only once a
            // client has asked.
            var seen = await Uia(window.Handle);
            var reachable = seen is null ? ["(UI Automation unreadable)"] : new[] { "Rail", "NavSessions", "UpdateReady" }.Where(seen.Contains).ToList();
            var headingOnScreen = seen?.Contains("UpdatingTitle") == true;
            check(focusedHeading && reached.Count == 0 && window.Rail.Visibility == Visibility.Collapsed && !window.Search.IsEnabled && reachable.Count == 0 && headingOnScreen,
                $"puts focus on the Updating heading and leaves nothing under the screen to Tab to or for a screen reader to find{where} (focus on {(FocusManager.GetFocusedElement(xamlRoot) as FrameworkElement)?.Name}, at once {focusedHeading}; Tab reached {string.Join(", ", reached)}; search enabled {window.Search.IsEnabled}; in UI Automation still {string.Join(", ", reachable)}; heading there {headingOnScreen})");
            if (contrast)
            {
                var windowColour = Application.Current.Resources.TryGetValue("SystemColorWindowColor", out var w) && w is Windows.UI.Color c ? c : (Windows.UI.Color?)null;
                check(pane?.Background is SolidColorBrush { Color: var fill } && fill == windowColour, $"draws the Updating screen on the window colour in high contrast ({(pane?.Background as SolidColorBrush)?.Color}, wanted {windowColour})");
            }
            if (drawn) { await looks(look => Picture(window.RootElement, file("updating", look), under(window.RootElement))); }

            var updated = new AppUpdateViewModel(() => null, _ => Task.FromResult<string?>(null));
            main.AppUpdate = updated;
            updated.Show(new UpdateAnnouncement(true, "0.1.0", "0.1.999", "0.1.999", WindowOpen: true));
            var bar = ById<InfoBar>(window.RootElement, "Updated");
            var shown = await Until(() => pane is { Visibility: Visibility.Collapsed } && window.Rail.Visibility == Visibility.Visible && window.Search.IsEnabled && bar is { IsOpen: true, ActualHeight: > 0 } && bar.Message == "Updated to version 0.1.999.", TimeSpan.FromSeconds(5));
            check(shown && Within(FocusManager.GetFocusedElement(xamlRoot) as DependencyObject, window.Search),
                $"gives the rail, the pages, search and focus back once the Updating screen goes{where} (focus on {(FocusManager.GetFocusedElement(xamlRoot) as FrameworkElement)?.Name})");
            // The control for the read above: the same read now finds the rail, and not the heading.
            var back = await Uia(window.Handle);
            check(back is not null && back.Contains("NavSessions") && !back.Contains("UpdatingTitle"),
                $"finds the rail through UI Automation again once the Updating screen goes{where} ({(back is null ? "unreadable" : $"{back.Count} ids")})");
            // Its action is in the bar's template, there only once the bar is open.
            var link = bar is null ? null : ById<HyperlinkButton>(bar, "WhatsNew");
            check(shown && bar is { Severity: InfoBarSeverity.Success, IsClosable: true } && AutomationProperties.GetLiveSetting(bar) == AutomationLiveSetting.Polite
                && link is { NavigateUri: { } notes } && notes.ToString().EndsWith("/releases/tag/v0.1.999", StringComparison.Ordinal) && link.Content as string == "What's new",
                $"draws the Updated bar as a closable success with What's new{where} ({bar?.Message}; {link?.NavigateUri})");
            if (shown) { await looks(look => Picture(bar!, file("updated", look), under(bar!))); }

            var failed = new AppUpdateViewModel(() => "0.1.999", _ => Task.FromResult<string?>(null));
            main.AppUpdate = failed;
            failed.Show(new UpdateAnnouncement(false, "0.1.0", "0.1.0", "0.1.999", WindowOpen: false));
            var ready = ById<InfoBar>(window.RootElement, "UpdateReady");
            var couldNot = $"Couldn't install version 0.1.999. {Product.Name} is still on 0.1.0.";
            var warned = await Until(() => ready is { IsOpen: true, Severity: InfoBarSeverity.Warning, ActualHeight: > 0 } && ready.Message == couldNot
                && ById<Button>(ready, "RestartToUpdate") is { } again && AutomationProperties.GetName(again) == "Try again", TimeSpan.FromSeconds(5));
            check(warned, $"says an install that didn't take as a warning with Try again{where} ({ready?.Message})");
            if (warned) { await looks(look => Picture(ready!, file("bar-could-not-install", look), under(ready!))); }
        }
        finally { main.AppUpdate = before; }
    }

    // Every AutomationId in the window's raw UI Automation tree, read from another process as a
    // screen reader reads it (Windows PowerShell's UIAutomationClient, part of Windows), so the
    // app's UI thread is free to answer. Null when it can't be read.
    static async Task<HashSet<string>?> Uia(nint window)
    {
        const string script = "Add-Type -AssemblyName UIAutomationClient; $w = [System.Windows.Automation.TreeWalker]::RawViewWalker; "
            + "function Walk($e, $d) { if ($d -gt 60) { return }; $c = $w.GetFirstChild($e); while ($c) { try { $c.Current.AutomationId } catch { }; Walk $c ($d + 1); $c = $w.GetNextSibling($c) } }; "
            + "Walk ([System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]::new([long]$args[0]))) 0";
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", $"& {{ {script} }} {window}" }) { info.ArgumentList.Add(a); }
            using var p = System.Diagnostics.Process.Start(info)!;
            var output = p.StandardOutput.ReadToEndAsync();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await p.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException) { p.Kill(true); return null; }
            return p.ExitCode == 0 ? (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet() : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }
}
