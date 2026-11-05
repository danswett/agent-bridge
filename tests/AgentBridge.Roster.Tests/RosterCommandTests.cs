using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentBridge.Roster;
using static RosterFixture;

// Vetoes (§5.6), person commands (§7.2) and head attestations (§6.3), case by case, and the
// generator of docs/protocol/vectors/roster-commands.json, which the PWA's port replays
// (pwa/test/unit/roster-commands.test.mjs). After a deliberate change, UPDATE_PROTOCOL_VECTORS=1
// writes the file.
public sealed class RosterCommandTests
{
    const string Now = "2026-11-04T12:00:00Z";
    const string Session = "5b0c8e9e-3f4a-4c55-9a1e-2d7f6b8c9d01";
    const string Request = "0f8fad5b-d9cb-469f-a165-70867728950e";
    static readonly byte[] HostNonce = SHA256.HashData("agent-bridge test host nonce"u8.ToArray());
    static readonly byte[] OtherNonce = SHA256.HashData("agent-bridge test other nonce"u8.ToArray());
    static readonly byte[] HeldReset = SHA256.HashData("agent-bridge test held reset"u8.ToArray());

    // G, confirmed A (a key), confirmed P (WebAuthn), host H, R removed, and N still on probation.
    internal static World Roster()
    {
        var w = new World();
        w.Genesis();
        w.Add("+A", "G", "A", "G");
        w.Confirm("cA", "G", "+A", "+A");
        w.Add("+P", "G", "P", "cA", kind: "pwa", presence: Presence.WebAuthn);
        w.Confirm("cP", "G", "+P", "+P");
        w.Add("+H", "G", "H", "cP", kind: "host");
        w.Add("+R", "G", "R", "+H");
        w.Remove("G-R", "G", "+R", "+R");
        w.Add("+N", "G", "N", "G-R");
        return w;
    }

    // Every entry accepted at running time 0, and checked an hour later: N is on probation.
    static (World W, RosterVerifier V) Held()
    {
        var w = Roster();
        var v = w.Verifier();
        w.Now = TimeSpan.FromHours(1);
        return (w, v);
    }

    // ---- vetoes ----

    sealed record VetoCase(string Name, string About, Func<World, string> Wrapper, string? Code = null, string? Refusal = null);

    static JsonObject VetoBody(World w, string by, string account = Account, string? generation = null, byte[]? reset = null) => new()
    {
        ["v"] = 1,
        ["op"] = "veto",
        ["account"] = account,
        ["generation"] = generation ?? Generation,
        ["reset"] = Codec.Base64Url(reset ?? HeldReset),
        ["by"] = w.Id(by),
        ["at"] = "2026-11-04T11:00:00Z",
    };

    static string SignVeto(World w, JsonObject body, string by, string? signAs = null, string? uvRole = null)
    {
        var bytes = Bytes(body);
        var sig = World.Keys(signAs ?? by).Sign.Sign(Veto.Signed(bytes));
        var uv = uvRole is null ? null : Authenticate(Presence.Challenge(uvRole, bytes));
        return Veto.Canonical(bytes, sig, uv);
    }

