using System.Globalization;
using System.Text.Json;

namespace AgentBridge.Roster;

// The two identities of docs/design/role-ids.md §1, distinct types so neither can stand in for the
// other: each a random UUIDv4 in canonical lowercase hyphenated text, never derived from a name.
public readonly record struct RoleId(string Value) { public static RoleId Parse(string text, string what) => new(RoleClaim.Uuid4(text, what)); }
public readonly record struct SessionId(string Value) { public static SessionId Parse(string text, string what) => new(RoleClaim.Uuid4(text, what)); }

// A published role holder claim (role-ids.md §2), its wire format only ("Slice 1 wire format").
// Additive and unused: nothing signs, verifies, publishes or reads one yet (slices 6 and 9).
public sealed record RoleClaim(string Account, string Generation, RoleId RoleId, int HolderEpoch, SessionId SessionId,
    string HostAdd, string HostKeyId, string BindingProof, string AssignmentDigest, string State,
    string IssuedAt, string ExpiresAt, string AuthorityKeyId, string Signature)
{
    public const string Purpose = "agent-bridge role claim v1";
    public const string BindingPurpose = "agent-bridge role binding v1";
    public static readonly string[] States = ["active", "quiescing", "revoked", "noHolder"];
    // role-ids.md §2's order, which the bytes keep; the signed body is all but the signature.
    static readonly string[] Order = ["version", "account", "generation", "roleId", "holderEpoch", "sessionId", "hostAdd",
        "hostKeyId", "bindingProof", "assignmentDigest", "state", "issuedAt", "expiresAt", "authorityKeyId", "signature"];

    // Refuses anything but the one canonical encoding, and a claim for another role than the one asked for.
    public static RoleClaim Parse(byte[] utf8, RoleId expected)
    {
        using var doc = StrictJson.Parse(utf8, "claim");
        var c = StrictJson.Object(doc.RootElement, "claim");
        StrictJson.Members(c, Order, Order, "claim");
        if (c.GetProperty("version").GetRawText() != "1") { throw new RosterFormatException(Refusal.Version, "claim.version"); }
        string S(string name) => StrictJson.String(c.GetProperty(name), "claim." + name);
        string Id(string name) => Codec.IsId(S(name)) ? S(name) : throw new RosterFormatException(Refusal.Id, "claim." + name);
        string Bytes32(string name) { Codec.FromBase64Url(S(name), 32, "claim." + name); return S(name); }
        string Der(string name) { P256.CheckRosterSignature(Codec.FromBase64Url(S(name), "claim." + name), "claim." + name); return S(name); }
        string At(string name) => Times.IsUtc(S(name)) ? S(name) : throw new RosterFormatException(Refusal.Time, "claim." + name);
        var claim = new RoleClaim(EntryBody.ParseAccount(S("account")), Id("generation"), RoleId.Parse(S("roleId"), "claim.roleId"),
            StrictJson.Integer(c.GetProperty("holderEpoch"), "claim.holderEpoch") ?? throw new RosterFormatException(Refusal.Value, "claim.holderEpoch"),
            SessionId.Parse(S("sessionId"), "claim.sessionId"), Bytes32("hostAdd"), Id("hostKeyId"), Der("bindingProof"),
            Bytes32("assignmentDigest"), States.Contains(S("state")) ? S("state") : throw new RosterFormatException(Refusal.Value, "claim.state"),
            At("issuedAt"), At("expiresAt"), Id("authorityKeyId"), Der("signature"));
        if (!claim.Bytes().AsSpan().SequenceEqual(utf8)) { throw new RosterFormatException(Refusal.NonCanonical, "claim"); }
        if (claim.RoleId.Value == claim.SessionId.Value) { throw new RosterFormatException(Refusal.Role, "claim.sessionId is its roleId"); }
        if (claim.RoleId != expected) { throw new RosterFormatException(Refusal.Role, "claim.roleId is not the role asked for"); }
        return claim;
    }

    public byte[] Bytes() => Encode(withSignature: true);
    public byte[] BodyBytes() => Encode(withSignature: false);

    // What the role authority's signature covers: F(purpose, body). Any other purpose is refused.
    public byte[] SigningInput(string purpose) => purpose == Purpose
        ? Framing.F(Framing.Label(Purpose), BodyBytes()) : throw new RosterFormatException(Refusal.Purpose, "not " + Purpose);

    // Compact JSON in Order. Only account can hold a quote or backslash (controls refuse); all else is raw UTF-8.
    byte[] Encode(bool withSignature)
    {
        EntryBody.ParseAccount(Account); // a hand-built claim must still be one a reader accepts
        static string Q(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        string[] values = ["1", Q(Account), Q(Generation), Q(RoleId.Value), HolderEpoch.ToString(CultureInfo.InvariantCulture), Q(SessionId.Value),
            Q(HostAdd), Q(HostKeyId), Q(BindingProof), Q(AssignmentDigest), Q(State), Q(IssuedAt), Q(ExpiresAt), Q(AuthorityKeyId), Q(Signature)];
        return new System.Text.UTF8Encoding(false, true).GetBytes("{" + string.Join(",", Order.Take(Order.Length - (withSignature ? 0 : 1)).Select((name, i) => $"\"{name}\":{values[i]}")) + "}");
    }

    // Version 4, RFC 4122 variant, canonical lowercase "D" text; nil, other versions and names refuse.
    internal static string Uuid4(string text, string what) =>
        Guid.TryParseExact(text, "D", out var g) && g.ToString("D") == text && text[14] == '4' && text[19] is '8' or '9' or 'a' or 'b'
            ? text : throw new RosterFormatException(Refusal.Uuid, what);
}
