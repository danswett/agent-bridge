using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.AgentEnvelope;
using AgentBridge.App.Core.Grants;
using AgentBridge.Roster;
using Xunit;

public sealed class FleetGrantReceiptTests : IDisposable
{
    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web);
    static readonly Guid Id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
    readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    readonly FleetGrantProofContext context = new("account", "default",
        "0123456789abcdef0123456789abcdef", "grant.confirmIssue", Guid.NewGuid());
    byte[] Point
    {
        get
        {
            var point = key.ExportParameters(false).Q;
            return [4, .. point.X!, .. point.Y!];
        }
    }
    JsonElement Receipt(bool rotated) =>
        JsonSerializer.SerializeToElement(new GrantReceipt(Id, 1, context.Receiver, new('a', 64), rotated), Wire);
    public void Dispose() => key.Dispose();

    [Theory]
    [InlineData("")]
    [InlineData(",\"rotated\":null")]
    [InlineData(",\"rotated\":\"true\"")]
    [InlineData(",\"rotated\":1")]
    public void Missing_or_wrong_type_rotation_flags_cannot_be_read_as_receipts(string flag)
    {
        var json = "{\"id\":\"0f8fad5b-d9cb-469f-a165-70867728950e\",\"generation\":1,"
            + "\"receiver\":\"0123456789abcdef0123456789abcdef\",\"digest\":\"" + new string('a', 64) + "\"" + flag + "}";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GrantReceipt>(json, Wire));
        using var parsed = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() =>
            FleetGrantReceiptProof.Seal(parsed.RootElement, context, new EcdsaEnvelopeSigner(key)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_authenticated_receipt_preserves_only_its_explicit_rotation_result(bool rotated)
    {
        var result = FleetGrantReceiptProof.Seal(Receipt(rotated), context, new EcdsaEnvelopeSigner(key));
        var number = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(number, 1);
        var expected = Framing.F(Framing.Label("agent-bridge fleet issue receipt v1"),
            Framing.Utf8(Id.ToString("D")), number, Framing.Utf8(context.Receiver),
            Convert.FromHexString(new string('a', 64)), [rotated ? (byte)1 : (byte)0]);
        Assert.Equal(expected, FleetGrantProof.Verify(result.Proof, Point, context));
        var receipt = FleetGrantReceiptProof.Verify(result, Point, context).Deserialize<GrantReceipt>(Wire)!;
        Assert.Equal(rotated, receipt.Rotated);
        Assert.Equal(Id, receipt.Id);
    }

    [Fact]
    public void Tampering_with_the_sealed_rotation_byte_fails_signature_verification()
    {
        var result = FleetGrantReceiptProof.Seal(Receipt(true), context, new EcdsaEnvelopeSigner(key));
        var bytes = Codec.FromBase64Url(result.Proof.Body, "body");
        bytes[^1] ^= 1;
        result = result with { Proof = result.Proof with { Body = Codec.Base64Url(bytes) } };
        Assert.Throws<InvalidDataException>(() => FleetGrantProof.Verify(result.Proof, Point, context));
        Assert.Throws<InvalidDataException>(() => FleetGrantReceiptProof.Verify(result, Point, context));
    }

    [Fact]
    public void An_unsealed_rotation_change_or_missing_proof_cannot_be_accepted()
    {
        var result = FleetGrantReceiptProof.Seal(Receipt(true), context, new EcdsaEnvelopeSigner(key));
        Assert.Throws<InvalidDataException>(() =>
            FleetGrantReceiptProof.Verify(result with { Receipt = Receipt(false) }, Point, context));
        Assert.Throws<InvalidDataException>(() =>
            FleetGrantReceiptProof.Verify(result with { Proof = null! }, Point, context));
        Assert.Throws<InvalidDataException>(() =>
            FleetGrantReceiptProof.Verify(result, Point, context with { Action = "grant.read" }));
        Assert.Throws<InvalidDataException>(() =>
            FleetGrantReceiptProof.Verify(result, Point, context with { Request = Guid.NewGuid() }));
    }

    [Theory]
    [InlineData(",\"rotated\":true,\"extra\":false")]
    [InlineData(",\"rotated\":true,\"rotated\":false")]
    public void Unknown_or_duplicate_receipt_fields_cannot_enter_the_seal(string flag)
    {
        var json = "{\"id\":\"" + Id + "\",\"generation\":1,\"receiver\":\"" + context.Receiver
            + "\",\"digest\":\"" + new string('a', 64) + "\"" + flag + "}";
        using var parsed = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() =>
            FleetGrantReceiptProof.Seal(parsed.RootElement, context, new EcdsaEnvelopeSigner(key)));
    }
}
