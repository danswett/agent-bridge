using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Roster;
using static RosterFixture;

namespace AgentBridge.Roster.Tests;

public sealed class CapSignDescriptorParserTests
{
    sealed record Vector(string Name, byte[] Body, string? Code);

    static readonly TestKey CapSign = TestKey.Named("person capSign");

    static byte[] Binding(TestKey dh, string account = Account, string? generation = null, string label = "agent-bridge verifier key binding v1") =>
        Framing.F(Framing.Label(label), Framing.Utf8(account), Framing.Utf8(generation ?? Generation), dh.Public);

    static JsonObject Device(TestKey dh, TestKey sign, string kind, string platform, string presence)
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
        d["capSign"] = Codec.Base64Url(CapSign.Public);
        d["capSignAlg"] = EntryBody.SignAlgorithm;
        d["capSignProof"] = Codec.Base64Url(CapSign.Sign(Binding(dh)));
        d["kind"] = kind;
        d["platform"] = platform;
        d["name"] = "Desk";
        return d;
    }

    static JsonObject Add() => Body("add", 1, [SHA256.HashData("parent"u8)], Phone, "2026-11-17T10:00:00Z");

    static JsonObject V3Add(string presence = Presence.Key)
    {
        var body = Add();
        body["v"] = 3;
        body["device"] = Device(Pwa, PwaSign, RosterKinds.Pwa, "windows", presence);
        body["approval"] = new JsonObject { ["method"] = "code", ["transcript"] = Codec.Base64Url(new byte[32]) };
        return body;
    }

    static JsonObject V3QrAdd()
    {
        var body = V3Add();
        body["approval"] = new JsonObject { ["method"] = "qr", ["transcript"] = Codec.Base64Url(new byte[32]), ["shown"] = "joiner" };
        return body;
    }

    static JsonObject V3Genesis()
    {
        var body = Body("genesis", 0, null, Phone, "2026-11-17T10:00:00Z");
        body["v"] = 3;
        body["device"] = Device(Phone, PhoneSign, RosterKinds.Phone, "ios", Presence.Key);
        body["approval"] = new JsonObject { ["method"] = "genesis" };
        return body;
    }

    static byte[] Bytes(JsonObject body) => Encoding.UTF8.GetBytes(body.ToJsonString(Raw));

    static byte[] NonMinimal(byte[] der)
    {
        var rs = P256.ReadDer(der)!.Value;
        var r = rs.R.ToByteArray(isUnsigned: true, isBigEndian: true);
        r = (r[0] & 0x80) != 0 ? [0, 0, .. r] : [0, .. r];
        var s = P256.Der(rs.R, rs.S)[(4 + der[3])..];
        return [0x30, (byte)(4 + r.Length + s.Length), 0x02, (byte)r.Length, .. r, .. s];
    }

    static IReadOnlyList<Vector> Vectors()
    {
        var valid = V3Add();
        var genesis = V3Genesis();
        var webauthn = V3Add(Presence.WebAuthn);
        var noPresence = V3Add(Presence.None);
        var qr = V3QrAdd();
        var proof = CapSign.Sign(Binding(Pwa));
        var fractional = Encoding.UTF8.GetString(Bytes(V3Add())).Replace("{\"v\":3,", "{\"v\":3.0,", StringComparison.Ordinal);
        var exponent = Encoding.UTF8.GetString(Bytes(V3Add())).Replace("{\"v\":3,", "{\"v\":3e0,", StringComparison.Ordinal);
        var duplicate = Encoding.UTF8.GetString(Bytes(valid)).Replace("\"capSignAlg\":", "\"capSign\":\"x\",\"capSignAlg\":", StringComparison.Ordinal);
        var vectors = new List<Vector>
        {
            new("person-add", Bytes(valid), null),
            new("person-add-webauthn", Bytes(webauthn), null),
            new("person-add-no-presence", Bytes(noPresence), null),
            new("person-add-by-qr", Bytes(qr), null),
            new("person-genesis", Bytes(genesis), null),
            new("fractional-version", Encoding.UTF8.GetBytes(fractional), Refusal.Version),
            new("exponent-version", Encoding.UTF8.GetBytes(exponent), Refusal.Version),
            new("string-version", Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Bytes(V3Add())).Replace("\"v\":3,", "\"v\":\"3\",", StringComparison.Ordinal)), Refusal.Version),
            new("duplicate-cap-sign", Encoding.UTF8.GetBytes(duplicate), Refusal.Duplicate),
        };
        void Refuse(string name, string code, JsonObject body) => vectors.Add(new(name, Bytes(body), code));
        void DeviceRefuse(string name, string code, Action<JsonObject> change)
        {
            var body = V3Add();
            change(body["device"]!.AsObject());
            Refuse(name, code, body);
        }
        void BodyRefuse(string name, string code, Action<JsonObject> change)
        {
            var body = V3Add();
            change(body);
            Refuse(name, code, body);
        }

        BodyRefuse("version-on-remove", Refusal.Version, body => body["op"] = "remove");
        DeviceRefuse("host-before-presence", Refusal.Version, d => { d["kind"] = RosterKinds.Host; d.Remove("presence"); });
        DeviceRefuse("kind-type-before-presence", Refusal.Type, d => { d["kind"] = 1; d["presence"] = 1; });
        DeviceRefuse("unknown-before-missing", Refusal.Unknown, d => { d.Remove("capSign"); d["keyId"] = "x"; });
        DeviceRefuse("presence-type", Refusal.Type, d => d["presence"] = 1);
        DeviceRefuse("presence-value", Refusal.Value, d => d["presence"] = "fido");
        DeviceRefuse("webauthn-required", Refusal.Missing, d => { d["presence"] = Presence.WebAuthn; d.Remove("webauthn"); });
        DeviceRefuse("webauthn-forbidden", Refusal.Unknown, d => d["webauthn"] = Credential());
        DeviceRefuse("missing-cap-sign", Refusal.Missing, d => d.Remove("capSign"));
        DeviceRefuse("missing-cap-sign-algorithm", Refusal.Missing, d => d.Remove("capSignAlg"));
        DeviceRefuse("missing-cap-sign-proof", Refusal.Missing, d => d.Remove("capSignProof"));
        DeviceRefuse("cap-sign-type", Refusal.Type, d => d["capSign"] = 7);
        DeviceRefuse("cap-sign-algorithm-type", Refusal.Type, d => d["capSignAlg"] = 1);
        DeviceRefuse("cap-sign-algorithm", Refusal.Value, d => d["capSignAlg"] = "ES256");
        DeviceRefuse("cap-sign-padded-base64", Refusal.Base64, d => d["capSign"] = Codec.Base64Url(CapSign.Public) + "=");
        DeviceRefuse("cap-sign-short-point", Refusal.Length, d => d["capSign"] = Codec.Base64Url(new byte[64]));
        DeviceRefuse("cap-sign-off-curve", Refusal.Point, d => d["capSign"] = Codec.Base64Url(new byte[65]));
        DeviceRefuse("proof-type", Refusal.Type, d => d["capSignProof"] = 1);
        DeviceRefuse("proof-padded-base64", Refusal.Base64, d => d["capSignProof"] = Codec.Base64Url(proof) + "=");
        DeviceRefuse("proof-nonminimal-der", Refusal.Der, d => d["capSignProof"] = Codec.Base64Url(NonMinimal(proof)));
        DeviceRefuse("cap-sign-equals-dh", Refusal.Device, d => d["capSign"] = Codec.Base64Url(Pwa.Public));
        DeviceRefuse("cap-sign-equals-sign", Refusal.Device, d => d["capSign"] = Codec.Base64Url(PwaSign.Public));
        DeviceRefuse("cap-sign-equals-webauthn", Refusal.Device, d => { d["presence"] = Presence.WebAuthn; d["webauthn"] = Credential(); d["capSign"] = Codec.Base64Url(PwaCredential.Public); });
        DeviceRefuse("proof-high-s", Refusal.HighS, d => d["capSignProof"] = Codec.Base64Url(P256.Flip(proof)));
        DeviceRefuse("proof-wrong-account", "cap-proof", d =>
        {
            d["capSignProof"] = Codec.Base64Url(CapSign.Sign(Binding(Pwa, account: "other")));
        });
        DeviceRefuse("proof-wrong-generation", Refusal.CapProof, d => d["capSignProof"] = Codec.Base64Url(CapSign.Sign(Binding(Pwa, generation: new string('0', 32)))));
        DeviceRefuse("proof-wrong-dh", Refusal.CapProof, d => d["capSignProof"] = Codec.Base64Url(CapSign.Sign(Binding(Phone))));
        DeviceRefuse("proof-wrong-domain", Refusal.CapProof, d => d["capSignProof"] = Codec.Base64Url(CapSign.Sign(Binding(Pwa, label: Framing.EntryLabel))));
        DeviceRefuse("proof-by-other-key", Refusal.CapProof, d => d["capSignProof"] = Codec.Base64Url(TestKey.Named("other capSign").Sign(Binding(Pwa))));
        DeviceRefuse("bad-proof-after-platform", Refusal.Value, d => { d["platform"] = "beos"; d["capSignProof"] = Codec.Base64Url(CapSign.Sign("wrong"u8.ToArray())); });
        DeviceRefuse("bad-proof-after-name", Refusal.Name, d => { d["name"] = ""; d["capSignProof"] = Codec.Base64Url(CapSign.Sign("wrong"u8.ToArray())); });
        BodyRefuse("add-grandfather", Refusal.Approval, body => body["approval"] = new JsonObject { ["method"] = "grandfather" });
        BodyRefuse("genesis-needs-genesis-approval", Refusal.Approval, body => { body["op"] = "genesis"; body["seq"] = 0; body["by"] = Pwa.Device; body.Remove("prev"); body["approval"] = new JsonObject { ["method"] = "code", ["transcript"] = Codec.Base64Url(new byte[32]) }; });
        var legacy = Body("add", 1, [SHA256.HashData("parent"u8)], Phone, "2026-11-17T10:00:00Z");
        legacy["device"] = Person(Pwa, PwaSign, RosterKinds.Pwa, "windows", "Desk", Presence.Key);
        legacy["device"]!["capSign"] = Codec.Base64Url(CapSign.Public);
        legacy["approval"] = new JsonObject { ["method"] = "code", ["transcript"] = Codec.Base64Url(new byte[32]) };
        Refuse("legacy-cap-sign", Refusal.Unknown, legacy);
        var hostV2 = JsonNode.Parse(RosterVectorTests.HostV2Add().Body)!.AsObject();
        hostV2["device"]!["capSign"] = Codec.Base64Url(CapSign.Public);
        Refuse("host-v2-cap-sign", Refusal.Unknown, hostV2);
        return vectors;
    }

    [Fact]
    public void Strict_v3_parser_vectors_accept_or_refuse_with_the_first_code()
    {
        foreach (var vector in Vectors())
        {
            if (vector.Code is null)
            {
                var parsed = EntryBody.Parse(vector.Body);
                Assert.Equal(3, parsed.Version);
                Assert.NotNull(parsed.Device?.CapSign);
                Assert.Equal(EntryBody.SignAlgorithm, parsed.Device.CapSignAlg);
                Assert.NotNull(parsed.Device.CapSignProof);
                continue;
            }
            var refused = Record.Exception(() => EntryBody.Parse(vector.Body));
            Assert.True(refused is RosterFormatException, $"{vector.Name}: expected {vector.Code}, got {refused?.GetType().Name ?? "accept"}");
            Assert.Equal(vector.Code, ((RosterFormatException)refused!).Code);
        }
    }

    [Fact]
    public void The_committed_capSign_parser_vectors_match_the_parser_cases()
    {
        var path = Path.Combine(VectorFiles.Folder, "capsign-descriptor.json");
        var text = VectorFileText();
        if (Environment.GetEnvironmentVariable(VectorFiles.UpdateVariable) == "1")
        {
            File.WriteAllText(path, text);
        }
        else { Assert.Equal(text, File.ReadAllText(path).ReplaceLineEndings("\n")); }
    }

    static string VectorFileText()
    {
        var lines = new List<string>
        {
            "{",
            "  \"about\": \"C# v3 person-descriptor parser vectors for #2385; vectors are keyed by name and record the first refusal code.\",",
            "  \"spec\": \"devices.md\",",
            "  \"generatedBy\": \"tests/AgentBridge.Roster.Tests/CapSignDescriptorParserTests.cs\",",
            $"  \"account\": {JsonSerializer.Serialize(Account, Raw)},",
            $"  \"generation\": {JsonSerializer.Serialize(Generation, Raw)},",
            $"  \"exampleDh\": {JsonSerializer.Serialize(Codec.Base64Url(Pwa.Public), Raw)},",
            $"  \"exampleCapSign\": {JsonSerializer.Serialize(Codec.Base64Url(CapSign.Public), Raw)},",
            $"  \"exampleBinding\": {JsonSerializer.Serialize(Codec.Base64Url(VerifierKey.Binding(Account, Generation, Pwa.Public)), Raw)},",
            "  \"vectors\": {",
        };
        var vectors = Vectors();
        for (var i = 0; i < vectors.Count; i++)
        {
            var vector = vectors[i];
            var value = new JsonObject
            {
                ["body"] = Encoding.UTF8.GetString(vector.Body),
                ["result"] = vector.Code ?? "valid",
            }.ToJsonString(Raw);
            lines.Add($"    {JsonSerializer.Serialize(vector.Name, Raw)}: {value}{(i + 1 == vectors.Count ? "" : ",")}");
        }
        lines.Add("  }");
        lines.Add("}");
        return string.Join("\n", lines) + "\n";
    }
}
