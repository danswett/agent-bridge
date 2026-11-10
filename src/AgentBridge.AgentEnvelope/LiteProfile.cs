using AgentBridge.Roster;
namespace AgentBridge.AgentEnvelope;

// The sender of a lite envelope: an agent session's AP1 credential, on a host whose account id
// and relay device key (`Add`, 32 bytes as base64url) the relay has bound to it.
public sealed record LiteSender(string Account, string Machine, string Add, string Session, string Instance, string Credential, string Agent, string Launcher);

public sealed record LiteTarget(string Machine, string Session, string Instance);

// Why Check refused a lite envelope; each has its own code. The envelope library's own codes
// (lifetime, signature, data) still apply to what it checks.
public static class LiteRefusal
{
    public const string Markers = "lite-markers";
    public const string Account = "lite-account";
    public const string Action = "lite-action";
    public const string OEligible = "lite-oeligible";
}

// AP5-lite (#728, slice L2): the temporary, own-agents-only profile of the AP4 envelope. There is
// no roster, so nothing binds a generation or a head to the sender: a lite envelope carries fixed
// all-zero markers instead, which can never be a real roster generation, and so can never verify
// in the roster path (RequireRoster). Only session.reply, same account, provenance unknown. Library only.
public static class LiteProfile
{
    public const string Generation = "00000000000000000000000000000000";
    public static readonly string Head = Codec.Base64Url(new byte[32]);
    public const string Action = "session.reply";
    public const int MaxClockSkewSeconds = 5;

    public static byte[] Seal(LiteSender sender, LiteTarget target, string request, ReadOnlySpan<byte> data, long nowSeconds, IEnvelopeSigner signer, int lifetimeSeconds = EnvelopeBody.MaxLifetimeSeconds)
    {
        if (lifetimeSeconds is < 1 or > EnvelopeBody.MaxLifetimeSeconds) { throw new EnvelopeException(Refusal.Lifetime, "expiresAt"); }
        var body = new EnvelopeBody(
            request, sender.Account, Generation, [Head],
            new AgentPrincipal(sender.Machine, sender.Add, sender.Session, sender.Instance, sender.Credential, sender.Agent, sender.Launcher),
            new AgentChain(request, nowSeconds, null, 1, 1, [new ChainSender(sender.Account, sender.Machine, sender.Session)]),
            new AgentProvenance(null, OEligible.No, null),
            Action,
            new AgentTarget(sender.Account, target.Machine, target.Session, target.Instance),
            EnvelopeBody.HashOf(data), nowSeconds, nowSeconds + lifetimeSeconds);
        return Envelope.Seal(body, signer);
    }

    // What a receiver does with a lite envelope before acting on it. `senderPoint` is the
    // pinned key of the device the sender's `principal.add` names (the caller resolves it); `data`
    // is the text the signature must bind. Throws EnvelopeException on any refusal.
    public static VerifiedEnvelope Check(UnverifiedEnvelope envelope, ReadOnlySpan<byte> senderPoint, string receiverAccount, ReadOnlySpan<byte> data, long nowSeconds)
    {
        var b = envelope.Body;
        if (!IsLite(b)) { throw new EnvelopeException(LiteRefusal.Markers, "generation"); }
        if (b.Account != receiverAccount || b.Target.Account != receiverAccount) { throw new EnvelopeException(LiteRefusal.Account, "account"); }
        if (b.Action != Action) { throw new EnvelopeException(LiteRefusal.Action, "action"); }
        if (b.Provenance.OEligible != OEligible.No || b.Provenance.Origins is not null) { throw new EnvelopeException(LiteRefusal.OEligible, "provenance"); }
        var verified = envelope.Verify(senderPoint, nowSeconds);
        if (b.IssuedAt > nowSeconds + MaxClockSkewSeconds) { throw new EnvelopeException(Refusal.Lifetime, "issuedAt"); }
        if (!b.BindsData(data)) { throw new EnvelopeException(Refusal.Data, "dataSha256"); }
        return verified;
    }

    public static bool IsLite(EnvelopeBody b) => b.Generation == Generation && b.Heads.Count == 1 && b.Heads[0] == Head;

    // The roster path's check of a body's generation and heads: a lite envelope's markers are not
    // a real generation, so it never passes.
    public static void RequireRoster(EnvelopeBody b, string currentGeneration, IReadOnlyCollection<string> knownHeads)
    {
        if (IsLite(b) || b.Generation != currentGeneration || !b.Heads.All(knownHeads.Contains)) { throw new EnvelopeException(Refusal.Value, "generation"); }
    }
}
