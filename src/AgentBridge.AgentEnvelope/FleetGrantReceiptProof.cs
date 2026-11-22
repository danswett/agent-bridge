using System.Buffers.Binary;
using System.Text.Json;
using AgentBridge.Roster;

namespace AgentBridge.AgentEnvelope;

public static class FleetGrantReceiptProof
{
    public const string Domain = "agent-bridge fleet issue receipt v1";

    public static FleetGrantIssueReceiptResult Seal(JsonElement receipt,
        FleetGrantProofContext context, IEnvelopeSigner signer)
    {
        var snapshot = receipt.Clone();
        return new(snapshot, FleetGrantProof.Seal(Bytes(snapshot, context), context, signer));
    }

    public static JsonElement Verify(FleetGrantIssueReceiptResult result,
        ReadOnlySpan<byte> trustedHostSign, FleetGrantProofContext expected)
    {
        if (result is null) { throw new InvalidDataException("issue-receipt"); }
        var receipt = result.Receipt.Clone();
        var expectedBody = Bytes(receipt, expected);
        var body = FleetGrantProof.Verify(result.Proof, trustedHostSign, expected);
        if (!body.AsSpan().SequenceEqual(expectedBody)) { throw new InvalidDataException("issue-receipt"); }
        return receipt;
    }

    public static byte[] Bytes(JsonElement receipt, FleetGrantProofContext context)
    {
        if (receipt.ValueKind != JsonValueKind.Object || !FleetGrantProof.Valid(context)
            || context.Action != "grant.confirmIssue")
        { throw new InvalidDataException("issue-receipt"); }
        var fields = receipt.EnumerateObject().ToArray();
        if (fields.Length != 5 || fields.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 5
            || fields.Any(p => p.Name is not ("id" or "generation" or "receiver" or "digest" or "rotated")))
        { throw new InvalidDataException("issue-receipt"); }
        var id = receipt.GetProperty("id");
        var generation = receipt.GetProperty("generation");
        var receiver = receipt.GetProperty("receiver");
        var digest = receipt.GetProperty("digest");
        var rotated = receipt.GetProperty("rotated");
        if (id.ValueKind != JsonValueKind.String || !id.TryGetGuid(out var parsedId) || parsedId == Guid.Empty
            || id.GetString() != parsedId.ToString("D")
            || generation.ValueKind != JsonValueKind.Number || !generation.TryGetInt64(out var value) || value <= 0
            || receiver.ValueKind != JsonValueKind.String || receiver.GetString() != context.Receiver
            || digest.ValueKind != JsonValueKind.String || digest.GetString() is not { Length: 64 } hash
            || !hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            || rotated.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        { throw new InvalidDataException("issue-receipt"); }
        var number = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(number, value);
        return Framing.F(Framing.Label(Domain), Framing.Utf8(parsedId.ToString("D")), number,
            Framing.Utf8(context.Receiver), Convert.FromHexString(hash),
            [rotated.ValueKind == JsonValueKind.True ? (byte)1 : (byte)0]);
    }
}
