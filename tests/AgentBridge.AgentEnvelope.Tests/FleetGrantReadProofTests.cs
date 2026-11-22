using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.AgentEnvelope;
using AgentBridge.Roster;
using Xunit;

public sealed class FleetGrantReadProofTests : IDisposable
{
    readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    readonly FleetGrantProofContext context = new("account", "default",
        "0123456789abcdef0123456789abcdef", "grant.read", Guid.NewGuid());
    byte[] Point
    {
        get
        {
            var point = key.ExportParameters(false).Q;
            return [4, .. point.X!, .. point.Y!];
        }
    }
    FleetGrantReadResult Seal(FleetGrantReadProblem problem) =>
        FleetGrantReadProof.Seal(FleetGrantReadProof.Reply(problem), context, new EcdsaEnvelopeSigner(key));
    public void Dispose() => key.Dispose();

    [Theory]
    [InlineData(FleetGrantReadProblem.None, 0)]
    [InlineData(FleetGrantReadProblem.RotationRequired, 1)]
    [InlineData(FleetGrantReadProblem.Unavailable, 2)]
    [InlineData(FleetGrantReadProblem.Other, 3)]
    public void A_sealed_read_reply_verifies_to_exactly_its_typed_problem_in_a_fixed_layout(
        FleetGrantReadProblem problem, byte code)
    {
        var result = Seal(problem);
        var expected = Framing.F(Framing.Label("agent-bridge fleet read problem v1"), [code]);
        Assert.Equal(expected, FleetGrantProof.Verify(result.Proof, Point, context));
        Assert.Equal(problem, FleetGrantReadProof.Verify(result, Point, context));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"problem\":null}")]
    [InlineData("{\"problem\":0}")]
    [InlineData("{\"problem\":true}")]
    [InlineData("{\"problem\":\"\"}")]
    [InlineData("{\"problem\":\"Unknown\"}")]
    [InlineData("{\"problem\":\"none \"}")]
    [InlineData("{\"problem\":\"NONE\"}")]
    [InlineData("{\"problem\":\"none\",\"extra\":1}")]
    [InlineData("{\"problem\":\"none\",\"problem\":\"other\"}")]
    [InlineData("[\"none\"]")]
    [InlineData("\"none\"")]
    public void Missing_unknown_wrong_type_or_extra_problem_values_are_refused_without_a_default(string json)
    {
        using var parsed = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() =>
            FleetGrantReadProof.Seal(parsed.RootElement, context, new EcdsaEnvelopeSigner(key)));
    }

    [Fact]
    public void Tampering_with_the_sealed_problem_byte_fails_signature_verification()
    {
        var result = Seal(FleetGrantReadProblem.RotationRequired);
        var bytes = Codec.FromBase64Url(result.Proof.Body, "body");
        bytes[^1] ^= 1;
        result = result with { Proof = result.Proof with { Body = Codec.Base64Url(bytes) } };
        Assert.Throws<InvalidDataException>(() => FleetGrantProof.Verify(result.Proof, Point, context));
        Assert.Throws<InvalidDataException>(() => FleetGrantReadProof.Verify(result, Point, context));
    }

    [Fact]
    public void An_unsealed_problem_change_or_missing_proof_cannot_be_accepted()
    {
        var result = Seal(FleetGrantReadProblem.RotationRequired);
        Assert.Throws<InvalidDataException>(() => FleetGrantReadProof.Verify(
            result with { Reply = FleetGrantReadProof.Reply(FleetGrantReadProblem.None) }, Point, context));
        Assert.Throws<InvalidDataException>(() =>
            FleetGrantReadProof.Verify(result with { Proof = null! }, Point, context));
        Assert.Throws<InvalidDataException>(() => FleetGrantReadProof.Verify(null!, Point, context));
    }

    [Fact]
    public void A_read_seal_is_bound_to_its_account_profile_receiver_action_and_fresh_request()
    {
        var result = Seal(FleetGrantReadProblem.Unavailable);
        foreach (var other in new[]
        {
            context with { Account = "other" }, context with { Profile = "other" },
            context with { Receiver = "fedcba9876543210fedcba9876543210" },
            context with { Action = "grant.confirmIssue" }, context with { Request = Guid.NewGuid() },
        })
        {
            Assert.Throws<InvalidDataException>(() => FleetGrantReadProof.Verify(result, Point, other));
        }
    }

    [Fact]
    public void A_seal_made_for_another_action_cannot_be_read_as_a_problem_and_nothing_else_may_seal_as_read()
    {
        var issue = context with { Action = "grant.confirmIssue" };
        Assert.Throws<InvalidDataException>(() => FleetGrantReadProof.Seal(
            FleetGrantReadProof.Reply(FleetGrantReadProblem.None), issue, new EcdsaEnvelopeSigner(key)));
        var raw = FleetGrantProof.Seal(Framing.F(Framing.Label("agent-bridge fleet read problem v1"), [1]),
            context, new EcdsaEnvelopeSigner(key));
        Assert.Throws<InvalidDataException>(() => FleetGrantReadProof.Verify(
            new(FleetGrantReadProof.Reply(FleetGrantReadProblem.None), raw), Point, context));
        Assert.Throws<ArgumentOutOfRangeException>(() => FleetGrantReadProof.Reply((FleetGrantReadProblem)4));
    }

    [Fact]
    public void A_wrong_host_key_wrong_label_trailing_byte_or_undefined_reply_is_refused_as_invalid_data()
    {
        var result = Seal(FleetGrantReadProblem.RotationRequired);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var point = other.ExportParameters(false).Q;
        Assert.Throws<InvalidDataException>(() =>
            FleetGrantReadProof.Verify(result, [4, .. point.X!, .. point.Y!], context));
        foreach (var body in new[]
        {
            Framing.F(Framing.Label("agent-bridge fleet read problem v2"), [1]),
            [.. Framing.F(Framing.Label("agent-bridge fleet read problem v1"), [1]), 0],
        })
        {
            var raw = FleetGrantProof.Seal(body, context, new EcdsaEnvelopeSigner(key));
            Assert.Throws<InvalidDataException>(() =>
                FleetGrantReadProof.Verify(result with { Proof = raw }, Point, context));
        }
        Assert.Throws<InvalidDataException>(() =>
            FleetGrantReadProof.Verify(result with { Reply = default }, Point, context));
        Assert.Throws<InvalidDataException>(() =>
            FleetGrantReadProof.Seal(default, context, new EcdsaEnvelopeSigner(key)));
        using var extra = JsonDocument.Parse("{\"problem\":\"rotationRequired\",\"extra\":1}");
        Assert.Throws<InvalidDataException>(() =>
            FleetGrantReadProof.Verify(result with { Reply = extra.RootElement }, Point, context));
    }
}
