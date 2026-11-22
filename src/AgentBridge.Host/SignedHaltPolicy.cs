using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host.Core.Updates;
using AgentBridge.Roster;

namespace AgentBridge.Host;

// The signed halt policy (docs/design/updates-fast-fleet-update.md §4.1), identical from every source: {"alg":"ES256","kid",
// "payload","sig"}. The signature covers Domain + kid + "\n" + the payload's exact bytes, which are parsed but never
// re-serialized, so no canonical JSON form can disagree. Keys and signatures follow the roster's P-256 rules (strict low-S DER).
public sealed class SignedHaltPolicy
{
    public const string Domain = "agent-bridge signed halt policy v1\n";
    // Pinned public keys (base64url SEC1 points): two slots so the next key ships before the current one retires.
    // Slot order is retirement order: a later slot is the successor, and once it is accepted every earlier slot is retired.
    // Empty until SRE provisions; an empty slot trusts nothing, so the legacy GitHub path stays in use.
    public static readonly string[] ProductionKeys = ["", ""];
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(7), Skew = TimeSpan.FromMinutes(5);
    const int Largest = 96 * 1024;
    readonly Dictionary<string, byte[]> keys;
    readonly string[] slots;

    public SignedHaltPolicy(IEnumerable<string> trusted)
    {
        var points = trusted.Where(t => t.Length > 0).Select(t => P256.Point(t, "halt policy key")).ToArray();
        // A key pinned twice (or a key-id collision) is a provisioning error: trust no key rather than throw from
        // Production's static initializer, so signed policies are refused and the legacy path stays, as with empty slots.
        slots = points.Select(KeyId).ToArray();
        if (slots.Distinct(StringComparer.Ordinal).Count() != slots.Length) { slots = []; }
        keys = slots.Length == points.Length ? points.ToDictionary(KeyId, StringComparer.Ordinal) : new(StringComparer.Ordinal);
    }

    public static SignedHaltPolicy Production { get; } = new(ProductionKeys);
    public static string KeyId(byte[] point) => Codec.Hex(SHA256.HashData(point))[..16];

    public Task<HaltPolicySnapshot> OfferAsync(ReleaseDiscovery discovery, string source, byte[] envelope, CancellationToken cancellationToken = default) =>
        discovery.OfferSignedHaltPolicyAsync(source, [.. slots], now => Verify(envelope, now), cancellationToken);

    internal VerifiedHaltPolicy Verify(byte[] envelope, DateTimeOffset now)
    {
        if (envelope.Length > Largest) { throw Refused("oversize"); }
        using var outer = Parse(envelope);
        var root = outer.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("sig", out _)) { throw Refused("unsigned"); }
        if (!Exactly(root, "alg", "kid", "payload", "sig")) { throw Refused("malformed"); }
        if (Text(root, "alg") != "ES256") { throw Refused("unsupported algorithm"); }
        var kid = Text(root, "kid");
        if (!keys.TryGetValue(kid, out var point)) { throw Refused("unknown key"); }
        if (!Codec.TryBase64Url(Text(root, "payload"), out var payload)) { throw Refused("malformed"); }
        if (!Codec.TryBase64Url(Text(root, "sig"), out var der) || P256.ReadDer(der) is not { } rs || !P256.IsLowS(rs.S)) { throw Refused("malformed signature"); }
        if (!P256.Verify(point, [.. Encoding.UTF8.GetBytes(Domain + kid + "\n"), .. payload], der)) { throw Refused("bad signature"); }
        using var inner = Parse(payload);
        var p = inner.RootElement;
        if (!Exactly(p, "v", "seq", "issued", "expires", "halts") || !p.GetProperty("v").TryGetInt32(out var v) || v != 1
            || !p.GetProperty("seq").TryGetInt64(out var seq) || seq < 1 || Stamp(p, "issued") is not { } issued
            || Stamp(p, "expires") is not { } expires || expires <= issued || p.GetProperty("halts").ValueKind != JsonValueKind.Object)
        { throw Refused("malformed"); }
        // The halts object's own signed bytes, in ops/halts.json's schema, become the stored body.
        var body = p.GetProperty("halts").GetRawText();
        var halted = ReleaseEligibility.ReadHalted(Encoding.UTF8.GetBytes(body)) ?? throw Refused("malformed");
        if (issued > now + Skew) { throw Refused("not yet valid"); }
        if (expires - issued > MaxLifetime) { throw Refused("lifetime too long"); }
        if (expires <= now) { throw Refused("expired"); }
        return new(0, body, halted, seq, expires, kid);
    }

    static JsonDocument Parse(byte[] bytes)
    {
        try { return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 }); }
        catch (JsonException) { throw Refused("malformed"); }
    }
    static bool Exactly(JsonElement e, params string[] names) => e.ValueKind == JsonValueKind.Object
        && e.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal));
    static string Text(JsonElement e, string name) => e.GetProperty(name) is { ValueKind: JsonValueKind.String } s ? s.GetString()! : "";
    static DateTimeOffset? Stamp(JsonElement e, string name) => e.GetProperty(name) is { ValueKind: JsonValueKind.String } s
        && s.GetString()!.EndsWith('Z') && s.TryGetDateTimeOffset(out var at) && at.Offset == TimeSpan.Zero ? at : null;
    static InvalidDataException Refused(string why) => new(why);
}
