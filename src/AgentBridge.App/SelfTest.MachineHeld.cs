using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

// A held machine's row on the Machines page (#788; ui.md §6.6): "Discovery held" in line 2's flag,
// the host's reason as the line's tooltip and accessible description and never on the row, and
// the line's name the words shown (§9.1 A1). The hold is put in the store as discovery.changed
// would, so the demo host stays as it is.
static partial class SelfTest
{
    static async Task CheckMachineHeld(MainWindow window, MainViewModel main, DemoHost remote, Action<bool, string> check, string outFile)
    {
        if (main.MachineOf(DemoHost.RemoteId) is not { } machine)
        {
            check(false, "finds the other machine to hold its discovery");
            return;
        }
        const string reason = "an unrecognised copilot process (pid 4120)";
        window.ShowPage("machines");
        TextBlock? Facts() => Find<Grid>(window.RootElement)
            .Where(g => AutomationProperties.GetAutomationId(g) == "MachineRow" && g.ActualHeight > 0 && ById<TextBlock>(g, "MachineName")?.Text == remote.MachineName)
            .Select(g => ById<TextBlock>(g, "MachineFacts")).FirstOrDefault();
        try
        {
            machine.Host.Sessions.SetHeld(new DiscoveryHeld(reason, [AgentKind.Copilot]));
            var said = await Until(() => Facts()?.Text.EndsWith(" · Discovery held", StringComparison.Ordinal) == true, TimeSpan.FromSeconds(5));
            await Task.Delay(300);
            var facts = Facts();
            var name = facts is null ? null : FrameworkElementAutomationPeer.CreatePeerForElement(facts)?.GetName();
            var tip = facts is null ? null : ToolTipService.GetToolTip(facts) as string;
            var description = facts is null ? null : AutomationProperties.GetHelpText(facts);
            check(said && facts!.Text.Split(" · ").Length <= 2 && !facts.Text.Contains("pid", StringComparison.Ordinal),
                $"says \"Discovery held\" on a held machine's row, in line 2's flag and with no reason on the row ({facts?.Text})");
            check(tip == $"Discovery held: {reason}" && description == tip, $"gives a held machine's line the host's reason as its tooltip and accessible description ({tip} | {description})");
            check(name == facts?.Text, $"names a held machine's line with the words it shows ({name})");
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                window.RootElement.RequestedTheme = theme;
                await Task.Delay(400);
                await Picture(window.RootElement, Path.ChangeExtension(outFile, $".machines-held.{theme.ToString().ToLowerInvariant()}.png"));
            }
            window.RootElement.RequestedTheme = ElementTheme.Default;
        }
        finally
        {
            machine.Host.Sessions.SetHeld(null);
        }
        var cleared = await Until(() => Facts() is { } f && !f.Text.Contains("Discovery held", StringComparison.Ordinal) && ToolTipService.GetToolTip(f) is null, TimeSpan.FromSeconds(5));
        check(cleared, $"drops \"Discovery held\" and its reason from the row once the hold clears ({Facts()?.Text})");
    }
}
