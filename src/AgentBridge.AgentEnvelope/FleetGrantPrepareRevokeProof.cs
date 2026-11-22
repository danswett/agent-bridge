using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Roster;

namespace AgentBridge.AgentEnvelope;

public sealed record FleetGrantPrepareRevokeRequest(Guid Id, long Generation);
public sealed record FleetGrantPrepareRevokeResult(FleetGrantPrepared Prepared, FleetGrantSignedPayload Proof);

public static class FleetGrantPrepareRevokeProof
{
    public const string Domain = "agent-bridge fleet prepare revoke v1";
    public const string DigestDomain = "agent-bridge fleet revoke digest v1";
    static readonly UTF8Encoding DocumentUtf8 = new(false, true);
    sealed record Preview(Guid Id, long Generation, string Receiver, string Account, string Profile,
        string Action, string Document, string Digest);

    public static FleetGrantPrepareRevokeResult Seal(FleetGrantPrepared prepared, FleetGrantProofContext context,
        FleetGrantPrepareRevokeRequest request, IEnvelopeSigner signer, TimeProvider clock)
    {
        var snapshot = Snapshot(prepared);
        return new(snapshot, FleetGrantProof.Seal(Bytes(snapshot, context, request, clock), context, signer));
    }
    public static FleetGrantPrepared Verify(FleetGrantPrepareRevokeResult result, ReadOnlySpan<byte> trustedHostSign,
        FleetGrantProofContext expected, FleetGrantPrepareRevokeRequest request, TimeProvider clock)
    {
        if (result is null) { throw Invalid(); }
        var snapshot = Snapshot(result.Prepared); var bytes = Bytes(snapshot, expected, request, clock);
        if (!FleetGrantProof.Verify(result.Proof, trustedHostSign, expected).AsSpan().SequenceEqual(bytes)) { throw Invalid(); }
        return snapshot;
    }
    public static FleetGrantPrepareRevokeResult Parse(ReadOnlySpan<byte> raw)
    {
        var root = FleetGrantRevokeProof.Root(raw); var result = FleetGrantRevokeProof.Read<FleetGrantPrepareRevokeResult>(root);
        if (result.Proof is not { Body.Length: > 0, Signature.Length: > 0 }) { throw Invalid(); }
        var snapshot = Snapshot(result.Prepared);
        var json = root.GetProperty("prepared");
        FleetGrantRevokeProof.GuidText(json.GetProperty("challenge").GetProperty("request"), snapshot.Challenge.Request);
        FleetGrantRevokeProof.GuidText(json.GetProperty("challenge").GetProperty("id"), snapshot.Challenge.Id);
        FleetGrantRevokeProof.GuidText(json.GetProperty("preview").GetProperty("id"), snapshot.Challenge.Id);
        return result with { Prepared = snapshot };
    }
    public static string Digest(FleetGrantChallenge challenge, string document) =>
        Convert.ToHexStringLower(SHA256.HashData(DigestBytes(challenge, document)));
    public static byte[] DigestBytes(FleetGrantChallenge c, string document)
    {
        if (c is null || !FleetGrantProof.Valid(new(c.Account, c.Profile, c.Receiver, c.Action, c.Request))
            || c.Action != "grant.confirmRevoke" || c.Id == Guid.Empty || c.Generation <= 0
            || c.ExpiresAt == default || document is not { Length: > 0 and <= 30 * 1024 }) { throw Invalid(); }
        byte[] text;
        try { text = DocumentUtf8.GetBytes(document); }
        catch (EncoderFallbackException e) { throw new InvalidDataException("prepare-revoke", e); }
        var bytes = Framing.F(Framing.Label(DigestDomain), Framing.Utf8(c.Account), Framing.Utf8(c.Profile),
            Framing.Utf8(c.Receiver), Framing.Utf8(c.Action), Framing.Utf8(c.Request.ToString("D")),
            Framing.Utf8(c.Id.ToString("D")), FleetGrantRevokeProof.Number(c.Generation),
            Framing.Utf8(c.ExpiresAt.ToString("O", CultureInfo.InvariantCulture)), text);
        if (bytes.Length > 30 * 1024) { throw Invalid(); }
        return bytes;
    }
    public static FleetGrantRevokeContext ConfirmContext(FleetGrantPrepared verifiedPrepared,
        FleetGrantProofContext expected, TimeProvider clock)
    {
        var snapshot = Snapshot(verifiedPrepared); var c = snapshot.Challenge;
        Validate(snapshot, expected, new(c.Id, c.Generation), clock, "grant.confirmRevoke");
        return new(expected, new(c.Request, c.Id, c.Generation, c.Digest));
    }
    static FleetGrantPrepared Snapshot(FleetGrantPrepared prepared)
    {
        if (prepared is null || prepared.Challenge is null) { throw Invalid(); }
        var v = FleetGrantRevokeProof.Read<Preview>(prepared.Preview); var c = prepared.Challenge;
        if (v.Id != c.Id || v.Generation != c.Generation || v.Receiver != c.Receiver || v.Account != c.Account
            || v.Profile != c.Profile || v.Action != c.Action || v.Digest != c.Digest || c.Digest != Digest(c, v.Document))
        { throw Invalid(); }
        return prepared with { Preview = JsonSerializer.SerializeToElement(v, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
    }
    static void Validate(FleetGrantPrepared prepared, FleetGrantProofContext expected,
        FleetGrantPrepareRevokeRequest request, TimeProvider clock, string action)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var c = prepared.Challenge;
        if (!FleetGrantProof.Valid(expected) || expected.Action != action || request is null
            || request.Id == Guid.Empty || request.Generation <= 0 || c.Id != request.Id || c.Generation != request.Generation
            || c.Request == expected.Request || c.Account != expected.Account || c.Profile != expected.Profile
            || c.Receiver != expected.Receiver || c.ExpiresAt <= clock.GetUtcNow()) { throw Invalid(); }
    }
    static byte[] Bytes(FleetGrantPrepared prepared, FleetGrantProofContext expected,
        FleetGrantPrepareRevokeRequest request, TimeProvider clock)
    {
        Validate(prepared, expected, request, clock, "grant.prepareRevoke");
        var v = FleetGrantRevokeProof.Read<Preview>(prepared.Preview);
        var body = Framing.F(Framing.Label(Domain), DigestBytes(prepared.Challenge, v.Document),
            Convert.FromHexString(prepared.Challenge.Digest));
        if (body.Length > 30 * 1024) { throw Invalid(); }
        return body;
    }
    static InvalidDataException Invalid() => new("prepare-revoke");
}
