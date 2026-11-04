using System.Text;
using System.Text.Json;

namespace AgentBridge.Roster;

// One WebAuthn assertion of a presence proof (§3.2, `uv`), its three parts decoded.
public sealed record Assertion(byte[] AuthenticatorData, byte[] ClientDataJson, byte[] Signature);

// An entry as it travels (§3.2): the body's exact bytes with detached signatures and presence
// assertions. Parsed strictly, sized over its fixed content, and re-emitted canonically by
// whoever forwards it, so the same entry fits the same frame on every path.
public sealed class Entry
{
    // Fixed content: the body bytes, the decoded signatures, and each assertion's decoded parts.
    public const int MaxRemoveBytes = 46 * 1024;
    public const int MaxBytes = 8 * 1024;

    public byte[] BodyBytes { get; }
    public EntryBody Body { get; }
    public byte[] Sig { get; }
    public byte[]? Cosig { get; }
    public Assertion? UvBy { get; }
    public Assertion? UvCosigner { get; }
    public byte[] Hash { get; }
    public int Size { get; }

    Entry(byte[] bodyBytes, EntryBody body, byte[] sig, byte[]? cosig, Assertion? uvBy, Assertion? uvCosigner, int size)
    {
        BodyBytes = bodyBytes;
        Body = body;
        Sig = sig;
        Cosig = cosig;
        UvBy = uvBy;
        UvCosigner = uvCosigner;
        Hash = Ids.Entry(bodyBytes);
        Size = size;
    }

    static readonly string[] WrapperFields = ["body", "sig", "cosig", "uv"];
    static readonly string[] UvFields = ["by", "cosigner"];
    static readonly string[] AssertionFields = ["authenticatorData", "clientDataJSON", "signature"];

    // The wrapper's UTF-8 bytes as received, whitespace and all. Throws RosterFormatException
    // with the first defect, in the order docs/protocol/devices.md gives.
    public static Entry Parse(byte[] wrapper)
    {
        using var doc = StrictJson.Parse(wrapper, "entry");
        var root = StrictJson.Object(doc.RootElement, "entry");
        StrictJson.Members(root, WrapperFields, ["body", "sig"], "entry");
        var body = Codec.FromBase64Url(StrictJson.String(root.GetProperty("body"), "entry.body"), "entry.body");
        var sig = Signature(root.GetProperty("sig"), "entry.sig");
        var cosig = root.TryGetProperty("cosig", out var c) ? Signature(c, "entry.cosig") : null;
        Assertion? uvBy = null, uvCosigner = null;
        if (root.TryGetProperty("uv", out var uv))
        {
            var u = StrictJson.Object(uv, "entry.uv");
            StrictJson.Members(u, UvFields, [], "entry.uv");
            if (!u.EnumerateObject().Any()) { throw new RosterFormatException(Refusal.Value, "entry.uv is empty"); }
            if (u.TryGetProperty("by", out var b)) { uvBy = ParseAssertion(b, "entry.uv.by"); }
            if (u.TryGetProperty("cosigner", out var cs)) { uvCosigner = ParseAssertion(cs, "entry.uv.cosigner"); }
        }
        var size = body.Length + sig.Length + (cosig?.Length ?? 0) + Sized(uvBy) + Sized(uvCosigner);
        if (size > MaxRemoveBytes) { throw new RosterFormatException(Refusal.Size, "entry"); }

        var parsed = EntryBody.Parse(body);
        if (parsed.Op != EntryOp.Remove && size > MaxBytes) { throw new RosterFormatException(Refusal.Size, "entry"); }
        if ((cosig is null) != (parsed.Cosigner is null)) { throw new RosterFormatException(Refusal.Cosigner, "entry.cosig"); }
        if (uvCosigner is not null && parsed.Cosigner is null) { throw new RosterFormatException(Refusal.Cosigner, "entry.uv.cosigner"); }
        return new Entry(body, parsed, sig, cosig, uvBy, uvCosigner, size);
    }

    static int Sized(Assertion? a) => a is null ? 0 : a.AuthenticatorData.Length + a.ClientDataJson.Length + a.Signature.Length;

    static byte[] Signature(JsonElement e, string what)
    {
        var der = Codec.FromBase64Url(StrictJson.String(e, what), what);
        P256.CheckRosterSignature(der, what);
        return der;
    }

    static Assertion ParseAssertion(JsonElement e, string what)
    {
        var a = StrictJson.Object(e, what);
        StrictJson.Members(a, AssertionFields, AssertionFields, what);
        var authenticatorData = Codec.FromBase64Url(StrictJson.String(a.GetProperty("authenticatorData"), $"{what}.authenticatorData"), $"{what}.authenticatorData");
        var clientData = Codec.FromBase64Url(StrictJson.String(a.GetProperty("clientDataJSON"), $"{what}.clientDataJSON"), $"{what}.clientDataJSON");
        var signature = Codec.FromBase64Url(StrictJson.String(a.GetProperty("signature"), $"{what}.signature"), $"{what}.signature");
        P256.CheckAssertionSignature(signature, $"{what}.signature");
        return new Assertion(authenticatorData, clientData, signature);
    }

    // The wrapper as every sender and forwarder emits it: members in the table's order, no
    // insignificant whitespace.
    public string Canonical() => Canonical(BodyBytes, Sig, Cosig, UvBy, UvCosigner);

    public static string Canonical(byte[] body, byte[] sig, byte[]? cosig = null, Assertion? uvBy = null, Assertion? uvCosigner = null)
    {
        var b = new StringBuilder();
        b.Append("{\"body\":\"").Append(Codec.Base64Url(body)).Append("\",\"sig\":\"").Append(Codec.Base64Url(sig)).Append('"');
        if (cosig is not null) { b.Append(",\"cosig\":\"").Append(Codec.Base64Url(cosig)).Append('"'); }
        if (uvBy is not null || uvCosigner is not null)
        {
            b.Append(",\"uv\":{");
            if (uvBy is not null) { b.Append("\"by\":"); Append(b, uvBy); }
            if (uvCosigner is not null)
            {
                if (uvBy is not null) { b.Append(','); }
                b.Append("\"cosigner\":");
                Append(b, uvCosigner);
            }
            b.Append('}');
        }
        return b.Append('}').ToString();
    }

    static void Append(StringBuilder b, Assertion a) =>
        b.Append("{\"authenticatorData\":\"").Append(Codec.Base64Url(a.AuthenticatorData))
            .Append("\",\"clientDataJSON\":\"").Append(Codec.Base64Url(a.ClientDataJson))
            .Append("\",\"signature\":\"").Append(Codec.Base64Url(a.Signature)).Append("\"}");

    // What `by` signs: F("agent-bridge roster entry v1", bodyBytes); and what the cosigner signs.
    public static byte[] SignedBy(byte[] body) => Framing.F(Framing.Label(Framing.EntryLabel), body);

    public static byte[] SignedByCosigner(byte[] body) => Framing.F(Framing.Label(Framing.CosignLabel), body);
}
