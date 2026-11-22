using AgentBridge.Roster;

namespace AgentBridge.AgentEnvelope;

public sealed record FleetGrantProofContext(string Account, string Profile, string Receiver,
    string Action, Guid Request);
public sealed record FleetGrantSignedPayload(string Body, string Signature);

public static class FleetGrantProof
{
    const int MaxBody = 30 * 1024;
    static readonly byte[] Domain = Framing.Label("agent-bridge fleet receiver proof v1");

    public static FleetGrantSignedPayload Seal(ReadOnlySpan<byte> body, FleetGrantProofContext context,
        IEnvelopeSigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);
        if (body.Length is < 1 or > MaxBody) { throw new ArgumentOutOfRangeException(nameof(body)); }
        if (!Valid(context)) { throw new ArgumentException("receiver-proof-context", nameof(context)); }
        var payload = body.ToArray();
        var signature = P256.Normalize(signer.Sign(Frame(payload, context)));
        return new(Codec.Base64Url(payload), Codec.Base64Url(signature));
    }

    public static byte[] Verify(FleetGrantSignedPayload proof, ReadOnlySpan<byte> trustedReceiverHostSign,
        FleetGrantProofContext expected)
    {
        if (proof is null || !Valid(expected) || !P256.IsPoint(trustedReceiverHostSign)
            || proof.Body is not { Length: > 0 and <= MaxBody * 4 / 3 }
            || proof.Signature is not { Length: > 0 and <= 96 }
            || !Codec.TryBase64Url(proof.Body, out var body) || body.Length is < 1 or > MaxBody
            || !Codec.TryBase64Url(proof.Signature, out var signature))
        { throw new InvalidDataException("receiver-proof"); }
        var parts = P256.ReadDer(signature);
        if (parts is null || !P256.IsLowS(parts.Value.S)
            || !P256.Verify(trustedReceiverHostSign, Frame(body, expected), signature))
        { throw new InvalidDataException("receiver-proof"); }
        return body;
    }

    static byte[] Frame(byte[] body, FleetGrantProofContext context) => Framing.F(Domain,
        Framing.Utf8(context.Account), Framing.Utf8(context.Profile), Framing.Utf8(context.Receiver),
        Framing.Utf8(context.Action), Framing.Utf8(context.Request.ToString("D")), body);

    static bool Valid(FleetGrantProofContext? context) => context is not null
        && Text(context.Account, 256) && Text(context.Profile, 64)
        && context.Receiver is { Length: 32 }
        && context.Receiver.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
        && context.Request != Guid.Empty
        && context.Action is "grant.read" or "grant.prepareIssue" or "grant.confirmIssue"
            or "grant.prepareRevoke" or "grant.confirmRevoke" or "grant.revoke";

    static bool Text(string? value, int max) => value is { Length: > 0 } && value.Length <= max
        && value.All(c => c is >= '!' and <= '~');
}
