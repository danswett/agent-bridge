using AgentBridge.Protocol;

namespace AgentBridge.Host;

// Clear and send (#804, slice 3b): what a reply is given before it reaches ReplyService, and what its refusal is allowed to say after.
// A clear is acted on only through the relay, from a device ReplyClearConfirmations accepts; the local pipe, MCP, Lite and Home Assistant
// lose it, and nothing they send can supply a confirmation or a draft (those are host-only fields).
internal static class ReplyClearWiring
{
    public static ReplyRequest Apply(ReplyRequest request, string? relayPeer, ReplyClearConfirmations? verifier) =>
        relayPeer is null || verifier is null || request.Clear is null
            ? request with { Clear = null, ClearConfirmation = null, ClearDraftSource = null, ClearDraftFromScreen = false }
            // The owner's option A: no other draft source than the screen read in the typing slot.
            : request with { ClearConfirmation = verifier.For(relayPeer, request), ClearDraftSource = null, ClearDraftFromScreen = true };

    // ReplyService marks a box it could clear; the policy and the device decide whether this caller is offered it.
    public static ReplyResult Offer(ReplyResult result, string? relayPeer, ReplyClearConfirmations? verifier) =>
        result.Input is { Clearable: true } input && !(relayPeer is not null && verifier?.Offers(relayPeer) == true)
            ? result with { Input = input with { Clearable = false } }
            : result;
}
