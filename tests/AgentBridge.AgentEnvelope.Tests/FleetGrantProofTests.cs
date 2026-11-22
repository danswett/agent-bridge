using System.Security.Cryptography;
using System.Text;
using AgentBridge.AgentEnvelope;
using AgentBridge.Roster;
using Xunit;

public sealed class FleetGrantProofTests : IDisposable
{
    readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    readonly FleetGrantProofContext context = new("account", "default",
        "0123456789abcdef0123456789abcdef", "grant.read", Guid.NewGuid());
    static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"generation\":1,\"hour\":2}");
    byte[] Point
    {
        get
        {
            var point = key.ExportParameters(false).Q;
            return [4, .. point.X!, .. point.Y!];
        }
    }

    public void Dispose() => key.Dispose();

    [Fact]
    public void Receiver_proof_verifies_the_exact_snapshot_bytes()
    {
        var proof = FleetGrantProof.Seal(Body, context, new EcdsaEnvelopeSigner(key));
        Assert.Equal(Body, FleetGrantProof.Verify(proof, Point, context));
    }

    [Fact]
    public void Binding_context_is_valid_for_a_fresh_sealed_receiver_reply()
    {
        var binding = context with { Action = "grant.binding" };
        var proof = FleetGrantProof.Seal(Body, binding, new EcdsaEnvelopeSigner(key));
        Assert.Equal(Body, FleetGrantProof.Verify(proof, Point, binding));
    }

    [Theory]
    [InlineData("body")]
    [InlineData("signature")]
    [InlineData("account")]
    [InlineData("profile")]
    [InlineData("receiver")]
    [InlineData("action")]
    [InlineData("request")]
    public void Tampering_or_reusing_a_proof_for_another_request_is_refused(string change)
    {
        var proof = FleetGrantProof.Seal(Body, context, new EcdsaEnvelopeSigner(key));
        var expected = context;
        switch (change)
        {
            case "body": proof = proof with { Body = Codec.Base64Url(Encoding.UTF8.GetBytes("{}")) }; break;
            case "signature":
                var signature = Codec.FromBase64Url(proof.Signature, "signature");
                signature[^1] ^= 1;
                proof = proof with { Signature = Codec.Base64Url(signature) };
                break;
            case "account": expected = context with { Account = "other" }; break;
            case "profile": expected = context with { Profile = "other" }; break;
            case "receiver": expected = context with { Receiver = new('f', 32) }; break;
            case "action": expected = context with { Action = "grant.revoke" }; break;
            case "request": expected = context with { Request = Guid.NewGuid() }; break;
        }
        Assert.Throws<InvalidDataException>(() => FleetGrantProof.Verify(proof, Point, expected));
    }

    [Fact]
    public void A_foreign_key_or_a_signature_outside_the_receiver_domain_is_refused()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var foreign = FleetGrantProof.Seal(Body, context, new EcdsaEnvelopeSigner(other));
        Assert.Throws<InvalidDataException>(() => FleetGrantProof.Verify(foreign, Point, context));
        var raw = new FleetGrantSignedPayload(Codec.Base64Url(Body),
            Codec.Base64Url(P256.Normalize(key.SignData(Body, HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence))));
        Assert.Throws<InvalidDataException>(() => FleetGrantProof.Verify(raw, Point, context));
    }

    [Fact]
    public void Oversized_snapshots_are_refused_before_signing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FleetGrantProof.Seal(
            new byte[30 * 1024 + 1], context, new EcdsaEnvelopeSigner(key)));
    }
}
