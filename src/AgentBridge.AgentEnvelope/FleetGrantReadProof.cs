using System.Text.Json;
using AgentBridge.Roster;

namespace AgentBridge.AgentEnvelope;

// Closed set. The byte is the sealed value; never add a member without a new Domain version.
public enum FleetGrantReadProblem : byte { None = 0, RotationRequired = 1, Unavailable = 2, Other = 3 }

// A grant.read reply must say, inside the seal, why it has (or lacks) grants. A consumer may act on
// RotationRequired only from a value this class verified: a missing, unknown, mistyped or unsealed
// problem is refused, never defaulted to None and never inferred from text, errors or an unsigned flag.
public static class FleetGrantReadProof
{
    public const string Domain = "agent-bridge fleet read problem v1";
    static readonly string[] Names = ["none", "rotationRequired", "unavailable", "other"];

    public static JsonElement Reply(FleetGrantReadProblem problem)
    {
        if (!Enum.IsDefined(problem)) { throw new ArgumentOutOfRangeException(nameof(problem)); }
        return JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["problem"] = Names[(int)problem] });
    }

    public static FleetGrantReadResult Seal(JsonElement reply, FleetGrantProofContext context, IEnvelopeSigner signer)
    {
        if (reply.ValueKind == JsonValueKind.Undefined) { throw new InvalidDataException("read-problem"); }
        var snapshot = reply.Clone();
        return new(snapshot, FleetGrantProof.Seal(Bytes(snapshot, context), context, signer));
    }

    public static FleetGrantReadProblem Verify(FleetGrantReadResult result, ReadOnlySpan<byte> trustedHostSign,
        FleetGrantProofContext expected)
    {
        if (result is null) { throw new InvalidDataException("read-problem"); }
        if (result.Reply.ValueKind == JsonValueKind.Undefined) { throw new InvalidDataException("read-problem"); }
        var expectedBody = Bytes(result.Reply.Clone(), expected);
        var body = FleetGrantProof.Verify(result.Proof, trustedHostSign, expected);
        if (!body.AsSpan().SequenceEqual(expectedBody)) { throw new InvalidDataException("read-problem"); }
        return (FleetGrantReadProblem)expectedBody[^1];
    }

    public static byte[] Bytes(JsonElement reply, FleetGrantProofContext context)
    {
        if (reply.ValueKind != JsonValueKind.Object || !FleetGrantProof.Valid(context) || context.Action != "grant.read")
        { throw new InvalidDataException("read-problem"); }
        var fields = reply.EnumerateObject().ToArray();
        var code = fields.Length == 1 && fields[0].Name == "problem" && fields[0].Value.ValueKind == JsonValueKind.String
            ? Array.IndexOf(Names, fields[0].Value.GetString()) : -1;
        if (code < 0) { throw new InvalidDataException("read-problem"); }
        return Framing.F(Framing.Label(Domain), [(byte)code]);
    }
}