    static IEnumerable<VetoCase> VetoCases() =>
    [
        new("by-a-key-device", "a confirmed person's device with a key vetoes", w => SignVeto(w, VetoBody(w, "A"), "A")),
        new("by-a-device-on-probation", "any active person's device may veto, on probation too", w => SignVeto(w, VetoBody(w, "N"), "N")),
        new("by-a-webauthn-device", "a WebAuthn device vetoes with its assertion for the veto role", w => SignVeto(w, VetoBody(w, "P"), "P", uvRole: Presence.RoleVeto)),
        new("assertion-for-another-role", "an assertion made for a person command does not prove presence for a veto", w => SignVeto(w, VetoBody(w, "P"), "P", uvRole: Presence.RoleCommand), Invalid.Presence),
        new("webauthn-without-assertion", "a WebAuthn device's veto without its assertion", w => SignVeto(w, VetoBody(w, "P"), "P"), Invalid.Presence),
        new("another-reset", "a veto of another reset than the one held", w => SignVeto(w, VetoBody(w, "A", reset: SHA256.HashData("another"u8.ToArray())), "A"), Invalid.Reset),
        new("another-generation", "a veto in another generation", w => SignVeto(w, VetoBody(w, "A", generation: new string('0', 32)), "A"), Invalid.Generation),
        new("another-account", "a veto for another account", w => SignVeto(w, VetoBody(w, "A", account: "someone-else"), "A"), Invalid.Account),
        new("by-a-removed-device", "a removed device's veto", w => SignVeto(w, VetoBody(w, "R"), "R"), Invalid.Signer),
        new("by-a-host", "a host is not a person's device", w => SignVeto(w, VetoBody(w, "H"), "H", signAs: "A"), Invalid.Signer),
        new("signed-by-another-key", "a veto signed by a key that is not by's", w => SignVeto(w, VetoBody(w, "A"), "A", signAs: "G"), Invalid.Signature),
        new("op-not-veto", "a body whose op is not veto", w => { var b = VetoBody(w, "A"); b["op"] = "remove"; return SignVeto(w, b, "A"); }, Refusal: Refusal.Value),
        new("unknown-member", "a body with a member a veto does not have", w => { var b = VetoBody(w, "A"); b["keep"] = new JsonArray(); return SignVeto(w, b, "A"); }, Refusal: Refusal.Unknown),
        new("reset-too-short", "a reset that is not 32 bytes", w => SignVeto(w, VetoBody(w, "A", reset: new byte[31]), "A"), Refusal: Refusal.Length),
        new("empty-uv", "a uv with no assertion", w => { var bytes = Bytes(VetoBody(w, "A")); var sig = World.Keys("A").Sign.Sign(Veto.Signed(bytes)); return $"{{\"body\":\"{Codec.Base64Url(bytes)}\",\"sig\":\"{Codec.Base64Url(sig)}\",\"uv\":{{}}}}"; }, Refusal: Refusal.Value),
    ];

    static string? VetoOutcome(World w, RosterVerifier v, string wrapper, out string? refusal)
    {
        refusal = null;
        Veto veto;
        try { veto = Veto.Parse(Encoding.UTF8.GetBytes(wrapper)); }
        catch (RosterFormatException e)
        {
            refusal = e.Code;
            return null;
        }
        return Veto.Check(veto, v, HeldReset);
    }

    [Fact]
    public void Every_veto_case_gets_its_verdict()
    {
        var (w, v) = Held();
        foreach (var c in VetoCases())
        {
            var code = VetoOutcome(w, v, c.Wrapper(w), out var refusal);
            Assert.True((c.Code, c.Refusal) == (code, refusal), $"{c.Name}: expected ({c.Code}, {c.Refusal}), got ({code}, {refusal})");
        }
    }

    // ---- person commands ----

    sealed record CommandCase(string Name, string About, Func<World, string> Wrapper, Func<World, CommandContext> Context, string? Code = null, string? Refusal = null);

    static JsonObject CommandBody(World w, string action, string by, string issued = "2026-11-04T11:59:00Z", string expires = "2026-11-04T12:04:00Z", string machine = "H", string head = "+N", string? to = "H2")
    {
        var b = new JsonObject { ["v"] = 1, ["action"] = action, ["request"] = Request, ["by"] = w.Id(by) };
        var target = new JsonObject { ["machine"] = w.Id(machine) };
        if (action == CommandAction.Transfer) { target["session"] = Session; }
        if (action == CommandAction.FolderTrustAnswer) { target["question"] = "trust-1"; }
        b["target"] = target;
        if (action == CommandAction.Transfer) { b["to"] = w.Id(to!); }
        if (action == CommandAction.FolderTrustAnswer) { b["choice"] = "once"; }
        if (CommandAction.NeedsNonce(action)) { b["nonce"] = Codec.Base64Url(HostNonce); }
        b["issuedAt"] = issued;
        b["expiresAt"] = expires;
        b["heads"] = Hashes([w[head].Hash]);
        return b;
    }

