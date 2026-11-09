using AgentBridge.Host;

namespace AgentBridge.Host.Lite;

// TEMPORARY copy of the shared AP5-lite contracts (interface note, #728). SG Lite Builder 4 (L4)
// commits the final file; delete this one when that merges.
public enum LiteKind { Delivered, Refused, Undeliverable, Unconfirmed, Off }

public sealed record LiteOutcome(LiteKind Kind, string? Code, string Request);

public sealed record LiteSendInput(string Machine, string Session, string Text);

public interface ILiteSender
{
    Task<LiteOutcome> SendAsync(AgentCredential caller, LiteSendInput input, CancellationToken ct);
}
