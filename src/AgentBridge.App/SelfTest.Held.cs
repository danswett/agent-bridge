using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AgentBridge.App;

// Each machine's Discovery held banner (#293; ui.md §7, Windows): one per machine, each with a
// close button the keyboard reaches, named for what it closes, that hides that banner only and
// leaves focus on what Tab reaches next. The host's hold stands.
static partial class SelfTest
{
    static async Task CheckHeldBanners(MainWindow window, MainViewModel main, DemoHost demo, DemoHost remote, Action<bool, string> check, string outFile)
    {
        window.ShowPage("sessions");
        main.SelectedMachine = main.Machines[0];
        var list = ById<ItemsControl>(window.RootElement, "DiscoveryHeld");
        List<InfoBar> Bars() => list is null ? [] : Find<InfoBar>(list).Where(b => b.IsOpen && b.Visibility == Visibility.Visible).ToList();
        InfoBar? BarOf(string machine) => Bars().FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == $"DiscoveryHeld:{machine}");
        try
        {
            await demo.HoldAsync(new DiscoveryHeld("an unrecognised copilot process (pid 4120)", [AgentKind.Copilot]));
            await remote.HoldAsync(new DiscoveryHeld("the claude processes could not be listed", [AgentKind.Claude]));
            var shown = await Until(() => Bars().Count == 2 && Bars().All(b => MainWindow.CloseButtonOf(b) is not null), TimeSpan.FromSeconds(10));
            var bars = Bars();
            var names = bars.Select(b => MainWindow.CloseButtonOf(b) is { } c ? AutomationProperties.GetName(c) : null).ToList();
            check(shown && bars.All(b => b.IsClosable) && bars.All(b => MainWindow.CloseButtonOf(b) is { IsTabStop: true, IsEnabled: true, Visibility: Visibility.Visible })
                && names.SequenceEqual(["Dismiss Discovery held on Demo machine", $"Dismiss Discovery held on {remote.MachineName}"]),
                $"shows each machine's Discovery held banner with a close button the keyboard reaches, named for what it closes ({string.Join(" | ", names)})");
            if (ById<InfoBar>(window.RootElement, "Banner") is { } warning) { check(!warning.IsClosable, "keeps the banner that says what stops the app from its job unclosable"); }

            await Task.Delay(500);
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                window.RootElement.RequestedTheme = theme;
                await Task.Delay(400);
                await Picture(window.RootElement, Path.ChangeExtension(outFile, $".held.{theme.ToString().ToLowerInvariant()}.png"));
            }
            window.RootElement.RequestedTheme = ElementTheme.Default;

            // The other machine's, from the keyboard: it goes, this machine's stays, and focus is
            // on what Tab reaches next, not lost to the window.
            if (BarOf(remote.MachineId) is { } other && MainWindow.CloseButtonOf(other) is { } close)
            {
                var focusedFirst = close.Focus(FocusState.Keyboard);
                ((IInvokeProvider)new ButtonAutomationPeer(close).GetPattern(PatternInterface.Invoke)).Invoke();
                var gone = await Until(() => BarOf(remote.MachineId) is null && main.HeldNotices.Count == 1, TimeSpan.FromSeconds(5));
                await Task.Delay(300);
                var focused = FocusManager.GetFocusedElement(window.RootElement.XamlRoot) as DependencyObject;
                var focusKept = focused is Control { FocusState: not FocusState.Unfocused } && Above<FrameworkElement>(focused, e => e == window.RootElement) is not null && Above<InfoBar>(focused, b => b == other) is null;
                check(focusedFirst && gone && BarOf(Machine.LocalKey) is not null, $"hides only the machine's own banner when its close button is pressed ({Bars().Count} left)");
                check(focusKept, $"moves keyboard focus on to what Tab reaches next when a banner closes ({(focused as FrameworkElement)?.Name}{(focused is null ? "nothing" : $" {focused.GetType().Name} {AutomationProperties.GetAutomationId(focused)}")})");
                check(main.MachineOf(DemoHost.RemoteId)?.Host.Sessions.Held is not null, "leaves the host's hold as it was when its banner is dismissed");
                await Task.Delay(300);
                foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
                {
                    window.RootElement.RequestedTheme = theme;
                    await Task.Delay(400);
                    await Picture(window.RootElement, Path.ChangeExtension(outFile, $".held-dismissed.{theme.ToString().ToLowerInvariant()}.png"));
                }
                window.RootElement.RequestedTheme = ElementTheme.Default;
            }
            else { check(false, "finds the other machine's Discovery held banner and its close button"); }

            // Another hold on that machine is news, and shows again.
            await remote.HoldAsync(new DiscoveryHeld("an unrecognised copilot process (pid 5120)", [AgentKind.Copilot]));
            check(await Until(() => BarOf(remote.MachineId) is not null, TimeSpan.FromSeconds(10)), "shows a dismissed machine's banner again once its hold is another");
        }
        finally
        {
            await demo.HoldAsync(null);
            await remote.HoldAsync(null);
            await Until(() => main.HeldNotices.Count == 0, TimeSpan.FromSeconds(10));
        }
    }
}
