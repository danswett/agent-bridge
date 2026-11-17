using AgentBridge.AgentEnvelope;
using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Lite;

public sealed class ReplyIngress
{
    readonly bool enabled;
    ReplyIngress(bool enabled) => this.enabled = enabled;

    public static ReplyIngress Initialize(string profile, Action<string> log)
    {
        try { ReplyClaimStore.EnsureInitialized(profile); return new(true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log($"reply delivery is held because its claim store could not be initialized: {e.Message}");
            return new(false);
        }
    }

    public ILiteDeliver Lite(ILiteDeliver delivery) => enabled ? delivery : new HeldLiteDelivery();
    public Func<HaReply, CancellationToken, Task<ReplyResult>>? HomeAssistant(
        Func<HaReply, CancellationToken, Task<ReplyResult>>? delivery) => enabled ? delivery : null;

    sealed class HeldLiteDelivery : ILiteDeliver
    {
        public Task<LiteDelivery> DeliverAsync(VerifiedEnvelope envelope, string text, CancellationToken ct) =>
            Task.FromResult(new LiteDelivery(false, "claimUnavailable"));
    }
}
