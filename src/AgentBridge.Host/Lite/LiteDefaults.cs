using AgentBridge.AgentEnvelope;
using AgentBridge.Host.Core.AgentMessaging;

namespace AgentBridge.Host.Lite;

// Stand-ins until the slices that own them merge: L5 (delivery into a session) and L2b (the pinned peers).
public sealed class NoLiteDelivery : ILiteDeliver
{
    public Task<LiteDelivery> DeliverAsync(VerifiedEnvelope envelope, string text, CancellationToken ct) =>
        Task.FromResult(new LiteDelivery(false, "notReplyTarget"));
}

public sealed class NoLitePeers : ILitePeers
{
    public bool TryFind(string deviceId, out LitePeer peer) { peer = null!; return false; }
    public IReadOnlyList<LitePeer> All() => [];
}

// The mode as config.json says it, read afresh on every message (no restart). The kill epoch and turning off
// from a peer arrive with L6.
public sealed class SettingsLiteMode(Func<AgentMessagingSettings> settings) : ILiteMode
{
    public bool On => settings().RemoteEnabled;
    public long KillEpoch => 0;
    public void TurnOff(string why) { }
    public void RaiseEpoch(long epoch) { }
}
