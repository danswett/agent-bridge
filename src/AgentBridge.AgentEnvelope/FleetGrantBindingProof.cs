using AgentBridge.Roster;

namespace AgentBridge.AgentEnvelope;

public static class FleetGrantBindingProof
{
    public const string Domain = "agent-bridge fleet receiver binding v1";

    public static FleetGrantBindingResult Seal(FleetGrantBinding binding,
        FleetGrantProofContext context, IEnvelopeSigner signer) =>
        new(binding, FleetGrantProof.Seal(Bytes(binding, context), context, signer));

    // The existing trusted local pipe authenticates this key's metadata; the seal alone does not.
    public static byte[] Verify(FleetGrantBindingResult result, ReadOnlySpan<byte> trustedHostSign,
        FleetGrantProofContext expected)
    {
        if (result is null || !P256.IsPoint(trustedHostSign))
        { throw new InvalidDataException("receiver-binding"); }
        var expectedBody = Bytes(result.Binding, expected);
        var hostSign = Codec.FromBase64Url(result.Binding.HostSign, "hostSign");
        if (!trustedHostSign.SequenceEqual(hostSign))
        { throw new InvalidDataException("receiver-binding"); }
        var body = FleetGrantProof.Verify(result.Proof, trustedHostSign, expected);
        if (!body.AsSpan().SequenceEqual(expectedBody))
        { throw new InvalidDataException("receiver-binding"); }
        return Codec.FromBase64Url(result.Binding.ReceiverKey, "receiverKey");
    }

    public static byte[] Bytes(FleetGrantBinding binding, FleetGrantProofContext context)
    {
        if (binding is null || !FleetGrantProof.Valid(context) || context.Action != "grant.binding"
            || binding.Account != context.Account || binding.Profile != context.Profile
            || binding.Receiver != context.Receiver
            || binding.RosterGeneration is not { Length: 32 } || !Codec.IsId(binding.RosterGeneration)
            || binding.ReceiverKey is not { Length: 87 } || binding.HostSign is not { Length: 87 }
            || !Codec.TryBase64Url(binding.ReceiverKey, out var receiverKey) || !P256.IsPoint(receiverKey)
            || !Codec.TryBase64Url(binding.HostSign, out var hostSign) || !P256.IsPoint(hostSign)
            || Ids.Device(receiverKey) != binding.Receiver || Ids.Device(hostSign) == binding.Receiver)
        { throw new InvalidDataException("receiver-binding"); }
        return Framing.F(Framing.Label(Domain), Framing.Utf8(binding.Account),
            Framing.Utf8(binding.Profile), Framing.Utf8(binding.Receiver),
            Framing.Utf8(binding.RosterGeneration), receiverKey, Framing.Sha256(hostSign),
            Framing.Utf8(context.Request.ToString("D")));
    }
}
