using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentBridge.Roster;
using static RosterFixture;

// docs/protocol/vectors/roster.json: the encoding primitives and the entry parser's verdicts,
// which pwa/test/unit/roster-vectors.test.mjs replays against the PWA's port, so both accept the
// same entries and refuse the rest with the same code. After a deliberate change,
// UPDATE_PROTOCOL_VECTORS=1 writes the file.
public sealed class RosterVectorTests
{
    public static IReadOnlyList<Signed> Chain()
    {
        var chain = new List<Signed>();
        byte[] H(int i) => chain[i].Hash;

        var genesis = Body("genesis", 0, null, Phone, "2026-11-03T17:20:00Z");
        genesis["device"] = Person(Phone, PhoneSign, "phone", "ios", "Travel iPhone", Presence.Key);
        genesis["approval"] = new JsonObject { ["method"] = "genesis" };
        chain.Add(new("genesis", Bytes(genesis), PhoneSign, null, null, null));

        var addPwa = Body("add", 1, [H(0)], Phone, "2026-11-03T17:25:10.5Z");
        addPwa["device"] = Person(Pwa, PwaSign, "pwa", "windows", "Office browser", Presence.WebAuthn);
        addPwa["approval"] = new JsonObject { ["method"] = "code", ["transcript"] = Codec.Base64Url(Transcript(1)) };
        chain.Add(new("add-by-code", Bytes(addPwa), PhoneSign, null, null, null));

        // Signed by the PWA, whose presence is WebAuthn: its assertion rides in uv.by.
        var addHost = Body("add", 2, [H(1)], Pwa, "2026-11-03T17:31:00Z");
        addHost["device"] = HostDevice(Host1, "windows", "Build box");
        addHost["approval"] = new JsonObject { ["method"] = "qr", ["transcript"] = Codec.Base64Url(Transcript(2)), ["shown"] = "joiner" };
        var addHostBytes = Bytes(addHost);
        chain.Add(new("add-by-qr-with-presence", addHostBytes, PwaSign, null, Authenticate(Presence.Challenge(Presence.RoleBy, addHostBytes)), null));

        var addApp = Body("add", 3, [H(2)], Phone, "2026-11-03T17:40:00.123456789Z");
        addApp["device"] = Person(App, AppSign, "app", "macos", "Desk Mac", Presence.None);
        addApp["approval"] = new JsonObject { ["method"] = "qr", ["transcript"] = Codec.Base64Url(Transcript(3)), ["shown"] = "approver" };
        chain.Add(new("add-by-qr-shown-by-approver", Bytes(addApp), PhoneSign, null, null, null));

        var addOld = Body("add", 4, [H(3)], Phone, "2026-11-03T17:41:00Z");
        addOld["device"] = HostDevice(Host2, "linux", "Old server");
        addOld["approval"] = new JsonObject { ["method"] = "grandfather" };
        chain.Add(new("add-grandfathered", Bytes(addOld), PhoneSign, null, null, null));

        var rename = Body("rename", 5, [H(4)], Phone, "2026-11-04T09:00:00Z");
        rename["target"] = Target(Host1, H(2));
        rename["name"] = "Build box ✓ 世界 🖥";
        chain.Add(new("rename", Bytes(rename), PhoneSign, null, null, null));

        var confirm = Body("confirm", 6, [H(5)], Phone, "2026-11-04T09:01:00Z");
        confirm["target"] = Target(App, H(3));
        chain.Add(new("confirm", Bytes(confirm), PhoneSign, null, null, null));

        // Cosigned by the PWA, with its assertion in uv.cosigner.
        var remove = Body("remove", 7, [H(6)], Phone, "2026-11-04T10:00:00Z");
        remove.Insert(7, "cosigner", Pwa.Device);
        remove["target"] = Target(Host2, H(4));
        remove["keep"] = Hashes([H(5), H(6)]);
        var removeBytes = Bytes(remove);
        chain.Add(new("remove-cosigned", removeBytes, PhoneSign, PwaSign, null, Authenticate(Presence.Challenge(Presence.RoleCosigner, removeBytes))));

        var branch = Body("rename", 7, [H(6)], Pwa, "2026-11-04T10:00:05Z");
        branch["target"] = Target(App, H(3));
        branch["name"] = "Desk Mac (home)";
        chain.Add(new("rename-concurrent", Bytes(branch), PwaSign, null, null, null));

        var merge = Body("merge", 8, [H(7), H(8)], Phone, "2026-11-04T10:05:00Z");
        merge["adopt"] = true;
        chain.Add(new("merge-adopting", Bytes(merge), PhoneSign, null, null, null));

        var leave = Body("remove", 9, [H(9)], App, "2026-11-05T08:00:00Z");
        leave["target"] = Target(App, H(3));
        leave["keep"] = new JsonArray();
        chain.Add(new("self-removal", Bytes(leave), AppSign, null, null, null));

        var mergePlain = Body("merge", 8, [H(7), H(8)], Pwa, "2026-11-04T10:05:01Z");
        chain.Add(new("merge", Bytes(mergePlain), PwaSign, null, null, null));
        return chain;
    }

    // A roster v2 host add (agent-principals.md §8.10, V1), after the PWA's add.
    public static Signed HostV2Add()
    {
        var parent = Chain()[1];
        var b = Body("add", 2, [parent.Hash], Phone, "2026-11-03T17:31:00Z");
        b["v"] = 2;
        b["device"] = HostDeviceV2(Host1, Host1Sign, "linux", "Agent host");
        b["approval"] = new JsonObject { ["method"] = "qr", ["transcript"] = Codec.Base64Url(Transcript(2)), ["shown"] = "joiner" };
        return new("add-host-v2", Bytes(b), PhoneSign, null, null, null);
    }

    static byte[] Transcript(int i) => SHA256.HashData(Encoding.UTF8.GetBytes($"transcript {i}"));

