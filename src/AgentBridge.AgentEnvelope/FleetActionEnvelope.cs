using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.Roster;

namespace AgentBridge.AgentEnvelope;

// G9 (docs/design/fleet-grants.md, "G9 wire and cutover rows"). Signed fleet action for a remote receiver.
// Never an EnvelopeBody v1 message: the signing domain below is distinct and a messaging envelope cannot verify here.
public sealed record FleetActionBody(string Account, string SourceDevice, string Subject, string Audience,
    string Action, string GrantId, long Generation, Guid Request, string Nonce, DateTimeOffset ExpiresAt,
    string PayloadSha256);
public sealed record FleetActionSigned(string Body, string Signature);

public static class FleetActionEnvelope
{
    public const string LaunchAction = "fleet.launch";
    public const string EndAction = "fleet.end";
    public const int MaxLifetimeSeconds = 120;
    const int MaxBody = 2048;
    static readonly byte[] Domain = Framing.Label("agent-bridge fleet action envelope v1");
    static readonly string[] Members = ["v", "action", "account", "sourceDevice", "subject", "audience", "grantId",
        "generation", "requestId", "nonce", "expiresAt", "payloadSha256"];

    public static string NewNonce() => Codec.Base64Url(RandomNumberGenerator.GetBytes(16));
    // The envelope `subject`: a digest of the agent principal tuple the grant names. The receiver finds the tuple among
    // the grant's subjects, so the process instance is attested by the source host and never travels or is rechecked here.
    public static string SubjectDigest(string account, string sourceHost, string session, string credential, int pid, long startedUtcTicks) =>
        Codec.Base64Url(SHA256.HashData(Framing.F(Framing.Label("agent-bridge fleet action subject v1"), Framing.Utf8(account),
            Framing.Utf8(sourceHost), Framing.Utf8(session), Framing.Utf8(credential), Framing.Utf8(pid.ToString()),
            Framing.Utf8(startedUtcTicks.ToString()))));
    public static string PayloadSha256(ReadOnlySpan<byte> canonicalRequest) => Codec.Base64Url(SHA256.HashData(canonicalRequest));

    public static FleetActionSigned Seal(FleetActionBody body, IEnvelopeSigner signer)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!Shape(body)) { throw new ArgumentException("fleet-action", nameof(body)); }
        return SealRaw(Write(body), signer);
    }

    internal static FleetActionSigned SealRaw(byte[] body, IEnvelopeSigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);
        if (body.Length is < 1 or > MaxBody) { throw new ArgumentOutOfRangeException(nameof(body)); }
        return new(Codec.Base64Url(body), Codec.Base64Url(P256.Normalize(signer.Sign(Frame(body)))));
    }

    // Verifies signature, canonical form, account, audience, action and a live short expiry. Replay is the
    // caller's FleetActionReplayCache step; it must run only after this succeeds.
    // expectedSource is the device whose key is trusted: the body's sourceDevice must be that device, so a valid signature
    // from one peer's key can never be presented as another device's action.
    public static FleetActionBody Verify(FleetActionSigned signed, ReadOnlySpan<byte> trustedSourceHostSign,
        string expectedAccount, string expectedSource, string expectedAudience, DateTimeOffset now, string expectedAction = LaunchAction)
    {
        if (signed is null || !P256.IsPoint(trustedSourceHostSign)
            || signed.Body is not { Length: > 0 and <= MaxBody * 4 / 3 + 4 }
            || signed.Signature is not { Length: > 0 and <= 96 }
            || !Codec.TryBase64Url(signed.Body, out var raw) || raw.Length is < 1 or > MaxBody
            || !Codec.TryBase64Url(signed.Signature, out var signature)) { throw Bad(); }
        var parts = P256.ReadDer(signature);
        if (parts is null || !P256.IsLowS(parts.Value.S) || !P256.Verify(trustedSourceHostSign, Frame(raw), signature))
        { throw Bad(); }
        var body = Read(raw);
        var remaining = body.ExpiresAt - now;
        if (!raw.AsSpan().SequenceEqual(Write(body)) || !Shape(body) || body.Action != expectedAction || expectedAction is not (LaunchAction or EndAction)
            || body.Account != expectedAccount || body.SourceDevice != expectedSource || body.Audience != expectedAudience
            || remaining <= TimeSpan.Zero || remaining > TimeSpan.FromSeconds(MaxLifetimeSeconds)) { throw Bad(); }
        return body;
    }

    static Exception Bad() => new InvalidDataException("fleet-action");
    static byte[] Frame(byte[] body) => Framing.F(Domain, body);

    static bool Shape(FleetActionBody b) => Text(b.Account, 256) && Hex32(b.SourceDevice) && Text(b.Subject, 128)
        && Hex32(b.Audience) && b.Action is not null && Text(b.GrantId, 128) && b.Generation is >= 0 and <= StrictJson.MaxInteger
        && b.Request != Guid.Empty && Nonce(b.Nonce) && b.PayloadSha256 is { Length: 43 }
        && Codec.TryBase64Url(b.PayloadSha256, out var h) && h.Length == 32
        && b.ExpiresAt.ToUnixTimeSeconds() is >= 0 and <= StrictJson.MaxInteger;

    static bool Nonce(string? n) => n is { Length: 22 } && Codec.TryBase64Url(n, out var r) && r.Length == 16;
    static bool Hex32(string? s) => s is { Length: 32 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    static bool Text(string? s, int max) => s is { Length: > 0 } && s.Length <= max && s.All(c => c is >= '!' and <= '~');

    static byte[] Write(FleetActionBody b)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteString("action", b.Action);
            w.WriteString("account", b.Account);
            w.WriteString("sourceDevice", b.SourceDevice);
            w.WriteString("subject", b.Subject);
            w.WriteString("audience", b.Audience);
            w.WriteString("grantId", b.GrantId);
            w.WriteNumber("generation", b.Generation);
            w.WriteString("requestId", b.Request.ToString("D"));
            w.WriteString("nonce", b.Nonce);
            w.WriteNumber("expiresAt", b.ExpiresAt.ToUnixTimeSeconds());
            w.WriteString("payloadSha256", b.PayloadSha256);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    static FleetActionBody Read(byte[] raw)
    {
        try
        {
            using var doc = StrictJson.Parse(raw, "fleet-action");
            var o = StrictJson.Object(doc.RootElement, "fleet-action");
            StrictJson.Members(o, Members, Members, "fleet-action");
            string S(string n) => StrictJson.String(o.GetProperty(n), "fleet-action");
            if (StrictJson.Integer(o.GetProperty("v"), "fleet-action") != 1 || !Guid.TryParseExact(S("requestId"), "D", out var request))
            { throw Bad(); }
            return new(S("account"), S("sourceDevice"), S("subject"), S("audience"), S("action"), S("grantId"),
                StrictJson.Integer(o.GetProperty("generation"), "fleet-action"), request, S("nonce"),
                DateTimeOffset.FromUnixTimeSeconds(StrictJson.Integer(o.GetProperty("expiresAt"), "fleet-action")),
                S("payloadSha256"));
        }
        catch (Exception e) when (e is not InvalidDataException) { throw Bad(); }
    }
}

