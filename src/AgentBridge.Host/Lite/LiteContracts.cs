using AgentBridge.AgentEnvelope;

namespace AgentBridge.Host.Lite;

// AP5-lite (#728) shared contracts: the one file every lite slice (L2b, L3, L4, L5, L6, L7, L8) codes against.
public enum LiteKind { Delivered, Refused, Undeliverable, Unconfirmed, Off, Pending, Queued }
public sealed record LiteOutcome(LiteKind Kind, string? Code, string Request);          // Code = receiver's refusal code when Refused
public sealed record LiteSendInput(string Machine, string Session, string Text);         // Text max 4096 chars
public interface ILiteMode { bool On { get; } long KillEpoch { get; } void TurnOff(string why); void RaiseEpoch(long epoch); } // wraps L1 settings; never turns itself ON from a peer
public sealed record LitePeer(string DeviceId, byte[] SignPoint, string Name);           // SignPoint = peer's 65-byte P-256 agent signing point, pinned
public interface ILitePeers { bool TryFind(string deviceId, out LitePeer peer); IReadOnlyList<LitePeer> All(); }  // L2b implements
public sealed record LiteDelivery(bool Typed, string? Code)
{
    public LiteKind? Outcome { get; init; }
}
internal interface ILiteReplayQuery
{
    Task<LiteDelivery> QueryAsync(VerifiedEnvelope envelope, string text, CancellationToken ct);
}
public interface ILiteDeliver { Task<LiteDelivery> DeliverAsync(VerifiedEnvelope envelope, string text, CancellationToken ct); } // L5 implements; L4 default stub returns (false, "notReplyTarget")
public interface ILiteSender { Task<LiteOutcome> SendAsync(AgentCredential caller, LiteSendInput input, CancellationToken ct); }
// A receipt lookup's answer (docs/protocol/agent-lite.md "Receipt lookup", #3875): the receiver's recorded outcome of the first attempt, or `unknown`
// (no record, or the lookup itself failed: Code then starts "lookup:"). Only an answered receipt can say `refused`.
public sealed record LiteReceiptReply(string Outcome, string? Code, string Request);
public interface ILiteReceipts { Task<LiteReceiptReply?> ReceiptAsync(AgentCredential caller, string request, CancellationToken ct); }  // L3 implements; L7 calls
