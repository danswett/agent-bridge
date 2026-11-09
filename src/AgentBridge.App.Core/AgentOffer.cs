using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// The card at the top of Settings for an agent installed after the bridge (#231, ui.md §6.9):
// "Claude Code 1.0.3 is installed · Set it up to see its sessions in Slipgates", with Set up and
// Not now. It is a group named for its line, its buttons named for the agent and with ids of their
// own (§9). Set up here is the agent row's Set up, and only the agent's id travels: the same
// confirmation and the same setup run for either.
public sealed class AgentOffer(AgentReadinessRow row, bool canManage)
{
    public string Id => row.Id;
    public string Version => row.Version ?? "";
    // What the dismissal is for: another version is a new offer.
    internal string Key => $"{Id}@{Version}";
    public string Name => $"{row.Label} {Version} is installed";
    public string Text => $"{Name} · Set it up to see its sessions in {Product.Name}";
    public string SetUpName => $"Set up {row.Label}";
    public string NotNowName => $"Not now, {row.Label}";
    public bool CanSetUp => canManage;
    public string GroupId => $"AgentOffer_{Id}";
    public string SetUpId => $"AgentOfferSetUp_{Id}";
    public string NotNowId => $"AgentOfferNotNow_{Id}";
}