    // Signed by `by` (or signAs), with an assertion where by is P unless `uv` says otherwise.
    static string SignCommand(World w, JsonObject body, string by, string? signAs = null, bool? uv = null)
    {
        var bytes = Bytes(body);
        var sig = World.Keys(signAs ?? by).Sign.Sign(PersonCommand.Signed(bytes));
        var assertion = uv ?? by == "P" ? Authenticate(Presence.Challenge(Presence.RoleCommand, bytes)) : null;
        return PersonCommand.Canonical(bytes, sig, assertion);
    }

    static CommandContext Context(World w, string action, string? floor = null, byte[]? nonce = null, string[]? previous = null) =>
        new(w.Id("H"), action, Instant.Parse(Now), floor is null ? null : Instant.Parse(floor), nonce ?? HostNonce, previous?.Select(p => w[p].Hash).ToList());

    static CommandCase Case(string name, string about, string action, string by, string? code = null, Func<JsonObject, JsonObject>? edit = null, string? signAs = null, bool? uv = null, Func<World, CommandContext>? context = null, string issued = "2026-11-04T11:59:00Z", string expires = "2026-11-04T12:04:00Z", string machine = "H", string head = "+N", string to = "H2", string? refusal = null) =>
        new(name, about, w =>
        {
            var b = CommandBody(w, action, by, issued, expires, machine, head, to);
            return SignCommand(w, edit is null ? b : edit(b), by, signAs, uv);
        }, context ?? (w => Context(w, action)), code, refusal);

