using System.Security.Cryptography;
using System.Text.Json;

namespace AgentBridge.Roster;

// A WebAuthn assertion as a presence proof (§4.6): any verifier checks it against the credential
// recorded in the signer's add, never against a list of platforms, so verifiers on different
// versions agree. The checks run in this order, and pwa/js/roster/webauthn.js runs the same.
public static class WebAuthnAssertion
{
    const byte UserPresent = 0x01;
    const byte UserVerified = 0x04;
    const byte BackupEligible = 0x08;
    const byte BackedUp = 0x10;
    const byte AttestedCredentialData = 0x40;
    const byte Extensions = 0x80;

    // rpIdHash (32) ‖ flags (1) ‖ signCount (4). An assertion carries no attested credential data,
    // and a presence proof takes no extensions, so exactly that.
    public const int AuthenticatorDataBytes = 37;

    // The origin a credential's assertions come from: the PWA's, https on the relying party id.
    public static string Origin(string rpId) => "https://" + rpId;

    // Throws RosterFormatException with the first failed check.
    public static void Check(Assertion a, WebAuthnCredential credential, byte[] challenge)
    {
        var data = a.AuthenticatorData;
        if (data.Length != AuthenticatorDataBytes || (data[32] & (AttestedCredentialData | Extensions)) != 0)
        {
            throw new RosterFormatException(Refusal.UvAuthenticatorData, "authenticatorData");
        }
        if (!data.AsSpan(0, 32).SequenceEqual(SHA256.HashData(Framing.Utf8(credential.RpId))))
        {
            throw new RosterFormatException(Refusal.UvRpId, "rpIdHash");
        }
        var flags = data[32];
        if ((flags & UserPresent) == 0 || (flags & UserVerified) == 0 || ((flags & BackedUp) != 0 && (flags & BackupEligible) == 0))
        {
            throw new RosterFormatException(Refusal.UvFlags, "flags");
        }
        // A synced credential never proves presence in v1 (§4.6), and every assertion repeats the
        // registration's BE flag.
        if (credential.BackupEligible || ((flags & BackupEligible) != 0) != credential.BackupEligible)
        {
            throw new RosterFormatException(Refusal.UvBackup, "BE flag");
        }

        JsonDocument client;
        try { client = StrictJson.Parse(a.ClientDataJson, "clientDataJSON"); }
        catch (RosterFormatException) { throw new RosterFormatException(Refusal.UvClientData, "clientDataJSON"); }
        using (client)
        {
            var root = client.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { throw new RosterFormatException(Refusal.UvClientData, "clientDataJSON"); }
            if (Member(root, "type") != "webauthn.get") { throw new RosterFormatException(Refusal.UvType, "type"); }
            if (Member(root, "challenge") != Codec.Base64Url(challenge)) { throw new RosterFormatException(Refusal.UvChallenge, "challenge"); }
            if (Member(root, "origin") != Origin(credential.RpId)
                || (root.TryGetProperty("crossOrigin", out var cross) && cross.ValueKind != JsonValueKind.False)
                || root.TryGetProperty("topOrigin", out _))
            {
                throw new RosterFormatException(Refusal.UvOrigin, "origin");
            }
        }

        P256.CheckRosterSignature(a.Signature, "signature");
        var signed = new byte[data.Length + 32];
        data.CopyTo(signed, 0);
        SHA256.HashData(a.ClientDataJson).CopyTo(signed, data.Length);
        if (!P256.Verify(credential.Key, signed, a.Signature)) { throw new RosterFormatException(Refusal.UvSignature, "signature"); }
    }

    static string? Member(JsonElement root, string name) =>
        root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}
