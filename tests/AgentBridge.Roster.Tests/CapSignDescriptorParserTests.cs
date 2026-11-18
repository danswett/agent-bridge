using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Roster;
using static RosterFixture;

namespace AgentBridge.Roster.Tests;

public sealed class CapSignDescriptorParserTests
{
    sealed record Vector(string Name, byte[] Body, string? Code, bool Wrapper = false);

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

    static JsonObject V3Add(string presence = Presence.Key)
    {
        var body = Body("add", 1, [SHA256.HashData("parent"u8)], Phone, "2026-11-17T10:00:00Z");
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

    static JsonObject V3Genesis(string presence = Presence.Key)
    {
        var body = Body("genesis", 0, null, Phone, "2026-11-17T10:00:00Z");
        body["v"] = 3;
        body["device"] = Device(Phone, PhoneSign, RosterKinds.Phone, "ios", presence);
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
        return [0x30, (byte)(2 + r.Length + s.Length), 0x02, (byte)r.Length, .. r, .. s];
    }

    static IReadOnlyList<Vector> Vectors()
    {
        var valid = V3Add();
        var proof = CapSign.Sign(Binding(Pwa));
        var fractional = Encoding.UTF8.GetString(Bytes(V3Add())).Replace("{\"v\":3,", "{\"v\":3.0,", StringComparison.Ordinal);
        var exponent = Encoding.UTF8.GetString(Bytes(V3Add())).Replace("{\"v\":3,", "{\"v\":3e0,", StringComparison.Ordinal);
        var duplicate = Encoding.UTF8.GetString(Bytes(valid)).Replace("\"capSignAlg\":", "\"capSign\":\"x\",\"capSignAlg\":", StringComparison.Ordinal);
        var vectors = new List<Vector>
        {
            new("person-add", Bytes(valid), null),
            new("person-add-webauthn", Bytes(V3Add(Presence.WebAuthn)), null),
            new("person-add-no-presence", Bytes(V3Add(Presence.None)), null),
            new("person-add-by-qr", Bytes(V3QrAdd()), null),
            new("person-genesis", Bytes(V3Genesis()), null),
            new("person-genesis-webauthn", Bytes(V3Genesis(Presence.WebAuthn)), null),
            new("person-genesis-none", Bytes(V3Genesis(Presence.None)), null),
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

        foreach (var op in new[] { "remove", "rename", "confirm", "merge", "future", "Add", "" })
        { BodyRefuse($"version-on-{op}", Refusal.Version, body => body["op"] = op); }
        BodyRefuse("v2-person", Refusal.Version, body => body["v"] = 2);
        foreach (var member in new[] { "hostSign", "hostSignAlg", "hostSignProof", "tier" })
        { DeviceRefuse($"forbidden-{member}", Refusal.Unknown, d => d[member] = "x"); }
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
        DeviceRefuse("proof-malformed-length", Refusal.Der, d => d["capSignProof"] = Codec.Base64Url([.. proof, 0]));
        DeviceRefuse("cap-sign-equals-dh", Refusal.Device, d => d["capSign"] = Codec.Base64Url(Pwa.Public));
        DeviceRefuse("cap-sign-equals-sign", Refusal.Device, d => d["capSign"] = Codec.Base64Url(PwaSign.Public));
        DeviceRefuse("cap-sign-equals-webauthn", Refusal.Device, d => { d["presence"] = Presence.WebAuthn; d["webauthn"] = Credential(); d["capSign"] = Codec.Base64Url(PwaCredential.Public); });
        DeviceRefuse("proof-high-s", Refusal.HighS, d => d["capSignProof"] = Codec.Base64Url(P256.Flip(proof)));
        DeviceRefuse("proof-wrong-account", Refusal.CapProof, d => d["capSignProof"] = Codec.Base64Url(CapSign.Sign(Binding(Pwa, account: "other"))));
        DeviceRefuse("proof-wrong-generation", Refusal.CapProof, d => d["capSignProof"] = Codec.Base64Url(CapSign.Sign(Binding(Pwa, generation: new string('0', 32)))));
        DeviceRefuse("proof-wrong-dh", Refusal.CapProof, d => d["capSignProof"] = Codec.Base64Url(CapSign.Sign(Binding(Phone))));
        DeviceRefuse("proof-wrong-domain", Refusal.CapProof, d => d["capSignProof"] = Codec.Base64Url(CapSign.Sign(Binding(Pwa, label: Framing.EntryLabel))));
        DeviceRefuse("proof-by-other-key", Refusal.CapProof, d => d["capSignProof"] = Codec.Base64Url(TestKey.Named("other capSign").Sign(Binding(Pwa))));
        DeviceRefuse("bad-proof-after-platform", Refusal.Value, d => { d["platform"] = "beos"; d["capSignProof"] = Codec.Base64Url(CapSign.Sign("wrong"u8.ToArray())); });
        DeviceRefuse("bad-proof-after-name", Refusal.Name, d => { d["name"] = ""; d["capSignProof"] = Codec.Base64Url(CapSign.Sign("wrong"u8.ToArray())); });
        BodyRefuse("add-grandfather", Refusal.Approval, body => body["approval"] = new JsonObject { ["method"] = "grandfather" });
        BodyRefuse("add-genesis-approval", Refusal.Approval, body => body["approval"] = new JsonObject { ["method"] = "genesis" });
        BodyRefuse("missing-transcript", Refusal.Approval, body => body["approval"]!.AsObject().Remove("transcript"));
        foreach (JsonNode? transcript in new JsonNode?[] { null, JsonValue.Create(1), JsonValue.Create("AA"), JsonValue.Create(Codec.Base64Url(new byte[32]) + "=") })
        { BodyRefuse($"wrong-transcript-{transcript?.ToJsonString() ?? "null"}", Refusal.Approval, body => body["approval"]!["transcript"] = transcript?.DeepClone()); }
        foreach (var shown in new[] { "", "relay" })
        { BodyRefuse($"qr-shown-{shown}", Refusal.Approval, body => { body["approval"]!["method"] = "qr"; if (shown != "") { body["approval"]!["shown"] = shown; } }); }
        BodyRefuse("qr-shown-type", Refusal.Approval, body => { body["approval"]!["method"] = "qr"; body["approval"]!["shown"] = 1; });
        BodyRefuse("proof-before-approval", Refusal.CapProof, body => { body["device"]!["capSignProof"] = Codec.Base64Url(CapSign.Sign("wrong"u8.ToArray())); body["approval"] = new JsonObject { ["method"] = "grandfather" }; });
        BodyRefuse("genesis-needs-genesis-approval", Refusal.Approval, body => { body["op"] = "genesis"; body["seq"] = 0; body["by"] = Pwa.Device; body.Remove("prev"); body["approval"] = new JsonObject { ["method"] = "code", ["transcript"] = Codec.Base64Url(new byte[32]) }; });
        var oversized = Encoding.UTF8.GetBytes(new string(' ', Entry.MaxBytes) + Encoding.UTF8.GetString(Bytes(valid)));
        vectors.Add(new("oversize-wrapper", Encoding.UTF8.GetBytes(Entry.Canonical(oversized, PhoneSign.Sign(Entry.SignedBy(oversized)))), Refusal.Size, true));
        var legacy = V3Add();
        legacy["v"] = 1;
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
            var refused = Record.Exception(() => { if (vector.Wrapper) { Entry.Parse(vector.Body); } else { EntryBody.Parse(vector.Body); } });
            Assert.True(refused is RosterFormatException, $"{vector.Name}: expected {vector.Code}, got {refused?.GetType().Name ?? "accept"}");
            Assert.Equal(vector.Code, ((RosterFormatException)refused!).Code);
        }
    }

    [Fact]
    public void Nonminimal_proof_has_valid_lengths_but_a_redundant_integer_zero()
    {
        var der = NonMinimal(CapSign.Sign(Binding(Pwa)));
        Assert.Equal(der.Length - 2, der[1]);
        Assert.Equal(0, der[4]);
        Assert.Equal(0, der[5]);
        Assert.Null(P256.ReadDer(der));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Verifier_refuses_v3_genesis_and_add_until_dependent_readers_land(bool acceptHostSign)
    {
        var verifier = new RosterVerifier(Account, Generation, () => TimeSpan.Zero) { AcceptHostSign = acceptHostSign };
        Entry Signed(JsonObject body) { var bytes = Bytes(body); return Entry.Parse(Encoding.UTF8.GetBytes(Entry.Canonical(bytes, PhoneSign.Sign(Entry.SignedBy(bytes))))); }
        var genesis = Signed(V3Genesis());
        Assert.Equal(new Offered(genesis.Hash, Verdict.Refused, Invalid.Version), Assert.Single(verifier.Offer(genesis)));
        var legacy = Entry.Parse(Encoding.UTF8.GetBytes(RosterVectorTests.Chain()[0].Wire));
        Assert.Equal(Verdict.Accepted, Assert.Single(verifier.Offer(legacy)).Verdict);
        var body = V3Add(); body["prev"] = Hashes([legacy.Hash]);
        var add = Signed(body);
        Assert.Equal(new Offered(add.Hash, Verdict.Refused, Invalid.Version), Assert.Single(verifier.Offer(add)));
        Assert.False(verifier.Holds(add.Hash));
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

    internal static string VectorFileText(bool includeDomains = false)
    {
        var lines = new List<string>
        {
            "{",
            "  \"about\": \"C# v3 person-descriptor parser vectors for #2385; vectors are keyed by name and record the first refusal code.\",",
            "  \"spec\": \"devices.md\",",
            "  \"generatedBy\": \"tests/AgentBridge.Roster.Tests/CapSignDescriptorParserTests.cs\",",
            "  \"contexts\": {}, \"records\": {}, \"knownDifferences\": [],",
            $"  \"account\": {JsonSerializer.Serialize(Account, Raw)},",
            $"  \"generation\": {JsonSerializer.Serialize(Generation, Raw)},",
            $"  \"exampleDh\": {JsonSerializer.Serialize(Codec.Base64Url(Pwa.Public), Raw)},",
            $"  \"exampleCapSign\": {JsonSerializer.Serialize(Codec.Base64Url(CapSign.Public), Raw)},",
            $"  \"exampleBinding\": {JsonSerializer.Serialize(Codec.Base64Url(VerifierKey.Binding(Account, Generation, Pwa.Public)), Raw)},",
        };
        if (includeDomains)
        {
            lines.Add("  \"domainInputs\": \"F(label, UTF8(account), ASCII(generation), raw exampleDh); not enrollment transcript T3\",");
            lines.Add("  \"domains\": [");
            var labels = typeof(Framing).GetFields().Where(f => f.IsLiteral && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!).Concat(new[]
                {
                    "agent-bridge fleet registry v1", "agent-bridge verifier support v1",
                    "agent-bridge verifier frontier v1", "agent-bridge verifier set v1",
                    "agent-bridge agent command v1", "agent-bridge session instance v1", "agent-bridge link v1",
                    "agent-bridge link-attest v1", "agent-bridge link tombstone v1", "agent-bridge command v1",
                }).Order(StringComparer.Ordinal).ToArray();
            for (var i = 0; i < labels.Length; i++)
            {
                var framed = Binding(Pwa, label: labels[i]);
                var vector = new JsonObject
                {
                    ["label"] = labels[i], ["framed"] = Codec.Base64Url(framed),
                    ["signature"] = Codec.Base64Url(CapSign.Sign(framed)),
                };
                lines.Add($"    {vector.ToJsonString(Raw)}{(i + 1 == labels.Length ? "" : ",")}");
            }
            lines.Add("  ],");
        }
        lines.Add("  \"vectors\": {");
        var vectors = Vectors();
        for (var i = 0; i < vectors.Count; i++)
        {
            var vector = vectors[i];
            var value = new JsonObject
            {
                [vector.Wrapper ? "wrapper" : "body"] = Encoding.UTF8.GetString(vector.Body),
                ["result"] = vector.Code ?? "valid",
            }.ToJsonString(Raw);
            lines.Add($"    {JsonSerializer.Serialize(vector.Name, Raw)}: {value}{(i + 1 == vectors.Count ? "" : ",")}");
        }
        lines.Add("  }");
        lines.Add("}");
        return string.Join("\n", lines) + "\n";
    }
}
