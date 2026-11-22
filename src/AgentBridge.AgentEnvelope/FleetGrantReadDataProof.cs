using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Roster;

namespace AgentBridge.AgentEnvelope;

public sealed record FleetGrantReadDataContext(FleetGrantProofContext Context, int Offset, long Generation, int Total);
public sealed record FleetGrantReadDataRow(JsonElement Document, JsonElement Counters, bool Stale);
public sealed record FleetGrantReadDataPage(int Offset, long RequestedGeneration, long Generation,
    DateTimeOffset ObservedAt, ImmutableArray<FleetGrantReadDataRow> Grants, int Next, int Total);
public sealed record FleetGrantReadDataResult(FleetGrantReadDataPage Page, FleetGrantSignedPayload Proof);

public static class FleetGrantReadDataProof
{
    public const string Domain = "agent-bridge fleet read data v1";
    static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
        NumberHandling = JsonNumberHandling.Strict, MaxDepth = 12
    };
    sealed record Process(int Pid, DateTimeOffset StartedAt);
    sealed record Subject(string Account, string SourceHost, string Session, string Credential, Process ProcessInstance);
    sealed record Issuer(string Account, string Device, string PersonProof, DateTimeOffset IssuedAt, string Digest);
    sealed record Audience(string Device, string Profile);
    sealed record Role(string Name, Subject Subject);
    sealed record Target(string Session, Process ProcessInstance);
    sealed record Scope(string[] Machines, string[] Workspaces, string[] Agents, string[] Models,
        string[] Profiles, string[] Efforts, string[] Contexts, string[] Permissions);
    sealed record Caps(int Hourly, int Daily, int Live);
    sealed record Document(int Version, Guid Id, string Account, Issuer Issuer, Audience Audience,
        Subject[] Subjects, string[] Actions, Scope Scope, Caps Caps, DateTimeOffset NotBefore, long Generation,
        DateTimeOffset ExpiresAt, Role[] Roles, Target[] StopTargets, bool AllowAllLaunch);
    internal sealed record Counters(int Hour, int Day, int Pending, int Live, string[] Requests,
        string? LastCaller, string? LastOutcome, DateTimeOffset ObservedAt, string[] PendingRequests, string[] LiveSessions);

    public static FleetGrantReadDataResult Seal(FleetGrantReadDataPage page, FleetGrantReadDataContext context,
        IEnvelopeSigner signer)
    {
        var snapshot = Snapshot(page);
        return new(snapshot, FleetGrantProof.Seal(Bytes(snapshot, context), context.Context, signer));
    }
    public static FleetGrantReadDataPage Verify(FleetGrantReadDataResult result, ReadOnlySpan<byte> trustedHostSign,
        FleetGrantReadDataContext expected)
    {
        if (result is null) { throw Invalid(); }
        var snapshot = Snapshot(result.Page); var expectedBody = Bytes(snapshot, expected);
        var body = FleetGrantProof.Verify(result.Proof, trustedHostSign, expected.Context);
        if (!body.AsSpan().SequenceEqual(expectedBody)) { throw Invalid(); }
        return snapshot;
    }
    public static FleetGrantReadDataResult Parse(JsonElement reply)
    {
        try
        {
            if (!Strict(reply)) { throw Invalid(); }
            var result = reply.Deserialize<FleetGrantReadDataResult>(Wire) ?? throw Invalid();
            if (result.Proof is not { Body.Length: > 0, Signature.Length: > 0 }) { throw Invalid(); }
            return result with { Page = Snapshot(result.Page) };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        { throw new InvalidDataException("read-data", e); }
    }
    static FleetGrantReadDataPage Snapshot(FleetGrantReadDataPage page)
    {
        try
        {
            if (page is null || page.Grants.IsDefault || page.Grants.Length > 16) { throw Invalid(); }
            return page with { Grants = [.. page.Grants.Select(row =>
            {
                if (row is null || !Strict(row.Document) || !Strict(row.Counters)) { throw Invalid(); }
                var d = row.Document.Deserialize<Document>(Wire) ?? throw Invalid();
                var c = row.Counters.Deserialize<Counters>(Wire) ?? throw Invalid();
                if (d.Version != 1 || d.Id == Guid.Empty || d.Generation <= 0 || d.NotBefore == default
                    || d.ExpiresAt == default || d.Issuer.IssuedAt == default || d.Issuer.Account != d.Account
                    || d.Issuer.Digest.Length != 64 || !d.Issuer.Digest.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f')
                    || d.Caps is not { Hourly: > 0 and <= 1000, Daily: > 0 and <= 5000, Live: > 0 and <= 100 }
                    || d.Caps.Hourly > d.Caps.Daily || d.Subjects.Length is < 1 or > 64
                    || d.Actions.Length is < 1 or > 2 || d.Actions.Any(a => a is not ("launch" or "stop"))
                    || c.Hour < 0 || c.Day < 0 || c.Pending < 0 || c.Live < 0 || c.ObservedAt == default)
                { throw Invalid(); }
                return new FleetGrantReadDataRow(JsonSerializer.SerializeToElement(d, Wire),
                    JsonSerializer.SerializeToElement(c, Wire), row.Stale);
            })] };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        { throw new InvalidDataException("read-data", e); }
    }
    static byte[] Bytes(FleetGrantReadDataPage page, FleetGrantReadDataContext expected)
    {
        if (expected is null || !FleetGrantProof.Valid(expected.Context) || expected.Context.Action != "grant.read"
            || expected.Offset is < 0 or > 256 || expected.Generation < 0 || expected.Total is < -1 or > 256
            || (expected.Generation == 0 ? expected.Offset != 0 || expected.Total != -1
                : expected.Offset == 0 || expected.Total < expected.Offset)
            || page.Offset != expected.Offset || page.RequestedGeneration != expected.Generation
            || page.Generation <= 0 || page.ObservedAt == default || page.Total is < 0 or > 256
            || (expected.Generation > 0 && (page.Generation != expected.Generation || page.Total != expected.Total)))
        { throw Invalid(); }
        var end = page.Offset + page.Grants.Length;
        if (end > page.Total || (end == page.Total ? page.Next != -1 : page.Next != end || end <= page.Offset))
        { throw Invalid(); }
        var last = "";
        var rows = page.Grants.Select(row =>
        {
            var d = row.Document.Deserialize<Document>(Wire)!;
            var id = d.Id.ToString("D");
            if (string.CompareOrdinal(last, id) >= 0 || d.Account != expected.Context.Account
                || d.Audience.Device != expected.Context.Receiver || d.Audience.Profile != expected.Context.Profile)
            { throw Invalid(); }
            last = id;
            return Framing.F(JsonSerializer.SerializeToUtf8Bytes(d, Wire),
                JsonSerializer.SerializeToUtf8Bytes(row.Counters.Deserialize<Counters>(Wire)!, Wire),
                [row.Stale ? (byte)1 : (byte)0]);
        }).ToArray();
        var body = Framing.F(Framing.Label(Domain), Number(page.Offset), Number(page.RequestedGeneration),
            Number(page.Generation), Framing.Utf8(page.ObservedAt.ToString("O", CultureInfo.InvariantCulture)),
            Number(page.Total), Framing.F(rows), Number(page.Next));
        if (body.Length > 30 * 1024) { throw Invalid(); }
        return body;
    }
    static bool Strict(JsonElement element, string name = "")
    {
        if (element.ValueKind == JsonValueKind.Null) { return name is "lastCaller" or "lastOutcome"; }
        if (element.ValueKind == JsonValueKind.Undefined) { return false; }
        if (element.ValueKind == JsonValueKind.Array) { return element.EnumerateArray().All(item => Strict(item)); }
        if (element.ValueKind != JsonValueKind.Object) { return true; }
        var names = new HashSet<string>(StringComparer.Ordinal);
        return element.EnumerateObject().All(p => names.Add(p.Name) && Strict(p.Value, p.Name));
    }
    static byte[] Number(long value)
    {
        var bytes = new byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); return bytes;
    }
    static InvalidDataException Invalid() => new("read-data");
}
