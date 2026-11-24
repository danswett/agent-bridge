using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBridge.Roster;

// A person's capability-signing key (devices.md "Person verifier evidence"): a distinct P-256 key,
// never its sign, dh or WebAuthn key, whose proof binds it to this account, generation and dh.
public static class CapSignKey
{
    // What `capSignProof` signs: F("agent-bridge verifier key binding v1", account, generation, dh).
    public static byte[] Binding(string account, string generation, ReadOnlySpan<byte> dh) =>
        Framing.F(Framing.Label(Framing.VerifierKeyBindingLabel), Framing.Utf8(account), Framing.Utf8(generation), dh.ToArray());
}

// Writes a v3 person descriptor and genesis in the emitted member order. A library writer only:
// the caller supplies capSign's proof (K2 holds the key); every output is checked by the parser.
public static class PersonDescriptorV3
{
    static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // The exact device bytes D, refusing as the parser would (for example `cap-proof`).
    public static byte[] Write(string account, string generation, byte[] dh, byte[] sign, string presence, WebAuthnCredential? webauthn,
        byte[] capSign, Func<byte[], byte[]> proveCapSign, string kind, string platform, string name)
    {
        var d = new JsonObject { ["id"] = Ids.Device(dh), ["dh"] = Codec.Base64Url(dh), ["sign"] = Codec.Base64Url(sign), ["signAlg"] = EntryBody.SignAlgorithm, ["presence"] = presence };
        if (webauthn is { } c)
        {
            d["webauthn"] = new JsonObject { ["rpId"] = c.RpId, ["credentialId"] = Codec.Base64Url(c.CredentialId), ["key"] = Codec.Base64Url(c.Key), ["attested"] = c.Attested, ["backupEligible"] = c.BackupEligible };
        }
        d["capSign"] = Codec.Base64Url(capSign);
        d["capSignAlg"] = EntryBody.SignAlgorithm;
        d["capSignProof"] = Codec.Base64Url(proveCapSign(CapSignKey.Binding(account, generation, dh)));
        d["kind"] = kind;
        d["platform"] = platform;
        d["name"] = name;
        var device = Encoding.UTF8.GetBytes(d.ToJsonString(Options));
        Genesis(account, generation, "1970-01-01T00:00:00Z", device);
        return device;
    }

    // The genesis body carrying D verbatim, for its own device's `sign` to sign (the caller's job).
    public static byte[] Genesis(string account, string generation, string at, byte[] device)
    {
        var id = JsonNode.Parse(device)?["id"]?.GetValue<string>();
        var head = new JsonObject { ["v"] = 3, ["op"] = "genesis", ["account"] = account, ["generation"] = generation, ["seq"] = 0, ["by"] = id, ["at"] = at }.ToJsonString(Options);
        byte[] body = [.. Encoding.UTF8.GetBytes($"{head[..^1]},\"device\":"), .. device, .. ",\"approval\":{\"method\":\"genesis\"}}"u8];
        EntryBody.Parse(body);
        return body;
    }
}
