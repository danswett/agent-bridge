using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

// The machine tabs (ui.md §6.1, #290): with more machines than the list's width holds, every machine
// is still a tab or under More, nothing goes past the pane's edge or scrolls sideways, the chosen
// one always shows, a long name is cut short with the whole of it as the tooltip and the name, and
// a machine chosen under More becomes a tab, selected. At the narrowest list, two panes and one column.
static partial class SelfTest
{
    static async Task CheckMachineTabs(MainWindow window, MainViewModel main, Action<bool, string> check, string outFile)
    {
        window.ShowPage("sessions");
        var page = window.Sessions;
        var layout = page.Layout!;
        var bar = ById<SelectorBar>(page, "MachinePicker");
        var more = page.MoreMachinesButton;
        if (bar is null || more.Parent is not Grid strip || ById<Grid>(page, "ListPane") is not { } pane)
        {
            check(false, "finds the machine tabs, More and the list pane");
            return;
        }
        async Task<bool> At(double width, PaneMode mode)
        {
            Resize(window, width, 900);
            var ok = await Until(() => layout.Mode == mode && Math.Abs(window.RootElement.XamlRoot.Size.Width - window.RootElement.ActualWidth) < 1, TimeSpan.FromSeconds(10));
            await Task.Delay(400);
            return ok;
        }
        static IRangeValueProvider? Range(PaneDividerView d) => FrameworkElementAutomationPeer.CreatePeerForElement(d)?.GetPattern(PatternInterface.RangeValue) as IRangeValueProvider;
        static TextBlock? NameOf(SelectorBarItem tab) => SessionsPage.NameOf(tab);

        // Stand-ins for more machines, one with a long name and one view only; with no host
        // behind them, choosing one lists nothing.
        MachineChoice[] extra =
        [
            new("selftest-long", "DSWETT-DEV-VM1 (shadow of the build machine) · 0 sessions", "DSWETT-DEV-VM1 (shadow of the build machine)"),
            new("selftest-arm", "DSWETT-ARM8 · 0 sessions", "DSWETT-ARM8"),
            new("selftest-lab", "Lab bench 2 · 0 sessions · View only", "Lab bench 2"),
        ];
        foreach (var c in extra) { main.Machines.Add(c); }
        try
        {
            // What's wrong with the tabs as drawn, if anything.
            List<string> Wrong()
            {
                var wrong = new List<string>();
                var shown = bar.Items.Select(i => i.Tag as MachineChoice).ToList();
                var under = page.MachinesUnderMore;
                var every = shown.Concat(under).ToList();
                if (every.Count != main.Machines.Count || main.Machines.Any(m => !every.Contains(m))) { wrong.Add($"not every machine a tab or under More ({string.Join(", ", shown.Select(c => c?.Short))} | {string.Join(", ", under.Select(c => c.Short))})"); }
                if (!shown.Contains(main.SelectedMachine) || bar.SelectedItem?.Tag != main.SelectedMachine) { wrong.Add($"the chosen machine ({main.SelectedMachine?.Short}) not a selected tab"); }
                if ((under.Count > 0) != (more.Visibility == Visibility.Visible)) { wrong.Add($"More {more.Visibility} with {under.Count} under it"); }
                // Against the list pane, not the bar: a bar too full is laid out wider than the pane and clipped.
                var edge = pane.ActualWidth - pane.BorderThickness.Right - strip.Padding.Right + 0.5;
                double Right(FrameworkElement e) => e.TransformToVisual(pane).TransformPoint(new Windows.Foundation.Point(e.ActualWidth, 0)).X;
                if (Right(bar) > edge || (more.Visibility == Visibility.Visible && Right(more) > edge)) { wrong.Add($"past the pane's edge (tabs to {Right(bar):0}, More to {(more.Visibility == Visibility.Visible ? Right(more) : 0):0}, of {edge:0})"); }
                foreach (var tab in bar.Items)
                {
                    if (tab.Tag is not MachineChoice c) { continue; }
                    if (NameOf(tab) is not { } name || name.ActualWidth > DesignLayout.MachineTab + 0.5) { wrong.Add($"{c.Short}'s name {NameOf(tab)?.ActualWidth:0} px wide"); }
                    if (AutomationProperties.GetName(tab) != c.Label || ToolTipService.GetToolTip(tab) as string != c.Label) { wrong.Add($"{c.Short} not named in full"); }
                }
                return wrong;
            }

            // The narrowest list, beside a session.
            await At(1440, PaneMode.Three);
            Range(page.ListDividerView)?.SetValue(DesignLayout.ListMin);
            await Until(() => page.MachinesUnderMore.Count > 0, TimeSpan.FromSeconds(5));
            await Task.Delay(400);
            var narrow = Wrong();
            check(narrow.Count == 0 && page.MachinesUnderMore.Count > 0,
                $"keeps every machine a tab or under More at the narrowest list, the chosen one a tab and nothing past the pane's edge ({page.MachinesUnderMore.Count} under More{(narrow.Count > 0 ? $"; {string.Join("; ", narrow)}" : "")})");
            var focusable = more.IsTabStop && more.IsEnabled && more.Focus(FocusState.Keyboard);
            check(focusable && AutomationProperties.GetName(more) == Words.Plural(page.MachinesUnderMore.Count, "more machine", "more machines"),
                $"offers More to the keyboard, named with how many machines are under it (\"{AutomationProperties.GetName(more)}\")");

            // Chosen under More: the long-named one becomes a tab, selected, its name cut short.
            var target = extra[0];
            if (!page.MachinesUnderMore.Contains(target))
            {
                check(false, $"puts the long-named machine under More at the narrowest list ({string.Join(", ", page.MachinesUnderMore.Select(c => c.Short))})");
                return;
            }
            ((IInvokeProvider)new ButtonAutomationPeer(more).GetPattern(PatternInterface.Invoke)).Invoke();
            var menu = more.Flyout as MenuFlyout;
            var listed = await Until(() => menu?.Items.Count == page.MachinesUnderMore.Count && VisualTreeHelper.GetOpenPopupsForXamlRoot(window.RootElement.XamlRoot).Count > 0, TimeSpan.FromSeconds(5));
            var lines = menu?.Items.OfType<MenuFlyoutItem>().Select(i => i.Text).ToList() ?? [];
            check(listed && lines.SequenceEqual(page.MachinesUnderMore.Select(c => c.Label)) && lines.Contains("Lab bench 2 · 0 sessions · View only"),
                $"lists each machine under More with its state ({string.Join(" | ", lines)})");
            if (VisualTreeHelper.GetOpenPopupsForXamlRoot(window.RootElement.XamlRoot).FirstOrDefault()?.Child is { } presenter)
            {
                await Picture(presenter, Path.ChangeExtension(outFile, ".machine-more.png"));
            }
            var entry = menu?.Items.OfType<MenuFlyoutItem>().FirstOrDefault(i => i.Tag == target);
            if (entry is not null) { ((IInvokeProvider)new MenuFlyoutItemAutomationPeer(entry).GetPattern(PatternInterface.Invoke)).Invoke(); }
            var chosen = await Until(() => main.SelectedMachine == target && bar.SelectedItem?.Tag == target, TimeSpan.FromSeconds(5));
            await Task.Delay(400);
            var tab = bar.Items.FirstOrDefault(i => i.Tag == target);
            var after = Wrong();
            string Drawn() => $"drawn: pane {pane.ActualWidth:0}, bar {strip.ActualWidth:0}, tabs {string.Join(" ", bar.Items.Select(i => i.ActualWidth.ToString("0")))}, More {more.ActualWidth:0}; arranged from {page.Arranged}";
            check(chosen && tab is not null && NameOf(tab) is { IsTextTrimmed: true } && after.Count == 0,
                $"makes a machine chosen under More a selected tab, its long name cut short and named in full ({Drawn()}){(after.Count > 0 ? $": {string.Join("; ", after)}" : "")}");
            // Pictures for review, in both themes, with no focus rectangle left on More.
            page.Rows.Focus(FocusState.Programmatic);
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                window.RootElement.RequestedTheme = theme;
                await Task.Delay(400);
                await Picture(window.RootElement, Path.ChangeExtension(outFile, $".machine-tabs.{theme.ToString().ToLowerInvariant()}.png"));
            }
            window.RootElement.RequestedTheme = ElementTheme.Default;
            await Task.Delay(400);
            var themed = Wrong();
            check(themed.Count == 0, $"keeps every machine a tab or under More once the theme changes ({Drawn()}){(themed.Count > 0 ? $": {string.Join("; ", themed)}" : "")}");

            // Windows' text made larger (#368 review): a wider More beside the chosen tab's whole
            // 128 px name went past the narrowest list's edge. The chosen name is cut to what More
            // leaves, and the tabs refit by themselves when More's size changes under an unchanged pane,
            // as it does when Windows' text size changes while the page is open.
            TextBlock? ChosenName() => bar.SelectedItem is { } chosenTab ? NameOf(chosenTab) : null;
            // Settled: no arrangement for a second, so fitting again never sets itself off.
            async Task<bool> Settles()
            {
                for (var tries = 0; tries < 10; tries++)
                {
                    var before = page.Arrangements;
                    await Task.Delay(1000);
                    if (page.Arrangements == before) { return true; }
                }
                return false;
            }
            var arrangedBefore = page.Arrangements;
            page.MoreMachinesWords.FontSize = 32;
            var refitted = await Until(() => ChosenName() is { MaxWidth: < DesignLayout.MachineTab } && Wrong().Count == 0, TimeSpan.FromSeconds(5));
            var settled = await Settles();
            var larger = Wrong();
            var cutName = ChosenName();
            check(refitted && settled && page.Arrangements > arrangedBefore && larger.Count == 0 && bar.SelectedItem?.Tag == target && cutName is { IsTextTrimmed: true },
                $"refits by itself when More grows under an unchanged pane, cutting the chosen tab's name to what More leaves at the narrowest list, and settles ({Drawn()}; the name up to {cutName?.MaxWidth:0} px; {page.Arrangements - arrangedBefore} arrangements, settled {settled}){(larger.Count > 0 ? $": {string.Join("; ", larger)}" : "")}");
            page.MoreMachinesWords.ClearValue(TextBlock.FontSizeProperty);
            var regrown = await Until(() => ChosenName()?.MaxWidth == DesignLayout.MachineTab && Wrong().Count == 0, TimeSpan.FromSeconds(5));
            var settledAgain = await Settles();
            check(regrown && settledAgain, $"gives the chosen tab's name its whole room back by itself once More is its own width again, and settles ({ChosenName()?.MaxWidth:0} px)");

            // Two panes at the list's default, and one column: the same, whatever fits.
            page.ListDividerView.Reset();
            foreach (var (w, mode) in new[] { (1000.0, PaneMode.Two), (400.0, PaneMode.One) })
            {
                var at = await At(w, mode);
                var wrong = Wrong();
                check(at && wrong.Count == 0, $"keeps every machine a tab or under More at {w} px ({mode}) ({Drawn()}){(wrong.Count > 0 ? $": {string.Join("; ", wrong)}" : "")}");
                if (mode == PaneMode.One) { await Picture(window.RootElement, Path.ChangeExtension(outFile, ".machine-tabs.narrow.png")); }
            }
        }
        finally
        {
            main.SelectedMachine = main.Machines[0];
            foreach (var c in extra) { main.Machines.Remove(c); }
            page.ListDividerView.Reset();
            await At(1440, PaneMode.Three);
        }
    }
}