    // Size boundaries: a body padded through an assertion's client data until the entry's fixed
    // content is exactly `size`.
    static Signed Padded(string name, Signed entry, int size)
    {
        var role = entry.Cosigner is null ? Presence.RoleBy : Presence.RoleCosigner;
        var challenge = Presence.Challenge(role, entry.Body);
        var pad = 0;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var a = Authenticate(challenge, clientData: ClientData(challenge, extra: $",\"pad\":\"{new string('x', pad)}\""));
            var padded = role == Presence.RoleBy ? entry with { Name = name, UvBy = a } : entry with { Name = name, UvCosigner = a };
            var at = Size(padded);
            if (at == size) { return padded; }
            pad += size - at;
        }
        throw new InvalidOperationException("the padding did not converge");
    }

    static int Size(Signed e) => e.Body.Length + e.Sig.Length + (e.Cosig?.Length ?? 0) + Sized(e.UvBy) + Sized(e.UvCosigner);

    static int Sized(Assertion? a) => a is null ? 0 : a.AuthenticatorData.Length + a.ClientDataJson.Length + a.Signature.Length;

    public static IReadOnlyList<Signed> Valid()
    {
        var chain = Chain();
        return [.. chain, HostV2Add(), Padded("add-at-8-kib", chain[2], Entry.MaxBytes), Padded("remove-at-46-kib", chain[7], Entry.MaxRemoveBytes)];
    }

    public sealed record Refused(string Name, byte[] Wrapper, string Code);

    // Each changes one thing in a valid entry, so the code it is refused with is that thing's.
    public static IReadOnlyList<Refused> Refusals()
    {
        var chain = Chain();
        var list = new List<Refused>();
        void Text(string name, string code, string wrapper) => list.Add(new(name, Encoding.UTF8.GetBytes(wrapper), code));
        void Raw(string name, string code, byte[] wrapper) => list.Add(new(name, wrapper, code));
        // A body rewritten as text, signed again by its signer so only the change is wrong.
        void Body(string name, string code, Signed entry, Func<string, string> change)
        {
            var text = change(Encoding.UTF8.GetString(entry.Body));
            Assert.NotEqual(Encoding.UTF8.GetString(entry.Body), text);
            Text(name, code, (entry with { Body = Encoding.UTF8.GetBytes(text) }).Wire);
        }
        void Edit(string name, string code, Signed entry, Action<JsonObject> change)
        {
            var body = JsonNode.Parse(entry.Body)!.AsObject();
            change(body);
            Text(name, code, (entry with { Body = Bytes(body) }).Wire);
        }
        var genesis = chain[0];
        var add = chain[1];
        var addHost = chain[2];
        var rename = chain[5];
        var remove = chain[7];
        var merge = chain[9];
        var leave = chain[10];
        var body = Codec.Base64Url(add.Body);
        var sig = Codec.Base64Url(add.Sig);

        // The wrapper.
        Raw("invalid-utf8", Refusal.Json, [.. Encoding.UTF8.GetBytes("{\"body\":\""), 0xC3, 0x28, .. Encoding.UTF8.GetBytes("\",\"sig\":\"x\"}")]);
        Raw("byte-order-mark", Refusal.Json, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(add.Wire)]);
        Text("trailing-content", Refusal.Json, add.Wire + "{}");
        Text("trailing-comma", Refusal.Json, add.Wire[..^1] + ",}");
        Text("lone-surrogate-in-a-name", Refusal.Json, add.Wire[..^1] + ",\"\\ud800\":1}");
        Text("too-deep", Refusal.Json, $"{{\"body\":{new string('[', 16)}{new string(']', 16)},\"sig\":\"{sig}\"}}");
        Text("deep-but-allowed", Refusal.Type, $"{{\"body\":{new string('[', 15)}{new string(']', 15)},\"sig\":\"{sig}\"}}");
        Text("not-an-object", Refusal.Type, $"[\"{body}\",\"{sig}\"]");
        Text("duplicate-sig", Refusal.Duplicate, add.Wire[..^1] + $",\"sig\":\"{sig}\"}}");
        Text("duplicate-by-escape", Refusal.Duplicate, add.Wire[..^1] + $",\"\\u0073ig\":\"{sig}\"}}");
        Text("unknown-wrapper-member", Refusal.Unknown, add.Wire[..^1] + ",\"hash\":\"x\"}");
        Text("member-names-are-case-sensitive", Refusal.Unknown, add.Wire.Replace("\"sig\"", "\"Sig\"", StringComparison.Ordinal));
        Text("missing-sig", Refusal.Missing, $"{{\"body\":\"{body}\"}}");
        Text("missing-body", Refusal.Missing, $"{{\"sig\":\"{sig}\"}}");
        Text("body-not-a-string", Refusal.Type, $"{{\"body\":{{}},\"sig\":\"{sig}\"}}");
        Text("body-padded", Refusal.Base64, $"{{\"body\":\"{body}=\",\"sig\":\"{sig}\"}}");
        var odd = chain.First(e => e.Sig.Length % 3 != 0);
        Text("sig-non-canonical-trailing-bits", Refusal.Base64, odd.Wire.Replace(Codec.Base64Url(odd.Sig), NonCanonical(odd.Sig), StringComparison.Ordinal));
        Text("sig-non-minimal-der", Refusal.Der, $"{{\"body\":\"{body}\",\"sig\":\"{Codec.Base64Url(LongR(add.Sig))}\"}}");
        Text("sig-r-zero", Refusal.Der, $"{{\"body\":\"{body}\",\"sig\":\"{Codec.Base64Url(P256.Der(0, 1))}\"}}");
        Text("sig-high-s", Refusal.HighS, $"{{\"body\":\"{body}\",\"sig\":\"{Codec.Base64Url(P256.Flip(add.Sig))}\"}}");
        Text("cosig-high-s", Refusal.HighS, remove.Wire.Replace(Codec.Base64Url(remove.Cosig!), Codec.Base64Url(P256.Flip(remove.Cosig!)), StringComparison.Ordinal));
        Text("uv-empty", Refusal.Value, add.Wire[..^1] + ",\"uv\":{}}");
        Text("uv-unknown-role", Refusal.Unknown, add.Wire[..^1] + ",\"uv\":{\"approver\":{}}}");
        Text("uv-assertion-missing-signature", Refusal.Missing, addHost.Wire.Replace(",\"signature\":\"" + Codec.Base64Url(addHost.UvBy!.Signature) + "\"", "", StringComparison.Ordinal));
        Text("uv-assertion-non-minimal-der", Refusal.Der, (addHost with { UvBy = addHost.UvBy! with { Signature = LongR(addHost.UvBy.Signature) } }).Wire);

        // Sizes, over fixed content.
        var big = Padded("x", addHost, Entry.MaxBytes + 1);
        Text("add-over-8-kib", Refusal.Size, big.Wire);
        Text("remove-over-46-kib", Refusal.Size, Padded("x", remove, Entry.MaxRemoveBytes + 1).Wire);
        Text("any-entry-over-46-kib-before-its-body-is-read", Refusal.Size, (big with { Body = Encoding.UTF8.GetBytes("not json"), UvBy = Padded("x", addHost, Entry.MaxRemoveBytes + addHost.Body.Length).UvBy }).Wire);

        // The cosigner and its signature and assertion agree.
        Text("cosig-without-cosigner", Refusal.Cosigner, (add with { Cosigner = PwaSign }).Wire);
        Text("cosigner-without-cosig", Refusal.Cosigner, (remove with { Cosigner = null }).Wire);
        Text("uv-cosigner-without-cosigner", Refusal.Cosigner, (add with { UvCosigner = addHost.UvBy }).Wire);

        // The body's syntax.
        Text("body-not-json", Refusal.Json, (add with { Body = Encoding.UTF8.GetBytes("{\"v\":1,") }).Wire);
        Text("body-invalid-utf8", Refusal.Json, (add with { Body = [.. add.Body[..^2], 0xFF, (byte)'}'] }).Wire);
        Text("body-byte-order-mark", Refusal.Json, (add with { Body = [0xEF, 0xBB, 0xBF, .. add.Body] }).Wire);
        Body("body-lone-surrogate-in-a-value", Refusal.Json, rename, t => t.Replace("Build box", "Build \\udc00box", StringComparison.Ordinal));
        Body("body-duplicate-member", Refusal.Duplicate, add, t => t.Replace("\"seq\":1,", "\"seq\":1,\"seq\":1,", StringComparison.Ordinal));
        Body("device-duplicate-member", Refusal.Duplicate, add, t => t.Replace("\"kind\":\"pwa\",", "\"kind\":\"pwa\",\"kind\":\"pwa\",", StringComparison.Ordinal));
        Text("body-not-an-object", Refusal.Type, (add with { Body = Encoding.UTF8.GetBytes("[1]") }).Wire);

        // v and op come first, since they say which members the rest may have.
        Edit("v-missing", Refusal.Missing, add, b => b.Remove("v"));
        Edit("v-2", Refusal.Version, add, b => b["v"] = 2);
        Body("v-1.0", Refusal.Version, add, t => t.Replace("\"v\":1,", "\"v\":1.0,", StringComparison.Ordinal));
        Edit("v-string", Refusal.Version, add, b => b["v"] = "1");
        Edit("op-missing", Refusal.Missing, add, b => b.Remove("op"));
        Edit("op-unknown", Refusal.Value, add, b => b["op"] = "delete");
        Edit("op-case", Refusal.Value, add, b => b["op"] = "Add");
        Edit("op-grants-left-for-later", Refusal.Value, add, b => b["op"] = "grants");
        Edit("op-number", Refusal.Type, add, b => b["op"] = 1);

        // Members, per op.
        Edit("unknown-body-member", Refusal.Unknown, add, b => b["note"] = "hi");
        Edit("unknown-before-missing", Refusal.Unknown, add, b => { b.Remove("account"); b["note"] = "hi"; });
        Edit("genesis-with-prev", Refusal.Unknown, genesis, b => b["prev"] = Hashes([add.Hash]));
        Edit("add-with-cosigner", Refusal.Unknown, add, b => b["cosigner"] = App.Device);
        Edit("add-with-name", Refusal.Unknown, add, b => b["name"] = "x");
        Edit("merge-with-adopt-on-remove", Refusal.Unknown, remove, b => b["adopt"] = true);
        Edit("add-missing-prev", Refusal.Missing, add, b => b.Remove("prev"));
        Edit("missing-in-fixed-order", Refusal.Missing, add, b => { b.Remove("approval"); b.Remove("account"); });
        Edit("remove-missing-keep", Refusal.Missing, remove, b => b.Remove("keep"));
        Edit("rename-missing-name", Refusal.Missing, rename, b => b.Remove("name"));

        // Each field.
        Edit("account-empty", Refusal.Account, add, b => b["account"] = "");
        Edit("account-over-128-bytes", Refusal.Account, add, b => b["account"] = new string('é', 65));
        Edit("account-control", Refusal.Account, add, b => b["account"] = "owner\n");
        Edit("account-number", Refusal.Type, add, b => b["account"] = 7);
        Edit("generation-uppercase", Refusal.Id, add, b => b["generation"] = Generation.ToUpperInvariant());
        Edit("generation-short", Refusal.Id, add, b => b["generation"] = Generation[..^1]);
        Edit("seq-string", Refusal.Type, add, b => b["seq"] = "1");
        Body("seq-leading-zero-is-not-json", Refusal.Json, add, t => t.Replace("\"seq\":1,", "\"seq\":01,", StringComparison.Ordinal));
        Body("seq-negative", Refusal.Seq, add, t => t.Replace("\"seq\":1,", "\"seq\":-1,", StringComparison.Ordinal));
        Body("seq-fraction", Refusal.Seq, add, t => t.Replace("\"seq\":1,", "\"seq\":1.0,", StringComparison.Ordinal));
        Body("seq-exponent", Refusal.Seq, add, t => t.Replace("\"seq\":1,", "\"seq\":1e0,", StringComparison.Ordinal));
        Edit("seq-2-to-the-31", Refusal.Seq, add, b => b["seq"] = 2147483648L);
        Edit("seq-0-on-an-add", Refusal.Seq, add, b => b["seq"] = 0);
        Edit("seq-1-on-a-genesis", Refusal.Seq, genesis, b => b["seq"] = 1);
        Edit("prev-not-an-array", Refusal.Type, add, b => b["prev"] = Codec.Base64Url(genesis.Hash));
        Edit("prev-empty", Refusal.Count, add, b => b["prev"] = new JsonArray());
        Edit("prev-two-on-an-add", Refusal.Count, add, b => b["prev"] = Hashes([genesis.Hash, add.Hash]));
        Edit("merge-one-parent", Refusal.Count, merge, b => b["prev"] = Hashes([genesis.Hash]));
        Edit("merge-five-parents", Refusal.Count, merge, b => b["prev"] = Hashes(chain.Take(5).Select(e => e.Hash)));
        Edit("merge-parents-out-of-order", Refusal.Order, merge, b => b["prev"] = new JsonArray([.. Hashes([genesis.Hash, add.Hash]).Reverse().Select(n => n!.DeepClone())]));
        Edit("merge-parent-twice", Refusal.Order, merge, b => b["prev"] = new JsonArray(Codec.Base64Url(add.Hash), Codec.Base64Url(add.Hash)));
        Edit("prev-hash-31-bytes", Refusal.Length, add, b => b["prev"] = new JsonArray(Codec.Base64Url(genesis.Hash[..31])));
        Edit("prev-hash-padded", Refusal.Base64, add, b => b["prev"] = new JsonArray(Codec.Base64Url(genesis.Hash) + "="));
        Edit("by-uppercase", Refusal.Id, add, b => b["by"] = Phone.Device.ToUpperInvariant());
        Edit("at-without-zone", Refusal.Time, add, b => b["at"] = "2026-11-03T17:25:10");
        Edit("at-offset", Refusal.Time, add, b => b["at"] = "2026-11-03T17:25:10+00:00");
        Edit("at-lowercase-t", Refusal.Time, add, b => b["at"] = "2026-11-03t17:25:10Z");
        Edit("at-february-30", Refusal.Time, add, b => b["at"] = "2026-02-30T17:25:10Z");
        Edit("at-leap-second", Refusal.Time, add, b => b["at"] = "2026-12-31T23:59:60Z");
        Edit("at-ten-fraction-digits", Refusal.Time, add, b => b["at"] = "2026-11-03T17:25:10.1234567890Z");
        Edit("at-empty-fraction", Refusal.Time, add, b => b["at"] = "2026-11-03T17:25:10.Z");
        Edit("at-unix-time", Refusal.Type, add, b => b["at"] = 1793300060);

        // The device.
        Edit("device-not-an-object", Refusal.Type, add, b => b["device"] = "x");
        Edit("kind-missing", Refusal.Missing, add, b => b["device"]!.AsObject().Remove("kind"));
        Edit("kind-test-is-never-rostered", Refusal.Value, add, b => b["device"]!["kind"] = "test");
        Edit("kind-case", Refusal.Value, add, b => b["device"]!["kind"] = "PWA");
        Edit("person-missing-presence", Refusal.Missing, add, b => b["device"]!.AsObject().Remove("presence"));
        Edit("presence-unknown", Refusal.Value, add, b => b["device"]!["presence"] = "fido");
        Edit("host-with-sign", Refusal.Unknown, addHost, b => b["device"]!.AsObject().Insert(2, "sign", Codec.Base64Url(PhoneSign.Public)));
        Edit("host-with-presence", Refusal.Unknown, addHost, b => b["device"]!.AsObject().Insert(2, "presence", "none"));
        Edit("webauthn-without-webauthn-presence", Refusal.Unknown, add, b => b["device"]!["presence"] = Presence.Key);
        Edit("webauthn-presence-without-credential", Refusal.Missing, add, b => b["device"]!.AsObject().Remove("webauthn"));
        Edit("device-unknown-member", Refusal.Unknown, add, b => b["device"]!["model"] = "x");
        Edit("person-missing-sign", Refusal.Missing, add, b => b["device"]!.AsObject().Remove("sign"));
        Edit("device-id-uppercase", Refusal.Id, add, b => b["device"]!["id"] = Pwa.Device.ToUpperInvariant());
        Edit("dh-not-on-the-curve", Refusal.Point, add, b => b["device"]!["dh"] = Codec.Base64Url(OffCurve(Pwa.Public)));
        Edit("dh-compressed", Refusal.Length, add, b => b["device"]!["dh"] = Codec.Base64Url([(byte)(2 + (Pwa.Public[64] & 1)), .. Pwa.Public[1..33]]));
        Edit("dh-hybrid-prefix", Refusal.Point, add, b => b["device"]!["dh"] = Codec.Base64Url([0x06, .. Pwa.Public[1..]]));
        Edit("dh-x-not-below-p", Refusal.Point, add, b => b["device"]!["dh"] = Codec.Base64Url([0x04, .. Enumerable.Repeat((byte)0xFF, 32), .. Pwa.Public[33..]]));
        Edit("device-id-not-its-dh", Refusal.Device, add, b => b["device"]!["id"] = App.Device);
        Edit("sign-not-on-the-curve", Refusal.Point, add, b => b["device"]!["sign"] = Codec.Base64Url(OffCurve(PwaSign.Public)));
        Edit("sign-alg-es256", Refusal.Value, add, b => b["device"]!["signAlg"] = "ES256");
        Edit("webauthn-unknown-member", Refusal.Unknown, add, b => b["device"]!["webauthn"]!["aaguid"] = "x");
        Edit("webauthn-missing-member", Refusal.Missing, add, b => b["device"]!["webauthn"]!.AsObject().Remove("attested"));
        Edit("rp-id-uppercase", Refusal.Value, add, b => b["device"]!["webauthn"]!["rpId"] = "Bridge.example");
        Edit("rp-id-with-scheme", Refusal.Value, add, b => b["device"]!["webauthn"]!["rpId"] = "https://bridge.example");
        Edit("rp-id-empty-label", Refusal.Value, add, b => b["device"]!["webauthn"]!["rpId"] = "bridge..example");
        Edit("credential-id-empty", Refusal.Length, add, b => b["device"]!["webauthn"]!["credentialId"] = "");
        Edit("credential-id-1024-bytes", Refusal.Length, add, b => b["device"]!["webauthn"]!["credentialId"] = Codec.Base64Url(new byte[1024]));
        Edit("credential-key-not-on-the-curve", Refusal.Point, add, b => b["device"]!["webauthn"]!["key"] = Codec.Base64Url(OffCurve(PwaCredential.Public)));
        Edit("attested-string", Refusal.Type, add, b => b["device"]!["webauthn"]!["attested"] = "false");
        Edit("platform-unknown", Refusal.Value, add, b => b["device"]!["platform"] = "beos");
        Edit("platform-case", Refusal.Value, add, b => b["device"]!["platform"] = "iOS");
        Edit("name-empty", Refusal.Name, add, b => b["device"]!["name"] = "");
        Edit("name-65-scalars", Refusal.Name, add, b => b["device"]!["name"] = new string('a', 65));
        Edit("name-65-scalars-257-bytes", Refusal.Name, add, b => b["device"]!["name"] = string.Concat(Enumerable.Repeat("😀", 64)) + "a");
        Edit("name-c0-control", Refusal.Name, add, b => b["device"]!["name"] = "Office\tbrowser");
        Edit("name-c1-control", Refusal.Name, add, b => b["device"]!["name"] = "Office\u0085browser");
        Edit("name-delete", Refusal.Name, add, b => b["device"]!["name"] = "Office\u007Fbrowser");
        Edit("genesis-of-a-host", Refusal.Device, genesis, b => { b["device"] = HostDevice(Phone, "windows", "Build box"); });
        Edit("genesis-signed-by-another-device", Refusal.Signer, genesis, b => b["by"] = Pwa.Device);

        // The approval: every defect is one code.
        Edit("approval-not-an-object", Refusal.Approval, add, b => b["approval"] = "code");
        Edit("approval-method-missing", Refusal.Approval, add, b => b["approval"]!.AsObject().Remove("method"));
        Edit("approval-method-unknown", Refusal.Approval, add, b => b["approval"]!["method"] = "email");
        Edit("approval-code-missing-transcript", Refusal.Approval, add, b => b["approval"]!.AsObject().Remove("transcript"));
        Edit("approval-transcript-31-bytes", Refusal.Approval, add, b => b["approval"]!["transcript"] = Codec.Base64Url(Transcript(1)[..31]));
        Edit("approval-transcript-padded", Refusal.Approval, add, b => b["approval"]!["transcript"] = Codec.Base64Url(Transcript(1)) + "=");
        Edit("approval-code-with-shown", Refusal.Approval, add, b => b["approval"]!["shown"] = "joiner");
        Edit("approval-qr-missing-shown", Refusal.Approval, addHost, b => b["approval"]!.AsObject().Remove("shown"));
        Edit("approval-qr-shown-other", Refusal.Approval, addHost, b => b["approval"]!["shown"] = "relay");
        Edit("approval-grandfather-with-transcript", Refusal.Approval, add, b => b["approval"] = new JsonObject { ["method"] = "grandfather", ["transcript"] = Codec.Base64Url(Transcript(1)) });
        Edit("approval-genesis-on-an-add", Refusal.Approval, add, b => b["approval"] = new JsonObject { ["method"] = "genesis" });
        Edit("approval-code-on-a-genesis", Refusal.Approval, genesis, b => b["approval"] = new JsonObject { ["method"] = "code", ["transcript"] = Codec.Base64Url(Transcript(1)) });
        Edit("approval-genesis-with-transcript", Refusal.Approval, genesis, b => b["approval"]!["transcript"] = Codec.Base64Url(Transcript(1)));
        Edit("approval-unknown-member", Refusal.Approval, add, b => b["approval"]!["note"] = "x");

        // Targets, keep lists and the rest.
        Edit("target-unknown-member", Refusal.Unknown, rename, b => b["target"]!["name"] = "x");
        Edit("target-missing-add", Refusal.Missing, rename, b => b["target"]!.AsObject().Remove("add"));
        Edit("target-add-31-bytes", Refusal.Length, rename, b => b["target"]!["add"] = Codec.Base64Url(addHost.Hash[..31]));
        Edit("keep-out-of-order", Refusal.Order, remove, b => b["keep"] = new JsonArray([.. Hashes([rename.Hash, chain[6].Hash]).Reverse().Select(n => n!.DeepClone())]));
        Edit("keep-twice", Refusal.Order, remove, b => b["keep"] = new JsonArray(Codec.Base64Url(rename.Hash), Codec.Base64Url(rename.Hash)));
        Edit("keep-513", Refusal.Count, remove, b => b["keep"] = Hashes(Enumerable.Range(0, 513).Select(i => SHA256.HashData(BitConverter.GetBytes(i)))));
        Edit("rename-name-control", Refusal.Name, rename, b => b["name"] = "Build\nbox");
        Edit("adopt-false", Refusal.Value, merge, b => b["adopt"] = false);
        Edit("adopt-string", Refusal.Type, merge, b => b["adopt"] = "true");
        Edit("self-removal-keeping", Refusal.Signer, leave, b => b["keep"] = Hashes([rename.Hash]));
        Text("self-removal-cosigned", Refusal.Signer, (leave with { Body = BodyWith(leave, b => b.Insert(7, "cosigner", Pwa.Device)), Cosigner = PwaSign }).Wire);
        Text("cosigner-is-the-signer", Refusal.Signer, (remove with { Body = BodyWith(remove, b => b["cosigner"] = Phone.Device) }).Wire);
        Text("cosigner-is-the-target", Refusal.Signer, (remove with { Body = BodyWith(remove, b => b["cosigner"] = Host2.Device) }).Wire);
        Edit("cosigner-uppercase", Refusal.Id, remove, b => b["cosigner"] = Pwa.Device.ToUpperInvariant());
        return list;
    }

    static byte[] BodyWith(Signed entry, Action<JsonObject> change)
    {
        var body = JsonNode.Parse(entry.Body)!.AsObject();
        change(body);
        return Bytes(body);
    }

    // The same bytes with non-zero unused trailing bits: a second spelling, which is refused.
    static string NonCanonical(byte[] bytes)
    {
        var text = Codec.Base64Url(bytes);
        if (text.Length % 4 == 0) { throw new ArgumentException("no unused bits", nameof(bytes)); }
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        return text[..^1] + alphabet[alphabet.IndexOf(text[^1], StringComparison.Ordinal) + 1];
    }

    // DER whose r has a needless leading zero byte.
    static byte[] LongR(byte[] der)
    {
        var rs = P256.ReadDer(der)!.Value;
        var r = rs.R.ToByteArray(isUnsigned: true, isBigEndian: true);
        r = (r[0] & 0x80) != 0 ? [0, 0, .. r] : [0, .. r];
        var s = P256.Der(rs.R, rs.S)[(4 + der[3])..];
        return [0x30, (byte)(2 + r.Length + s.Length), 0x02, (byte)r.Length, .. r, .. s];
    }

    static byte[] OffCurve(byte[] point)
    {
        var p = point.ToArray();
        p[64] ^= 1;
        return p;
    }

    public sealed record AssertionCase(string Name, WebAuthnCredential Credential, byte[] Challenge, Assertion Assertion, string? Code);

    public static IReadOnlyList<AssertionCase> Assertions()
    {
        var challenge = Presence.Challenge(Presence.RoleBy, Chain()[2].Body);
        var credential = ParsedCredential();
        var good = Authenticate(challenge);
        var cases = new List<AssertionCase>
        {
            new("valid", credential, challenge, good, null),
            new("valid-high-s", credential, challenge, good with { Signature = P256.Flip(good.Signature) }, null),
            new("valid-extra-client-data-member", credential, challenge, Authenticate(challenge, clientData: ClientData(challenge, extra: ",\"other_keys_can_be_added_here\":\"x\"")), null),
            new("authenticator-data-38-bytes", credential, challenge, good with { AuthenticatorData = [.. good.AuthenticatorData, 0] }, Refusal.UvAuthenticatorData),
            new("attested-credential-data-flag", credential, challenge, Authenticate(challenge, flags: 0x45), Refusal.UvAuthenticatorData),
            new("extension-data-flag", credential, challenge, Authenticate(challenge, flags: 0x85), Refusal.UvAuthenticatorData),
            new("another-rp-id", credential, challenge, Authenticate(challenge, rpId: "evil.example"), Refusal.UvRpId),
            new("not-user-present", credential, challenge, Authenticate(challenge, flags: 0x04), Refusal.UvFlags),
            new("not-user-verified", credential, challenge, Authenticate(challenge, flags: 0x01), Refusal.UvFlags),
            new("backed-up-without-backup-eligible", credential, challenge, Authenticate(challenge, flags: 0x15), Refusal.UvFlags),
            new("backup-eligible-unrecorded", credential, challenge, Authenticate(challenge, flags: 0x0D), Refusal.UvBackup),
            new("backup-eligible-credential", ParsedCredential(backupEligible: true), challenge, Authenticate(challenge, flags: 0x0D), Refusal.UvBackup),
            new("client-data-not-json", credential, challenge, Authenticate(challenge, clientData: "{\"type\":"), Refusal.UvClientData),
            new("client-data-duplicate-type", credential, challenge, Authenticate(challenge, clientData: ClientData(challenge, extra: ",\"type\":\"webauthn.get\"")), Refusal.UvClientData),
            new("client-data-not-an-object", credential, challenge, Authenticate(challenge, clientData: "[]"), Refusal.UvClientData),
            new("type-create", credential, challenge, Authenticate(challenge, clientData: ClientData(challenge, type: "webauthn.create")), Refusal.UvType),
            new("another-challenge", credential, challenge, Authenticate(challenge, clientData: ClientData(Presence.Challenge(Presence.RoleCosigner, Chain()[2].Body))), Refusal.UvChallenge),
            new("another-origin", credential, challenge, Authenticate(challenge, clientData: ClientData(challenge, origin: "https://evil.example")), Refusal.UvOrigin),
            new("http-origin", credential, challenge, Authenticate(challenge, clientData: ClientData(challenge, origin: "http://" + RpId)), Refusal.UvOrigin),
            new("cross-origin", credential, challenge, Authenticate(challenge, clientData: ClientData(challenge).Replace("\"crossOrigin\":false", "\"crossOrigin\":true", StringComparison.Ordinal)), Refusal.UvOrigin),
            new("top-origin", credential, challenge, Authenticate(challenge, clientData: ClientData(challenge, extra: ",\"topOrigin\":\"https://evil.example\"")), Refusal.UvOrigin),
            new("another-key", credential, challenge, Authenticate(challenge, key: PwaSign), Refusal.UvSignature),
            new("signature-over-other-data", credential, challenge, good with { Signature = Authenticate(challenge, clientData: ClientData(challenge, extra: ",\"x\":1")).Signature }, Refusal.UvSignature),
        };
        return cases;
    }

    public sealed record EnrollmentCase(string Account, byte[] H, byte[] NA, byte[] NN);

    // The first exchange whose code 1 has a leading zero comes first, so every client is seen
    // to keep it.
    public static IReadOnlyList<EnrollmentCase> Exchanges()
    {
        static byte[] Bytes(string s) => SHA256.HashData(Encoding.UTF8.GetBytes(s));
        var cases = new List<EnrollmentCase>();
        for (var i = 0; i < 1000 && cases.Count == 0; i++)
        {
            var c = new EnrollmentCase(Account, Bytes($"h {i}"), Bytes($"nA {i}"), Bytes($"nN {i}"));
            if (Enrollment.Code1(Enrollment.Transcript(c.Account, c.H, c.NA, c.NN))[0] == '0') { cases.Add(c); }
        }
        cases.Add(new EnrollmentCase("ünïcode account", Bytes("h x"), Bytes("nA x"), Bytes("nN x")));
        return cases;
    }

    [Fact]
    public void Every_valid_entry_parses_hashes_and_verifies_and_every_refusal_has_its_code()
    {
        foreach (var e in Valid())
        {
            var parsed = Entry.Parse(Encoding.UTF8.GetBytes(e.Wire));
            Assert.Equal(e.Hash, parsed.Hash);
            Assert.Equal(e.Wire, parsed.Canonical());
            Assert.True(P256.Verify(e.Signer.Public, Entry.SignedBy(e.Body), parsed.Sig), e.Name);
            if (e.Cosigner is not null) { Assert.True(P256.Verify(e.Cosigner.Public, Entry.SignedByCosigner(e.Body), parsed.Cosig!), e.Name); }
            if (parsed.UvBy is not null) { WebAuthnAssertion.Check(parsed.UvBy, ParsedCredential(), Presence.Challenge(Presence.RoleBy, e.Body)); }
            if (parsed.UvCosigner is not null) { WebAuthnAssertion.Check(parsed.UvCosigner, ParsedCredential(), Presence.Challenge(Presence.RoleCosigner, e.Body)); }
        }
        foreach (var r in Refusals())
        {
            var refused = Assert.Throws<RosterFormatException>(() => Entry.Parse(r.Wrapper));
            Assert.True(r.Code == refused.Code, $"{r.Name}: expected {r.Code}, got {refused.Message}");
        }
        foreach (var a in Assertions())
        {
            if (a.Code is null) { WebAuthnAssertion.Check(a.Assertion, a.Credential, a.Challenge); continue; }
            var refused = Assert.Throws<RosterFormatException>(() => WebAuthnAssertion.Check(a.Assertion, a.Credential, a.Challenge));
            Assert.True(a.Code == refused.Code, $"{a.Name}: expected {a.Code}, got {refused.Message}");
        }
    }

    [Fact]
    public void The_committed_roster_vectors_are_what_the_library_gives()
    {
        var stale = VectorFiles.Stale(new Dictionary<string, JsonObject> { ["roster.json"] = Generate() });
        Assert.True(stale.Count == 0, "docs/protocol/vectors/roster.json is not what the roster library gives; with UPDATE_PROTOCOL_VECTORS=1 this test writes it.");
    }

    static JsonObject Generate()
    {
        var keys = new JsonObject();
        foreach (var k in Keys) { keys[k.Name.Replace(' ', '-')] = k.Json(); }

        var valid = new JsonArray();
        foreach (var e in Valid())
        {
            var parsed = Entry.Parse(Encoding.UTF8.GetBytes(e.Wire));
            var v = new JsonObject
            {
                ["name"] = e.Name,
                ["wrapper"] = e.Wire,
                ["op"] = EntryBody.OpName(parsed.Body.Op),
                ["hash"] = Codec.Base64Url(e.Hash),
                ["size"] = parsed.Size,
                ["signer"] = e.Signer.Name.Replace(' ', '-'),
            };
            if (e.Cosigner is not null) { v["cosigner"] = e.Cosigner.Name.Replace(' ', '-'); }
            valid.Add(v);
        }
        // A wrapper with insignificant whitespace is accepted, and forwarded canonically.
        var spaced = Chain()[7];
        valid.Add(new JsonObject
        {
            ["name"] = "whitespace-is-accepted-and-dropped",
            ["wrapper"] = " {\n  \"body\" : \"" + Codec.Base64Url(spaced.Body) + "\",\r\n\t\"sig\": \"" + Codec.Base64Url(spaced.Sig) + "\", \"cosig\":\"" + Codec.Base64Url(spaced.Cosig!) + "\" ,\"uv\" : { \"cosigner\" : " + Json(spaced.UvCosigner!).ToJsonString() + " } }\n",
            ["canonical"] = spaced.Wire,
            ["op"] = "remove",
            ["hash"] = Codec.Base64Url(spaced.Hash),
            ["size"] = Entry.Parse(Encoding.UTF8.GetBytes(spaced.Wire)).Size,
            ["signer"] = spaced.Signer.Name.Replace(' ', '-'),
            ["cosigner"] = spaced.Cosigner!.Name.Replace(' ', '-'),
        });
        var spacedParsed = Entry.Parse(Encoding.UTF8.GetBytes(valid[^1]!["wrapper"]!.GetValue<string>()));
        Assert.Equal(spaced.Wire, spacedParsed.Canonical());

        var refused = new JsonArray();
        foreach (var r in Refusals())
        {
            var o = new JsonObject { ["name"] = r.Name };
            if (System.Text.Unicode.Utf8.IsValid(r.Wrapper)) { o["wrapper"] = Encoding.UTF8.GetString(r.Wrapper); }
            else { o["wrapperHex"] = r.Wrapper.ToHex(); }
            o["code"] = r.Code;
            refused.Add(o);
        }

        var assertions = new JsonArray();
        foreach (var a in Assertions())
        {
            var o = new JsonObject
            {
                ["name"] = a.Name,
                ["credential"] = Credential(a.Credential.BackupEligible),
                ["challenge"] = Codec.Base64Url(a.Challenge),
                ["assertion"] = Json(a.Assertion),
                ["code"] = a.Code,
            };
            assertions.Add(o);
        }

        var exchanges = new JsonArray();
        foreach (var c in Exchanges())
        {
            var t = Enrollment.Transcript(c.Account, c.H, c.NA, c.NN);
            exchanges.Add(new JsonObject
            {
                ["account"] = c.Account,
                ["h"] = c.H.ToHex(),
                ["nA"] = c.NA.ToHex(),
                ["nN"] = c.NN.ToHex(),
                ["commit"] = Enrollment.Commit(c.H, c.NN).ToHex(),
                ["keyProofMessage"] = Enrollment.KeyProof(c.H).ToHex(),
                ["transcript"] = t.ToHex(),
                ["code1"] = Enrollment.Code1(t),
                ["code2"] = Enrollment.Code2(t),
            });
        }

        var typed = new JsonArray();
        foreach (var input in new[] { "042 917", "042917", " 0 4 2 9 1 7 ", "42917", "0429170", "04291a", "０４２９１７", "042\t917", "" })
        {
            typed.Add(new JsonObject { ["input"] = input, ["typed"] = Enrollment.Typed(input) });
        }

        var qr = new JsonArray();
        foreach (var i in new[] { 1, 2 })
        {
            var q = SHA256.HashData(Encoding.UTF8.GetBytes($"q {i}"))[..Enrollment.QrSecretBytes];
            var h = SHA256.HashData(Encoding.UTF8.GetBytes($"qr h {i}"));
            qr.Add(new JsonObject
            {
                ["q"] = q.ToHex(),
                ["h"] = h.ToHex(),
                ["hint"] = Enrollment.InviteHint(q),
                ["joiner"] = Enrollment.QrJoiner(q, h).ToHex(),
                ["approver"] = Enrollment.QrApprover(q, h).ToHex(),
            });
        }

        var chain = Chain();
        var framing = new JsonArray();
        foreach (var parts in new byte[][][] { [], [[]], [Encoding.ASCII.GetBytes(Framing.EntryLabel), [1, 2, 3]], [Encoding.UTF8.GetBytes("ü"), new byte[300]] })
        {
            framing.Add(new JsonObject { ["parts"] = new JsonArray([.. parts.Select(p => (JsonNode)p.ToHex())]), ["framed"] = Framing.F(parts).ToHex() });
        }

        return new JsonObject
        {
            ["about"] = "The device roster's encoding (#55 R1a): framing, base64url, points, DER, ids, the entry wrapper and body parser with its refusal codes, WebAuthn presence assertions and the enrollment values.",
            ["spec"] = "devices.md",
            ["generatedBy"] = "tests/AgentBridge.Roster.Tests/RosterVectorTests.cs",
            ["account"] = Account,
            ["generation"] = Generation,
            ["keys"] = keys,
            ["framing"] = framing,
            ["base64url"] = new JsonObject
            {
                ["valid"] = new JsonArray([.. new byte[][] { [], [0], [0, 1], [0, 1, 2], [0xFB, 0xFF] }.Select(b => (JsonNode)new JsonObject { ["text"] = Codec.Base64Url(b), ["hex"] = b.ToHex() })]),
                ["refused"] = new JsonArray("AA==", "AAA=", "+/8", "A", "AB", "AAB", "A A", "AA\n"),
            },
            ["points"] = new JsonObject
            {
                ["valid"] = new JsonArray([.. Keys.Take(3).Select(k => (JsonNode)Codec.Base64Url(k.Public))]),
                ["refused"] = new JsonArray(
                    new JsonObject { ["text"] = Codec.Base64Url(OffCurve(Phone.Public)), ["code"] = Refusal.Point },
                    new JsonObject { ["text"] = Codec.Base64Url(Phone.Public[..33]), ["code"] = Refusal.Length },
                    new JsonObject { ["text"] = Codec.Base64Url(new byte[65]), ["code"] = Refusal.Point },
                    new JsonObject { ["text"] = Codec.Base64Url([0x04, .. new byte[64]]), ["code"] = Refusal.Point }),
            },
            ["der"] = new JsonObject
            {
                ["valid"] = new JsonArray([.. chain.Take(3).Select(e => (JsonNode)e.Sig.ToHex())]),
                ["highS"] = new JsonArray([.. chain.Take(3).Select(e => (JsonNode)P256.Flip(e.Sig).ToHex())]),
                ["refused"] = new JsonArray(
                    LongR(chain[0].Sig).ToHex(),
                    P256.Der(0, 1).ToHex(),
                    P256.Der(1, 0).ToHex(),
                    ((byte[])[.. chain[0].Sig, 0]).ToHex(),
                    ((byte[])[0x31, .. chain[0].Sig[1..]]).ToHex(),
                    ((byte[])[.. chain[0].Sig[..3], (byte)(chain[0].Sig[3] + 1), .. chain[0].Sig[4..]]).ToHex()),
            },
            ["ids"] = new JsonArray([.. Keys.Take(3).Select(k => (JsonNode)new JsonObject { ["dh"] = Codec.Base64Url(k.Public), ["device"] = k.Device })]),
            ["fingerprint"] = new JsonObject { ["genesis"] = Codec.Base64Url(chain[0].Hash), ["fingerprint"] = Ids.Fingerprint(chain[0].Hash) },
            ["signed"] = new JsonObject
            {
                ["body"] = Codec.Base64Url(chain[0].Body),
                ["entry"] = Entry.SignedBy(chain[0].Body).ToHex(),
                ["cosign"] = Entry.SignedByCosigner(chain[0].Body).ToHex(),
                ["presenceBy"] = Codec.Base64Url(Presence.Challenge(Presence.RoleBy, chain[0].Body)),
                ["presenceCosigner"] = Codec.Base64Url(Presence.Challenge(Presence.RoleCosigner, chain[0].Body)),
            },
            ["entries"] = new JsonObject { ["valid"] = valid, ["refused"] = refused },
            ["assertions"] = assertions,
            ["enrollment"] = new JsonObject { ["exchanges"] = exchanges, ["typed"] = typed, ["qr"] = qr },
        };
    }
}
