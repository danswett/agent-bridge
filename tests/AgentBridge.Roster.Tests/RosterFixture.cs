using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Roster;

// The fixture docs/protocol/vectors/roster.json is generated from: fixed keys, a chain of valid
// entries of every op, and refusals built by changing one thing in a valid entry.
public static class RosterFixture
{
    public const string Account = "owner@example";
    public const string RpId = "bridge.example";
    public static readonly string Generation = Codec.Hex(SHA256.HashData(Encoding.UTF8.GetBytes("agent-bridge roster test generation")).AsSpan(0, 16));

    public static readonly TestKey Phone = TestKey.Named("phone dh");
    public static readonly TestKey PhoneSign = TestKey.Named("phone sign");
    public static readonly TestKey Pwa = TestKey.Named("pwa dh");
    public static readonly TestKey PwaSign = TestKey.Named("pwa sign");
    public static readonly TestKey PwaCredential = TestKey.Named("pwa webauthn");
    public static readonly TestKey App = TestKey.Named("app dh");
    public static readonly TestKey AppSign = TestKey.Named("app sign");
    public static readonly TestKey Host1 = TestKey.Named("host one dh");
    public static readonly TestKey Host2 = TestKey.Named("host two dh");

    public static readonly byte[] CredentialId = SHA256.HashData(Encoding.UTF8.GetBytes("pwa credential"));

    public static readonly JsonSerializerOptions Raw = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static IEnumerable<TestKey> Keys => [Phone, PhoneSign, Pwa, PwaSign, PwaCredential, App, AppSign, Host1, Host2];

    public static JsonObject Person(TestKey dh, TestKey sign, string kind, string platform, string name, string presence)
    {
        var d = new JsonObject
        {
            ["id"] = dh.Device,
            ["dh"] = Codec.Base64Url(dh.Public),
            ["sign"] = Codec.Base64Url(sign.Public),
            ["signAlg"] = EntryBody.SignAlgorithm,
            ["presence"] = presence,
        };
        if (presence == Presence.WebAuthn) { d["webauthn"] = Credential(); }
        d["kind"] = kind;
        d["platform"] = platform;
        d["name"] = name;
        return d;
    }

    public static JsonObject Credential(bool backupEligible = false) => new()
    {
        ["rpId"] = RpId,
        ["credentialId"] = Codec.Base64Url(CredentialId),
        ["key"] = Codec.Base64Url(PwaCredential.Public),
        ["attested"] = false,
        ["backupEligible"] = backupEligible,
    };

    public static WebAuthnCredential ParsedCredential(bool backupEligible = false) =>
        new(RpId, CredentialId, PwaCredential.Public, false, backupEligible);

    public static JsonObject HostDevice(TestKey dh, string platform, string name) => new()
    {
        ["id"] = dh.Device,
        ["dh"] = Codec.Base64Url(dh.Public),
        ["kind"] = RosterKinds.Host,
        ["platform"] = platform,
        ["name"] = name,
    };

    public static JsonObject Body(string op, int seq, IEnumerable<byte[]>? prev, TestKey by, string at)
    {
        var b = new JsonObject { ["v"] = 1, ["op"] = op, ["account"] = Account, ["generation"] = Generation, ["seq"] = seq };
        if (prev is not null) { b["prev"] = Hashes(prev); }
        b["by"] = by.Device;
        b["at"] = at;
        return b;
    }

    public static JsonArray Hashes(IEnumerable<byte[]> hashes) =>
        [.. hashes.Order(Ids.HeadOrder).Select(h => (JsonNode)Codec.Base64Url(h))];

    public static JsonObject Target(TestKey device, byte[] add) => new() { ["id"] = device.Device, ["add"] = Codec.Base64Url(add) };

    public static byte[] Bytes(JsonNode body) => Encoding.UTF8.GetBytes(body.ToJsonString(Raw));

    // A WebAuthn assertion as an authenticator gives one for this credential and challenge.
    public static Assertion Authenticate(byte[] challenge, byte flags = 0x05, string? clientData = null, string rpId = RpId, TestKey? key = null)
    {
        byte[] data = [.. SHA256.HashData(Encoding.UTF8.GetBytes(rpId)), flags, 0, 0, 0, 7];
        var client = Encoding.UTF8.GetBytes(clientData ?? ClientData(challenge));
        return new Assertion(data, client, (key ?? PwaCredential).Sign([.. data, .. SHA256.HashData(client)]));
    }

    public static string ClientData(byte[] challenge, string type = "webauthn.get", string? origin = null, string extra = "") =>
        $"{{\"type\":\"{type}\",\"challenge\":\"{Codec.Base64Url(challenge)}\",\"origin\":\"{origin ?? WebAuthnAssertion.Origin(RpId)}\",\"crossOrigin\":false{extra}}}";

    public static JsonObject Json(Assertion a) => new()
    {
        ["authenticatorData"] = Codec.Base64Url(a.AuthenticatorData),
        ["clientDataJSON"] = Codec.Base64Url(a.ClientDataJson),
        ["signature"] = Codec.Base64Url(a.Signature),
    };
}

// One entry of the chain: its body and who signs it, with the wrapper it travels as.
public sealed record Signed(string Name, byte[] Body, TestKey Signer, TestKey? Cosigner, Assertion? UvBy, Assertion? UvCosigner)
{
    public byte[] Hash => Ids.Entry(Body);
    public byte[] Sig => Signer.Sign(Entry.SignedBy(Body));
    public byte[]? Cosig => Cosigner?.Sign(Entry.SignedByCosigner(Body));
    public string Wire => Entry.Canonical(Body, Sig, Cosig, UvBy, UvCosigner);
}
