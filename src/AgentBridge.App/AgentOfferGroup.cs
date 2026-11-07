using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

// The card for an agent installed after the bridge (#231; ui.md §9): a group named for its line
// ("Claude Code 1.0.3 is installed"), with its two buttons inside it. A panel is not in the UI
// Automation tree on its own, so without this a screen reader would meet two buttons and no line.
public sealed partial class AgentOfferGroup : Grid
{
    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    sealed partial class Peer(AgentOfferGroup owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => nameof(AgentOfferGroup);
    }
}
