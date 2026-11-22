using System.Security.Cryptography;
using AgentBridge.AgentEnvelope;
using AgentBridge.Roster;
using Xunit;

public sealed class FleetGrantBindingTests : IDisposable
{
    readonly ECDsa signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    readonly ECDsa receiver = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    static byte[] Point(ECDsa key)
    {
        var point = key.ExportParameters(false).Q;
        return [4, .. point.X!, .. point.Y!];
    }
    FleetGrantBinding Binding => new("account", "default", Ids.Device(Point(receiver)),
        new('a', 32), Codec.Base64Url(Point(signer)), Codec.Base64Url(Point(receiver)));
    FleetGrantProofContext Context(FleetGrantBinding binding) =>
        new(binding.Account, binding.Profile, binding.Receiver, "grant.binding", Guid.NewGuid());
    public void Dispose() { signer.Dispose(); receiver.Dispose(); }

    [Fact]
    public void A_sealed_binding_returns_only_the_verified_receiver_key_in_the_fixed_layout()
    {
        var binding = Binding;
        var context = Context(binding);
        var result = FleetGrantBindingProof.Seal(binding, context, new EcdsaEnvelopeSigner(signer));
        var bytes = Framing.F(Framing.Label("agent-bridge fleet receiver binding v1"),
            Framing.Utf8(binding.Account), Framing.Utf8(binding.Profile),
            Framing.Utf8(binding.Receiver), Framing.Utf8(binding.RosterGeneration),
            Point(receiver), SHA256.HashData(Point(signer)), Framing.Utf8(context.Request.ToString("D")));
        Assert.Equal(bytes, FleetGrantProof.Verify(result.Proof, Point(signer), context));
        Assert.Equal(Point(receiver), FleetGrantBindingProof.Verify(result, Point(signer), context));
    }

    [Fact]
    public void Tampering_with_the_receiver_key_inside_the_sealed_bytes_fails_signature_verification()
    {
        var binding = Binding;
        var context = Context(binding);
        var result = FleetGrantBindingProof.Seal(binding, context, new EcdsaEnvelopeSigner(signer));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var tampered = Framing.F(Framing.Label(FleetGrantBindingProof.Domain),
            Framing.Utf8(binding.Account), Framing.Utf8(binding.Profile),
            Framing.Utf8(binding.Receiver), Framing.Utf8(binding.RosterGeneration),
            Point(other), SHA256.HashData(Point(signer)), Framing.Utf8(context.Request.ToString("D")));
        result = result with { Proof = result.Proof with { Body = Codec.Base64Url(tampered) } };
        Assert.Throws<InvalidDataException>(() => FleetGrantProof.Verify(result.Proof, Point(signer), context));
        Assert.Throws<InvalidDataException>(() => FleetGrantBindingProof.Verify(result, Point(signer), context));
    }

    [Theory]
    [InlineData("receiverKey")]
    [InlineData("hostSign")]
    [InlineData("generation")]
    public void Unsealed_key_or_roster_metadata_changes_are_refused(string change)
    {
        var binding = Binding;
        var context = Context(binding);
        var result = FleetGrantBindingProof.Seal(binding, context, new EcdsaEnvelopeSigner(signer));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        binding = change switch
        {
            "receiverKey" => binding with { ReceiverKey = Codec.Base64Url(Point(other)) },
            "hostSign" => binding with { HostSign = Codec.Base64Url(Point(other)) },
            _ => binding with { RosterGeneration = new('b', 32) },
        };
        Assert.Throws<InvalidDataException>(() =>
            FleetGrantBindingProof.Verify(result with { Binding = binding }, Point(signer), context));
    }

    [Theory]
    [InlineData("receiverKey", null)]
    [InlineData("receiverKey", "")]
    [InlineData("receiverKey", "malformed")]
    [InlineData("receiverKey", "invalid-point")]
    [InlineData("hostSign", null)]
    [InlineData("hostSign", "")]
    [InlineData("hostSign", "malformed")]
    public void Missing_or_malformed_binding_keys_are_refused_before_signing(string field, string? value)
    {
        var binding = Binding;
        var context = Context(binding);
        if (value == "invalid-point") { value = Codec.Base64Url(new byte[65]); }
        binding = field == "receiverKey"
            ? binding with { ReceiverKey = value! } : binding with { HostSign = value! };
        Assert.Throws<InvalidDataException>(() =>
            FleetGrantBindingProof.Seal(binding, context, new EcdsaEnvelopeSigner(signer)));
    }

    [Fact]
    public void A_binding_refuses_host_key_substitution_and_stale_or_wrong_action_context()
    {
        var binding = Binding;
        var context = Context(binding);
        var result = FleetGrantBindingProof.Seal(binding, context, new EcdsaEnvelopeSigner(signer));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<InvalidDataException>(() => FleetGrantBindingProof.Verify(result, Point(other), context));
        Assert.Throws<InvalidDataException>(() =>
            FleetGrantBindingProof.Verify(result, Point(signer), context with { Request = Guid.NewGuid() }));
        Assert.Throws<InvalidDataException>(() =>
            FleetGrantBindingProof.Verify(result, Point(signer), context with { Request = Guid.Empty }));
        Assert.Throws<InvalidDataException>(() =>
            FleetGrantBindingProof.Verify(result, Point(signer), context with { Action = "grant.read" }));
        Assert.Throws<InvalidDataException>(() =>
            FleetGrantBindingProof.Verify(result with { Proof = null! }, Point(signer), context));
        var colliding = binding with { ReceiverKey = binding.HostSign, Receiver = Ids.Device(Point(signer)) };
        Assert.Throws<InvalidDataException>(() => FleetGrantBindingProof.Seal(
            colliding, Context(colliding), new EcdsaEnvelopeSigner(signer)));
    }
}
