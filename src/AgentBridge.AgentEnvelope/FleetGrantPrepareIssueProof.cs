using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Roster;

namespace AgentBridge.AgentEnvelope;

public sealed record FleetGrantPrepareIssueResult(FleetGrantPrepared Prepared, FleetGrantSignedPayload Proof);

public static class FleetGrantPrepareIssueProof
{
    public const string Domain = "agent-bridge fleet prepare issue v1";
    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 12
    };
    sealed record Allocation(int Hourly, int Daily, int Live);
    sealed record Preview(Guid Id, long Generation, string Receiver, string Profile, string Document,
        string Digest, Allocation Allocation, string EffectivePolicy, string[] UnavailableActions);

    public static FleetGrantPrepareIssueResult Seal(FleetGrantPrepared prepared, FleetGrantProofContext context,
        IEnvelopeSigner signer)
    {
        var snapshot = Snapshot(prepared);
        return new(snapshot, FleetGrantProof.Seal(Bytes(snapshot, context), context, signer));
    }

    public static FleetGrantPrepared Verify(FleetGrantPrepareIssueResult result, ReadOnlySpan<byte> trustedHostSign,
        FleetGrantProofContext expected)
    {
        if (result is null) { throw Invalid(); }
        var snapshot = Snapshot(result.Prepared);
        var expectedBody = Bytes(snapshot, expected);
        var body = FleetGrantProof.Verify(result.Proof, trustedHostSign, expected);
        if (!body.AsSpan().SequenceEqual(expectedBody)) { throw Invalid(); }
        return snapshot;
    }

    public static FleetGrantPrepareIssueResult Parse(JsonElement reply)
    {
        try
        {
            if (!Strict(reply)) { throw Invalid(); }
            var result = reply.Deserialize<FleetGrantPrepareIssueResult>(Wire) ?? throw Invalid();
            if (result.Proof is not { Body.Length: > 0, Signature.Length: > 0 }) { throw Invalid(); }
            return result with { Prepared = Snapshot(result.Prepared) };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        { throw new InvalidDataException("prepare-issue", e); }
    }

    static FleetGrantPrepared Snapshot(FleetGrantPrepared prepared)
    {
        try
        {
            if (prepared is null || prepared.Challenge is null || !Strict(prepared.Preview)) { throw Invalid(); }
            _ = prepared.Preview.Deserialize<Preview>(Wire) ?? throw Invalid();
            return prepared with { Preview = prepared.Preview.Clone() };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        { throw new InvalidDataException("prepare-issue", e); }
    }

    static byte[] Bytes(FleetGrantPrepared prepared, FleetGrantProofContext context)
    {
        var c = prepared.Challenge;
        var v = prepared.Preview.Deserialize<Preview>(Wire)!;
        if (!FleetGrantProof.Valid(context) || context.Action != "grant.prepareIssue"
            || c.Request == Guid.Empty || c.Request == context.Request
            || c.Id == Guid.Empty || c.Generation <= 0 || c.ExpiresAt == default
            || c.Action != "grant.confirmIssue" || c.Account != context.Account
            || c.Profile != context.Profile || c.Receiver != context.Receiver || !Digest(c.Digest)
            || v.Id != c.Id || v.Generation != c.Generation || v.Receiver != c.Receiver
            || v.Profile != c.Profile || v.Digest != c.Digest || !Text(v.Document) || !Text(v.EffectivePolicy)
            || v.Allocation is not { Hourly: > 0 and <= 1000, Daily: > 0 and <= 5000, Live: > 0 and <= 100 } a
            || a.Hourly > a.Daily || v.UnavailableActions is not { Length: <= 2 } actions
            || actions.Any(s => s is not ("launch" or "stop")) || actions.Distinct().Count() != actions.Length)
        { throw Invalid(); }
        var body = Framing.F(Framing.Label(Domain),
            Utf8(c.Request.ToString("D")), Utf8(c.Id.ToString("D")), Number(c.Generation),
            Utf8(c.Account), Utf8(c.Profile), Utf8(c.Receiver), Utf8(c.Action),
            Convert.FromHexString(c.Digest), Utf8(c.ExpiresAt.ToString("O", CultureInfo.InvariantCulture)),
            Utf8(v.Id.ToString("D")), Number(v.Generation), Utf8(v.Receiver), Utf8(v.Profile),
            Utf8(v.Document), Convert.FromHexString(v.Digest), Number(a.Hourly), Number(a.Daily), Number(a.Live),
            Utf8(v.EffectivePolicy), Framing.F(actions.Select(Utf8).ToArray()));
        if (body.Length > 30 * 1024) { throw Invalid(); }
        return body;
    }

    static bool Strict(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) { return false; }
        if (element.ValueKind == JsonValueKind.Array) { return element.EnumerateArray().All(Strict); }
        if (element.ValueKind != JsonValueKind.Object) { return true; }
        var names = new HashSet<string>(StringComparer.Ordinal);
        return element.EnumerateObject().All(p => names.Add(p.Name) && Strict(p.Value));
    }
    static bool Digest(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    static bool Text(string? value) => value is { Length: > 0 and <= 30 * 1024 };
    static byte[] Utf8(string value) => Framing.Utf8(value);
    static byte[] Number(long value)
    {
        var bytes = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); return bytes;
    }
    static InvalidDataException Invalid() => new("prepare-issue");
}