    static IEnumerable<CommandCase> CommandCases() =>
    [
        Case("update", "a confirmed key device asks for an update with this channel's nonce", CommandAction.Update, "A"),
        Case("transfer-with-webauthn", "a WebAuthn device's transfer with its assertion for the command role", CommandAction.Transfer, "P"),
        Case("folder-trust-answer", "a folder-trust answer names its question and choice", CommandAction.FolderTrustAnswer, "A"),
        Case("forget", "forget carries no nonce", CommandAction.Forget, "A"),
        Case("webauthn-without-assertion", "a WebAuthn device's command without its assertion", CommandAction.Update, "P", Invalid.Presence, uv: false),
        Case("key-device-with-assertion", "a key device's command carrying an assertion", CommandAction.Update, "A", Invalid.Presence, uv: true),
        Case("for-another-action", "a command for another action than the op asking", CommandAction.Update, "A", Invalid.Action, context: w => Context(w, CommandAction.Attest)),
        Case("for-another-machine", "a command for another machine", CommandAction.Update, "A", Invalid.Machine, machine: "G"),
        Case("on-probation", "a device on probation cannot command", CommandAction.Update, "N", Invalid.Probation),
        Case("removed", "a removed device cannot command", CommandAction.Update, "R", Invalid.Signer),
        Case("to-itself", "a transfer whose signer is the machine it goes to", CommandAction.Transfer, "A", Invalid.Signer, to: "A"),
        Case("signed-by-another-key", "signed by a key that is not by's", CommandAction.Update, "A", Invalid.Signature, signAs: "G"),
        Case("ten-minutes", "a lifetime of exactly ten minutes, to the nanosecond", CommandAction.Update, "A", issued: "2026-11-04T11:59:00.5Z", expires: "2026-11-04T12:09:00.500000000Z"),
        Case("ten-minutes-and-a-nanosecond", "a lifetime one nanosecond over ten minutes", CommandAction.Update, "A", Invalid.Lifetime, issued: "2026-11-04T11:59:00.5Z", expires: "2026-11-04T12:09:00.500000001Z"),
        Case("expires-when-issued", "a command that expires as it is issued", CommandAction.Update, "A", Invalid.Lifetime, issued: "2026-11-04T11:59:00Z", expires: "2026-11-04T11:59:00Z"),
        Case("two-minutes-ahead", "issued exactly two minutes in the host's future", CommandAction.Update, "A", issued: "2026-11-04T12:02:00Z", expires: "2026-11-04T12:05:00Z"),
        Case("further-ahead", "issued over two minutes in the host's future", CommandAction.Update, "A", Invalid.Future, issued: "2026-11-04T12:02:00.000000001Z", expires: "2026-11-04T12:05:00Z"),
        Case("expired", "expired before now", CommandAction.Update, "A", Invalid.Expired, issued: "2026-11-04T11:50:00Z", expires: "2026-11-04T11:59:59.999999999Z"),
        Case("expires-now", "expiring exactly now is still in time", CommandAction.Update, "A", issued: "2026-11-04T11:55:00Z", expires: Now),
        Case("at-the-floor", "issued at the floor a local clock reset set", CommandAction.Update, "A", Invalid.Floor, context: w => Context(w, CommandAction.Update, floor: "2026-11-04T11:59:00Z")),
        Case("after-the-floor", "issued after the floor", CommandAction.Update, "A", context: w => Context(w, CommandAction.Update, floor: "2026-11-04T11:58:59.999999999Z")),
        Case("another-nonce", "a nonce this channel was not given", CommandAction.Update, "A", Invalid.Nonce, context: w => Context(w, CommandAction.Update, nonce: OtherNonce)),
        Case("an-unknown-head", "a head the host does not hold", CommandAction.Update, "A", Invalid.Heads, edit: b => { b["heads"] = Hashes([SHA256.HashData("unknown"u8.ToArray())]); return b; }),
        Case("attest-forward", "an attestation whose heads descend from the last one taken", CommandAction.Attest, "A", context: w => Context(w, CommandAction.Attest, previous: ["cP"])),
        Case("attest-back", "an attestation that would move the host back", CommandAction.Attest, "A", Invalid.Regress, head: "cP", context: w => Context(w, CommandAction.Attest, previous: ["+N"])),
        Case("unknown-action", "an action person commands do not have", CommandAction.Update, "A", edit: b => { b["action"] = "launch"; return b; }, refusal: Refusal.Value),
        Case("missing-choice", "a folder-trust answer without its choice", CommandAction.FolderTrustAnswer, "A", edit: b => { b.Remove("choice"); return b; }, refusal: Refusal.Missing),
        Case("transfer-with-a-nonce", "a transfer carries no nonce", CommandAction.Transfer, "A", edit: b => { b["nonce"] = Codec.Base64Url(HostNonce); return b; }, refusal: Refusal.Unknown),
        Case("request-not-a-uuid", "a request that is not a lowercase UUID", CommandAction.Update, "A", edit: b => { b["request"] = Request.ToUpperInvariant(); return b; }, refusal: Refusal.Value),
        Case("five-heads", "more than four heads", CommandAction.Update, "A", edit: b => { b["heads"] = Hashes(Enumerable.Range(0, 5).Select(i => SHA256.HashData([(byte)i]))); return b; }, refusal: Refusal.Count),
        Case("an-offset-time", "a time with an offset instead of Z", CommandAction.Update, "A", issued: "2026-11-04T11:59:00+00:00", refusal: Refusal.Time),
    ];

    static string? CommandOutcome(RosterVerifier v, string wrapper, CommandContext context, out string? refusal)
    {
        refusal = null;
        PersonCommand command;
        try { command = PersonCommand.Parse(Encoding.UTF8.GetBytes(wrapper)); }
        catch (RosterFormatException e)
        {
            refusal = e.Code;
            return null;
        }
        return PersonCommand.Check(command, v, context);
    }

    [Fact]
    public void Every_person_command_case_gets_its_verdict()
    {
        var (w, v) = Held();
        foreach (var c in CommandCases())
        {
            var code = CommandOutcome(v, c.Wrapper(w), c.Context(w), out var refusal);
            Assert.True((c.Code, c.Refusal) == (code, refusal), $"{c.Name}: expected ({c.Code}, {c.Refusal}), got ({code}, {refusal})");
        }
    }

