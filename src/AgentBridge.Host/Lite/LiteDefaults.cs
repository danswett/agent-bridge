using AgentBridge.AgentEnvelope;

namespace AgentBridge.Host.Lite;

// Stand-ins until L5 (delivery) and L2b (pinned peers) merge.
public sealed class NoLiteDelivery : ILiteDeliver
{
    public Task<LiteDelivery> DeliverAsync(VerifiedEnvelope envelope, string text, CancellationToken ct) =>
        Task.FromResult(new LiteDelivery(false, "notReplyTarget"));
}

public sealed class NoLitePeers : ILitePeers
{
    public bool TryFind(string deviceId, out LitePeer peer) { peer = null!; return false; }  public IReadOnlyList<LitePeer> All() => [];
}
