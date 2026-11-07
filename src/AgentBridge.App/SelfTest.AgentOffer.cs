using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

// The card for an agent installed after the bridge (#231; ui.md §6.9, §9), on the demo's Copilot CLI,
// which is installed and never set up: a group named for its line with its two buttons named for the
// agent, above everything else on Settings and never taking focus, said once; a grey dot (no count) on
// the Settings item, named for it; and the row holding no second Set up while the card shows. Pictured
// in Light and Dark, wide and at the narrowest window. Then Not now, as a person presses it: the card
// and the dot go, the row's Set up returns, and focus goes to the next card or the first control below
// (there is no next card, so Check agents again).
static partial class SelfTest
{
    static async Task CheckAgentOffer(MainWindow window, MainViewModel main, Action<bool, string> check, string outFile, Dictionary<string, object?> result)
    {
        window.ShowPage("settings");
        var view = window.SettingsPage;
        if (main.Settings is not { } settings) { check(false, "offers an agent installed later a card (no Settings)"); return; }
        const string line = "Copilot CLI 1.0.105 is installed";
        if (!await Until(() => settings.AgentOffers.Count == 1 && Find<AgentOfferGroup>(view.Offers).Count == 1, TimeSpan.FromSeconds(10)))
        {
            check(false, $"offers the demo's Copilot CLI, installed and never set up, a card ({settings.AgentOffers.Count} offers; {settings.AgentsState})");
            return;
        }
        await Task.Delay(500);
        var group = Find<AgentOfferGroup>(view.Offers).Single();
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(group);
        var buttons = Find<Button>(group).Select(b => (Id: AutomationProperties.GetAutomationId(b), Name: FrameworkElementAutomationPeer.CreatePeerForElement(b)?.GetName() ?? "", Button: b)).ToList();
        var words = Find<TextBlock>(group).Where(t => Above<Button>(t, _ => true) is null).Select(t => t.Text).ToList();
        check(peer?.GetAutomationControlType() == AutomationControlType.Group && peer.GetName() == line && AutomationProperties.GetAutomationId(group) == "AgentOffer_copilot"
            && words.SequenceEqual([$"{line} · Set it up to see its sessions in {Product.Name}"])
            && buttons.Select(b => $"{b.Id}: {b.Name}").SequenceEqual(["AgentOfferSetUp_copilot: Set up Copilot CLI", "AgentOfferNotNow_copilot: Not now, Copilot CLI"]),
            $"draws the card for an agent installed later as a group named for its line, with its words and its two buttons named for the agent ({peer?.GetAutomationControlType()} \"{peer?.GetName()}\"; {string.Join(" | ", words)}; {string.Join(" | ", buttons.Select(b => $"{b.Id}: {b.Name}"))})");

        // At the top of the page, ahead of the agents it is about, and never taking focus where it appears.
        var above = group.TransformToVisual(view.AgentRows).TransformPoint(new Windows.Foundation.Point(0, 0)).Y < 0;
        var focused = FocusManager.GetFocusedElement(window.RootElement.XamlRoot) as DependencyObject;
        var inCard = focused is not null && Above<AgentOfferGroup>(focused, g => g == group) is not null;
        check(above && !inCard, $"puts the card ahead of the agents and leaves focus where it was (above: {above}; focus in the card: {inCard})");

        // The row holds no second Set up while the card shows it (ui.md §6.9, nothing drawn twice).
        var rowSetUp = Buttons(view.AgentRows).FirstOrDefault(b => b.Id == "SetUpAgent_copilot").Button;
        check(rowSetUp is null or { Visibility: Visibility.Collapsed }, $"draws no second Set up on the agent's row while the card shows ({(rowSetUp is null ? "absent" : rowSetUp.Visibility.ToString())})");

        // The grey dot on the Settings item: a dot, never a count, and in the item's name.
        var settingsItem = window.Rail.FooterMenuItems.OfType<NavigationViewItem>().Single();
        var dot = ById<InfoBadge>(settingsItem, "NavSettingsDot");
        var itemName = FrameworkElementAutomationPeer.CreatePeerForElement(settingsItem)?.GetName();
        check(dot is { Visibility: Visibility.Visible, Value: -1 } && itemName == "Settings, new agent found"
            && (dot.Background as SolidColorBrush)?.Color == (Ui.Brush("AbNeutral") as SolidColorBrush)?.Color,
            $"marks the Settings item with a grey dot, not a count, named in the item ({(dot is null ? "no dot" : $"{dot.Visibility}, value {dot.Value}, {(dot.Background as SolidColorBrush)?.Color}")}; \"{itemName}\")");

        // Said once, politely, while Settings is open.
        await Task.Delay(400);
        result["agentOfferAnnouncements"] = view.OffersAnnounced.ToList();
        check(view.OffersAnnounced.SequenceEqual([line]) && AutomationProperties.GetLiveSetting(group) == AutomationLiveSetting.Polite,
            $"raises the card once, as a polite live region ({string.Join(" | ", view.OffersAnnounced)})");

        // Pictured wide in both themes, and at the narrowest window, where the words wrap and the buttons
        // stay inside it.
        var root = window.RootElement;
        foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
        {
            root.RequestedTheme = theme;
            await Task.Delay(400);
            await Picture(root, Path.ChangeExtension(outFile, $".settings-offer.{theme.ToString().ToLowerInvariant()}.png"));
        }
        var before = window.AppWindow.Size;
        Resize(window, DesignLayout.MinWidth, DesignLayout.MinHeight);
        var cut = new List<string>();
        try
        {
            await Until(() => root.ActualWidth <= DesignLayout.MinWidth + 1, TimeSpan.FromSeconds(10));
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                root.RequestedTheme = theme;
                await Task.Delay(500);
                foreach (var (id, _, button) in Find<Button>(group).Select(b => (Id: AutomationProperties.GetAutomationId(b), Name: "", Button: b)))
                {
                    var right = button.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(button.ActualWidth, 0)).X;
                    if (right > root.ActualWidth + 1) { cut.Add($"{theme}: {id} to {right:0}"); }
                }
                await Picture(root, Path.ChangeExtension(outFile, $".settings-offer.narrow.{theme.ToString().ToLowerInvariant()}.png"));
            }
        }
        finally
        {
            root.RequestedTheme = ElementTheme.Default;
            window.AppWindow.Resize(before);
            await Task.Delay(500);
        }
        check(cut.Count == 0, $"keeps the card's buttons inside a {DesignLayout.MinWidth:0} px window{(cut.Count > 0 ? $": not {string.Join(", ", cut)}" : "")}");

        // Set up on the card opens the row's own confirmation (named for the agent, Cancel the default) and
        // is cancelled here: the same handler as the row's, so one writer.
        var setUp = Find<Button>(group).First(b => AutomationProperties.GetAutomationId(b) == "AgentOfferSetUp_copilot");
        ((IInvokeProvider)new ButtonAutomationPeer(setUp).GetPattern(PatternInterface.Invoke)).Invoke();
        ContentDialog? dialog = null;
        await Until(() => (dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
            .SelectMany(p => p.Child is ContentDialog c ? new[] { c } : p.Child is { } child ? Find<ContentDialog>(child).ToArray() : []).FirstOrDefault()) is not null, TimeSpan.FromSeconds(5));
        var asked = dialog is null ? null : $"{dialog.Title} ({dialog.CloseButtonText} the default: {dialog.DefaultButton == ContentDialogButton.Close})";
        dialog?.Hide();
        await Task.Delay(400);
        check(asked == "Set up Copilot CLI? (Cancel the default: True)", $"opens the row's own confirmation from the card's Set up ({asked ?? "no dialog"})");

        // Not now, pressed as UI Automation would.
        var notNow = Find<Button>(Find<AgentOfferGroup>(view.Offers).Single()).First(b => AutomationProperties.GetAutomationId(b) == "AgentOfferNotNow_copilot");
        ((IInvokeProvider)new ButtonAutomationPeer(notNow).GetPattern(PatternInterface.Invoke)).Invoke();
        var gone = await Until(() => settings.AgentOffers.Count == 0 && Find<AgentOfferGroup>(view.Offers).Count == 0, TimeSpan.FromSeconds(5));
        await Task.Delay(400);
        var rowAgain = Buttons(view.AgentRows).FirstOrDefault(b => b.Id == "SetUpAgent_copilot").Button;
        var dotGone = ById<InfoBadge>(settingsItem, "NavSettingsDot") is { Visibility: Visibility.Collapsed }
            && FrameworkElementAutomationPeer.CreatePeerForElement(settingsItem)?.GetName() == "Settings";
        check(gone && dotGone && rowAgain is { Visibility: Visibility.Visible } && FrameworkElementAutomationPeer.CreatePeerForElement(rowAgain)?.GetName() == "Set up Copilot CLI",
            $"takes the card and the dot away on Not now and gives the row its Set up back (card gone: {gone}; dot gone: {dotGone}; row: {(rowAgain is null ? "absent" : rowAgain.Visibility.ToString())})");
        var landed = await Until(() => FocusManager.GetFocusedElement(window.RootElement.XamlRoot) is FrameworkElement f && AutomationProperties.GetAutomationId(f) == "CheckAgents", TimeSpan.FromSeconds(3));
        check(landed, $"moves focus to the first control below when there is no next card ({(FocusManager.GetFocusedElement(window.RootElement.XamlRoot) is FrameworkElement now ? AutomationProperties.GetAutomationId(now) : "nothing")})");
    }
}
