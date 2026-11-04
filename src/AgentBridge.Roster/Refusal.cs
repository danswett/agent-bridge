namespace AgentBridge.Roster;

// Why an entry, or a part of one, is refused. The codes are protocol: the shared vectors
// (docs/protocol/vectors/roster.json) name them, and the PWA's port (pwa/js/roster) gives the same
// code for the same input, so the two implementations agree on the reason as well as the verdict.
// Each check runs in the order docs/protocol/devices.md gives, so an input with one defect has
// one code everywhere.
public static class Refusal
{
    // Not I-JSON (RFC 7493): invalid UTF-8, a byte-order mark, a syntax error, a lone surrogate.
    public const string Json = "json";
    public const string Duplicate = "duplicate-member";
    public const string Unknown = "unknown-member";
    public const string Missing = "missing-member";
    // A member of the wrong JSON type.
    public const string Type = "type";
    // An enumerated value the protocol does not have, or a value out of its range.
    public const string Value = "value";
    public const string Version = "version";
    public const string Seq = "seq";
    // A device id or generation that is not 32 lowercase hex characters.
    public const string Id = "id";
    // Not base64url without padding, or not its canonical form.
    public const string Base64 = "base64";
    // Base64url that decodes to another length than the field has.
    public const string Length = "length";
    // A public key that is not an uncompressed P-256 point on the curve.
    public const string Point = "point";
    // A signature that is not strict minimal DER, or whose r or s is out of range.
    public const string Der = "der";
    public const string HighS = "high-s";
    public const string Name = "name";
    public const string Account = "account";
    public const string Time = "time";
    public const string Approval = "approval";
    // prev or keep not strictly ascending by raw bytes (head order), so not distinct either.
    public const string Order = "order";
    // prev or keep holding more or fewer hashes than its op allows.
    public const string Count = "count";
    // A device whose id is not its dh key's, or whose fields do not fit its kind.
    public const string Device = "device";
    // Signer rules a body alone decides: a genesis signed by another device, a self-removal with
    // a keep list or a cosigner, a cosigner that is the signer or the target.
    public const string Signer = "signer";
    // cosig and uv's members not matching the body's cosigner.
    public const string Cosigner = "cosigner";
    // Over 46 KiB for a removal, or 8 KiB for anything else, of fixed content.
    public const string Size = "size";

    // WebAuthn assertion checks (§4.6), in the order they run.
    public const string UvAuthenticatorData = "uv-authenticator-data";
    public const string UvRpId = "uv-rp-id";
    public const string UvFlags = "uv-flags";
    public const string UvBackup = "uv-backup";
    public const string UvClientData = "uv-client-data";
    public const string UvType = "uv-type";
    public const string UvChallenge = "uv-challenge";
    public const string UvOrigin = "uv-origin";
    public const string UvSignature = "uv-signature";
}

// A refusal with its code, and what it was about for a log (never a secret, never a whole entry).
public sealed class RosterFormatException(string code, string detail) : Exception($"{code}: {detail}")
{
    public string Code { get; } = code;
}