    [Fact]
    public void A_host_time_converts_to_the_same_instant_as_its_rfc_3339_form()
    {
        Assert.Equal(Instant.Parse("2026-11-04T12:00:00.1234567Z"), Instant.From(new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567)));
        Assert.Equal(new Instant(-1, 999_999_900), Instant.From(DateTimeOffset.UnixEpoch.AddTicks(-1)));
        Assert.Equal(new Instant(951782400, 0), Instant.Parse("2000-02-29T00:00:00Z"));
    }

    // ---- head attestations ----

    static byte[] Shared(TestKey self, byte[] peer)
    {
        using var mine = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = TestKey.Bytes(self.D), Q = new ECPoint { X = self.Public[1..33], Y = self.Public[33..] } });
        using var theirs = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = peer[1..33], Y = peer[33..] } });
        return mine.DeriveRawSecretAgreement(theirs.PublicKey);
    }

    sealed record Attestation(World W, string Attester, string Host, byte[] Key, byte[] Message, byte[] Mac, IReadOnlyList<byte[]> Heads, IReadOnlyList<byte[]> Revoked, byte[] CommandBody, byte[] CommandMac);

    static Attestation Attest()
    {
        var (w, v) = Held();
        var (attester, host) = (w.Id("A"), w.Id("H"));
        var key = HeadAttestation.Key(Shared(World.Keys("A").Dh, World.Keys("H").Dh.Public), attester, host);
        var heads = v.Heads;
        var revoked = HeadAttestation.Revoked(v);
        var message = HeadAttestation.Message(Account, Generation, heads, host, HostNonce, revoked);
        // A signed command's MAC by the same two devices: HMAC under the same key over its body.
        var commandBody = Bytes(CommandBody(w, CommandAction.Update, "A"));
        return new(w, attester, host, key, message, HeadAttestation.Mac(key, message), heads, revoked, commandBody, HMACSHA256.HashData(key, commandBody));
    }

    [Fact]
    public void An_attestation_is_made_by_either_end_of_the_pair_and_names_what_its_host_revoked()
    {
        var a = Attest();
        Assert.Equal(a.Key, HeadAttestation.Key(Shared(World.Keys("H").Dh, World.Keys("A").Dh.Public), a.Attester, a.Host));
        Assert.True(HeadAttestation.Verify(a.Key, a.Message, a.Mac));
        Assert.Equal([a.W["+R"].Hash], a.Revoked);
        Assert.False(HeadAttestation.Verify(a.Key, HeadAttestation.Message(Account, Generation, a.Heads, a.Host, HostNonce, []), a.Mac));
        Assert.False(HeadAttestation.Verify(a.Key, HeadAttestation.Message(Account, Generation, a.Heads, a.W.Id("G"), HostNonce, a.Revoked), a.Mac));
    }

    // Same key, so the label is what keeps them apart: an attestation's input is never a command's
    // JSON body, and a command's MAC never verifies an attestation.
    [Fact]
    public void An_attestation_never_verifies_as_a_signed_command_nor_a_signed_command_as_an_attestation()
    {
        var a = Attest();
        Assert.Equal(Framing.F(Framing.Label(Framing.HeadLabel)), a.Message[..(4 + Framing.HeadLabel.Length)]);
        Assert.Equal("json", Assert.Throws<RosterFormatException>(() => StrictJson.Parse(a.Message, "command").Dispose()).Code);
        Assert.False(HeadAttestation.Verify(a.Key, a.Message, a.CommandMac));
        Assert.NotEqual(a.Mac, a.CommandMac);
    }

    // ---- the vectors ----

    static JsonObject Generate()
    {
        var (w, v) = Held();
        var entries = new JsonArray([.. w.Labels.Select(l => (JsonNode)new JsonObject { ["label"] = l, ["hash"] = Codec.Base64Url(w[l].Hash), ["wrapper"] = w[l].Wire })]);
        var vetoes = new JsonArray();
        foreach (var c in VetoCases())
        {
            var wrapper = c.Wrapper(w);
            var code = VetoOutcome(w, v, wrapper, out var refusal);
            var o = new JsonObject { ["name"] = c.Name, ["about"] = c.About, ["wrapper"] = wrapper };
            if (refusal is not null) { o["refusal"] = refusal; }
            else { o["code"] = code; }
            vetoes.Add(o);
        }
        var commands = new JsonArray();
        foreach (var c in CommandCases())
        {
            var wrapper = c.Wrapper(w);
            var context = c.Context(w);
            var code = CommandOutcome(v, wrapper, context, out var refusal);
            var ctx = new JsonObject { ["host"] = context.Host, ["action"] = context.Action, ["now"] = Now };
            if (context.Floor is not null) { ctx["floor"] = c.Name == "at-the-floor" ? "2026-11-04T11:59:00Z" : "2026-11-04T11:58:59.999999999Z"; }
            if (context.Nonce is not null) { ctx["nonce"] = Codec.Base64Url(context.Nonce); }
            if (context.Previous is not null) { ctx["previous"] = new JsonArray([.. context.Previous.Select(h => (JsonNode)Codec.Base64Url(h))]); }
            var o = new JsonObject { ["name"] = c.Name, ["about"] = c.About, ["wrapper"] = wrapper, ["context"] = ctx };
            if (refusal is not null) { o["refusal"] = refusal; }
            else { o["code"] = code; }
            commands.Add(o);
        }
        var a = Attest();
        var attesterKey = World.Keys("A").Dh;
        var attestation = new JsonObject
        {
            ["attester"] = a.Attester,
            ["attesterPrivate"] = Codec.Base64Url(TestKey.Bytes(attesterKey.D)),
            ["attesterPublic"] = Codec.Base64Url(attesterKey.Public),
            ["host"] = a.Host,
            ["hostPublic"] = Codec.Base64Url(World.Keys("H").Dh.Public),
            ["key"] = Codec.Hex(a.Key),
            ["account"] = Account,
            ["generation"] = Generation,
            ["heads"] = new JsonArray([.. a.Heads.Select(h => (JsonNode)Codec.Base64Url(h))]),
            ["nonce"] = Codec.Base64Url(HostNonce),
            ["revoked"] = new JsonArray([.. a.Revoked.Select(h => (JsonNode)Codec.Base64Url(h))]),
            ["message"] = Codec.Hex(a.Message),
            ["mac"] = Codec.Base64Url(a.Mac),
            ["command"] = new JsonObject { ["body"] = Codec.Base64Url(a.CommandBody), ["mac"] = Codec.Base64Url(a.CommandMac) },
        };
        return new JsonObject
        {
            ["about"] = "The device roster's vetoes (design §5.6), person commands (§7.2) and head attestations (§6.3) (#55 R1c). Each veto and command is checked against the roster in entries, every entry accepted at running time 0 and checked an hour later, so N is on probation. A case with refusal is refused by the parser with that code; otherwise code is the check's (null: accepted). A veto vetoes the held reset heldReset. An attestation's key is HKDF from ECDH between attester and host, as a signed command's; command is a signed command's body and MAC under the same key, which must not verify the attestation, and the attestation's message must not parse as a command body.",
            ["spec"] = "devices.md",
            ["generatedBy"] = "tests/AgentBridge.Roster.Tests/RosterCommandTests.cs",
            ["account"] = Account,
            ["generation"] = Generation,
            ["devices"] = new JsonObject([.. new[] { "G", "A", "P", "H", "R", "N", "H2" }.Select(n => KeyValuePair.Create(n, (JsonNode?)w.Id(n)))]),
            ["entries"] = entries,
            ["heldReset"] = Codec.Base64Url(HeldReset),
            ["vetoes"] = vetoes,
            ["commands"] = commands,
            ["attestation"] = attestation,
        };
    }

    [Fact]
    public void The_committed_roster_command_vectors_are_what_the_library_gives()
    {
        var stale = VectorFiles.Stale(new Dictionary<string, JsonObject> { ["roster-commands.json"] = Generate() });
        Assert.True(stale.Count == 0, "docs/protocol/vectors/roster-commands.json is not what the roster library gives; with UPDATE_PROTOCOL_VECTORS=1 this test writes it.");
    }
}
