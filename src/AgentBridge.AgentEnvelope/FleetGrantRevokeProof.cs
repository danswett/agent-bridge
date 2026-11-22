using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Roster;

namespace AgentBridge.AgentEnvelope;

public enum FleetGrantRevokeProblem : byte { None, Unavailable, NotFound, Generation, AuditIncomplete, WriteFailed, PersonRequired, Policy, Other }
public enum FleetGrantRevokeState : byte { Revoked, Refused, RevocationPending }
public sealed record FleetGrantRevokeContext(FleetGrantProofContext Context, FleetGrantConfirmation Confirmation);
public sealed record FleetGrantRevokeRequestResult(FleetGrantConfirmation Request, FleetGrantSignedPayload Proof);
public sealed record FleetGrantRevokeReceiptResult(JsonElement Receipt, FleetGrantSignedPayload Proof);
public sealed record FleetGrantRevokeOutcome(Guid Id, long Generation, FleetGrantRevokeProblem Problem,
    FleetGrantRevokeState State, JsonElement Receipt);

public static class FleetGrantRevokeProof
{
    public const string RequestDomain = "agent-bridge fleet revoke request v1";
    public const string ReceiptDomain = "agent-bridge fleet revoke receipt v1";
    static readonly string[] Problems = ["none", "unavailable", "notFound", "generation", "auditIncomplete",
        "writeFailed", "personRequired", "policy", "other"];
    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
        NumberHandling = JsonNumberHandling.Strict, MaxDepth = 12
    };
    sealed record Refusal(Guid Id, long Generation, string Receiver, string Account, string Profile,
        string Action, string Digest, string Problem, string FinalState);
    sealed record Success(Guid Id, long Generation, string Receiver, string Account, string Profile,
        string Action, string Digest, string Problem, string FinalState, bool Tombstone,
        JsonElement Counters, Guid[] InFlightRequests);
    sealed record Payload(Refusal Value, FleetGrantRevokeProblem Problem, FleetGrantRevokeState State,
        JsonElement Snapshot, byte[][] Tail);

    public static FleetGrantRevokeRequestResult SealRequest(FleetGrantConfirmation request, FleetGrantRevokeContext expected,
        IEnvelopeSigner signer) => new(request, FleetGrantProof.Seal(RequestBytes(request, expected), expected.Context, signer));
    public static FleetGrantConfirmation VerifyRequest(FleetGrantRevokeRequestResult result, ReadOnlySpan<byte> trustedHostSign,
        FleetGrantRevokeContext expected)
    {
        if (result is null) { throw Invalid(); }
        Match(result.Proof, RequestBytes(result.Request, expected), trustedHostSign, expected.Context);
        return result.Request;
    }
    public static FleetGrantRevokeRequestResult ParseRequest(ReadOnlySpan<byte> raw)
    {
        var root = Root(raw); var result = Read<FleetGrantRevokeRequestResult>(root);
        Confirmation(result.Request); GuidText(root.GetProperty("request").GetProperty("challenge"), result.Request.Challenge);
        GuidText(root.GetProperty("request").GetProperty("id"), result.Request.Id); Proof(result.Proof);
        return result;
    }
    public static FleetGrantRevokeReceiptResult SealReceipt(JsonElement receipt, FleetGrantRevokeContext expected,
        IEnvelopeSigner signer)
    {
        var payload = Snapshot(receipt);
        return new(payload.Snapshot, FleetGrantProof.Seal(ReceiptBytes(payload, expected), expected.Context, signer));
    }
    public static FleetGrantRevokeOutcome VerifyReceipt(FleetGrantRevokeReceiptResult result, ReadOnlySpan<byte> trustedHostSign,
        FleetGrantRevokeContext expected)
    {
        if (result is null) { throw Invalid(); }
        var payload = Snapshot(result.Receipt);
        Match(result.Proof, ReceiptBytes(payload, expected), trustedHostSign, expected.Context);
        return new(payload.Value.Id, payload.Value.Generation, payload.Problem, payload.State, payload.Snapshot);
    }
    public static FleetGrantRevokeReceiptResult ParseReceipt(ReadOnlySpan<byte> raw)
    {
        var result = Read<FleetGrantRevokeReceiptResult>(Root(raw)); Proof(result.Proof);
        return result with { Receipt = Snapshot(result.Receipt).Snapshot };
    }
    static byte[] RequestBytes(FleetGrantConfirmation request, FleetGrantRevokeContext expected)
    {
        Context(expected); Confirmation(request);
        if (request != expected.Confirmation) { throw Invalid(); }
        return Framing.F(Framing.Label(RequestDomain), Framing.Utf8(request.Challenge.ToString("D")),
            Framing.Utf8(request.Id.ToString("D")), Number(request.Generation), Framing.Utf8(request.Digest));
    }
    static byte[] ReceiptBytes(Payload payload, FleetGrantRevokeContext expected)
    {
        Context(expected); var v = payload.Value; var c = expected.Context; var r = expected.Confirmation;
        if (v.Id != r.Id || v.Generation != r.Generation || v.Digest != r.Digest
            || v.Account != c.Account || v.Profile != c.Profile || v.Receiver != c.Receiver || v.Action != c.Action)
        { throw Invalid(); }
        var body = Framing.F(Framing.Label(ReceiptDomain), Framing.Utf8(r.Challenge.ToString("D")),
            Framing.Utf8(v.Id.ToString("D")), Number(v.Generation), Framing.Utf8(v.Receiver),
            Framing.Utf8(v.Account), Framing.Utf8(v.Profile), Framing.Utf8(v.Action), Framing.Utf8(v.Digest),
            [(byte)payload.Problem], [(byte)payload.State], Framing.F(payload.Tail));
        if (body.Length > 30 * 1024) { throw Invalid(); }
        return body;
    }
    static Payload Snapshot(JsonElement receipt)
    {
        if (receipt.ValueKind != JsonValueKind.Object || !receipt.TryGetProperty("problem", out var problem)
            || problem.ValueKind != JsonValueKind.String) { throw Invalid(); }
        var code = Array.IndexOf(Problems, problem.GetString()); if (code < 0) { throw Invalid(); }
        Refusal value; JsonElement snapshot; byte[][] tail = [];
        if (code == 0)
        {
            var s = Read<Success>(receipt);
            if (s.FinalState != "revoked" || !s.Tombstone) { throw Invalid(); }
            var counters = Read<FleetGrantReadDataProof.Counters>(s.Counters);
            if (counters.Hour < 0 || counters.Day < 0 || counters.Pending < 0 || counters.Live < 0
                || counters.ObservedAt == default) { throw Invalid(); }
            var last = ""; var ids = new List<byte[]>();
            foreach (var id in s.InFlightRequests)
            {
                var text = id.ToString("D"); GuidText(receipt.GetProperty("inFlightRequests")[ids.Count], id);
                if (string.CompareOrdinal(last, text) >= 0) { throw Invalid(); }
                last = text; ids.Add(Framing.Utf8(text));
            }
            value = new(s.Id, s.Generation, s.Receiver, s.Account, s.Profile, s.Action, s.Digest, s.Problem, s.FinalState);
            snapshot = JsonSerializer.SerializeToElement(s with { Counters = JsonSerializer.SerializeToElement(counters, Wire) }, Wire);
            tail = [[1], JsonSerializer.SerializeToUtf8Bytes(counters, Wire), Framing.F([.. ids])];
        }
        else { value = Read<Refusal>(receipt); snapshot = JsonSerializer.SerializeToElement(value, Wire); }
        var state = value.FinalState switch { "revoked" when code == 0 => FleetGrantRevokeState.Revoked,
            "refused" when code > 0 => FleetGrantRevokeState.Refused,
            "unconfirmed" when code > 0 => FleetGrantRevokeState.RevocationPending, _ => throw Invalid() };
        GuidText(receipt.GetProperty("id"), value.Id);
        if (value.Generation <= 0 || !Digest(value.Digest) || value.Action != "grant.revoke") { throw Invalid(); }
        return new(value, (FleetGrantRevokeProblem)code, state, snapshot, tail);
    }
    static void Context(FleetGrantRevokeContext expected)
    {
        if (expected is null || !FleetGrantProof.Valid(expected.Context) || expected.Context.Action != "grant.revoke")
        { throw Invalid(); }
        Confirmation(expected.Confirmation);
        if (expected.Confirmation.Challenge == expected.Context.Request) { throw Invalid(); }
    }
    static void Confirmation(FleetGrantConfirmation value)
    { if (value is null || value.Challenge == Guid.Empty || value.Id == Guid.Empty || value.Generation <= 0 || !Digest(value.Digest)) { throw Invalid(); } }
    static bool Digest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    static void GuidText(JsonElement value, Guid id)
    { if (id == Guid.Empty || value.ValueKind != JsonValueKind.String || value.GetString() != id.ToString("D")) { throw Invalid(); } }
    static void Proof(FleetGrantSignedPayload proof)
    { if (proof is not { Body.Length: > 0, Signature.Length: > 0 }) { throw Invalid(); } }
    static void Match(FleetGrantSignedPayload proof, byte[] bytes, ReadOnlySpan<byte> key, FleetGrantProofContext context)
    { if (!FleetGrantProof.Verify(proof, key, context).AsSpan().SequenceEqual(bytes)) { throw Invalid(); } }
    static JsonElement Root(ReadOnlySpan<byte> raw)
    {
        if (raw.Length > 128 * 1024) { throw Invalid(); }
        try { using var doc = JsonDocument.Parse(raw.ToArray(), new() { MaxDepth = 12 }); return doc.RootElement.Clone(); }
        catch (JsonException e) { throw new InvalidDataException("revoke", e); }
    }
    static T Read<T>(JsonElement value) where T : class
    {
        try { if (!Strict(value)) { throw Invalid(); } return value.Deserialize<T>(Wire) ?? throw Invalid(); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { throw new InvalidDataException("revoke", e); }
    }
    static bool Strict(JsonElement value, string name = "")
    {
        if (value.ValueKind == JsonValueKind.Null) { return name is "lastCaller" or "lastOutcome"; }
        if (value.ValueKind == JsonValueKind.Undefined) { return false; }
        if (value.ValueKind == JsonValueKind.Array) { return value.EnumerateArray().All(v => Strict(v)); }
        if (value.ValueKind != JsonValueKind.Object) { return true; }
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(p => names.Add(p.Name) && Strict(p.Value, p.Name));
    }
    static byte[] Number(long value)
    { var bytes = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); return bytes; }
    static InvalidDataException Invalid() => new("revoke");
}