// Receiver replay cache keyed by (sourceDevice, nonce), kept until expiry plus skew. Bounded in count (1..MaxCapacity; a full
// cache refuses new entries instead of evicting a live one) and in time (an expiry-ordered queue drops entries as they lapse; an
// expiry already past, or beyond the envelope lifetime, is refused here too). In memory, so a restart is covered by a WARM-UP:
// nothing is accepted until every envelope sealed before the start has expired (lifetime plus skew), which also closes replay
// of a pre-restart envelope without a disk store. A restart therefore pauses remote launches for about 2.5 minutes.
public sealed class FleetActionReplayCache
{
    public const int MaxCapacity = 65536;
    static readonly TimeSpan MaxSkew = TimeSpan.FromMinutes(5);
    readonly Dictionary<(string, string), DateTimeOffset> seen = [];
    readonly PriorityQueue<(string, string), DateTimeOffset> byExpiry = new();
    readonly object gate = new();
    readonly int capacity;
    readonly TimeSpan skew;
    readonly DateTimeOffset warmUntil;

    public FleetActionReplayCache(int capacity, TimeSpan skew, DateTimeOffset startedAt)
    {
        if (capacity is < 1 or > MaxCapacity) { throw new ArgumentOutOfRangeException(nameof(capacity)); }
        if (skew < TimeSpan.Zero || skew > MaxSkew) { throw new ArgumentOutOfRangeException(nameof(skew)); }
        this.capacity = capacity;
        this.skew = skew;
        warmUntil = startedAt + TimeSpan.FromSeconds(FleetActionEnvelope.MaxLifetimeSeconds) + skew;
    }

    public bool TryAdd(FleetActionBody body, DateTimeOffset now)
    {
        lock (gate)
        {
            if (now < warmUntil || body.ExpiresAt <= now || body.ExpiresAt - now > TimeSpan.FromSeconds(FleetActionEnvelope.MaxLifetimeSeconds))
            { return false; }
            while (byExpiry.TryPeek(out var key, out var until) && until <= now) { byExpiry.Dequeue(); seen.Remove(key); }
            var id = (body.SourceDevice, body.Nonce);
            if (seen.ContainsKey(id) || seen.Count >= capacity) { return false; }
            var keep = body.ExpiresAt + skew;
            seen[id] = keep;
            byExpiry.Enqueue(id, keep);
            return true;
        }
    }
}
