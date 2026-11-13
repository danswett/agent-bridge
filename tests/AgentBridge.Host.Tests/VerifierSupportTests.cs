using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Roster;
using Xunit;

public sealed class VerifierSupportTests
{
    const string Unsupported = "unsupported-verifier";
    const string Label = "agent-bridge verifier support v1";
    const string Command = """{"v":1,"action":"accept-host-sign","request":"0f8fad5b-d9cb-469f-a165-70867728950e","by":"11111111111111111111111111111111","target":{"machine":"44444444444444444444444444444444"},"nonce":"ZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmZmY","issuedAt":"2026-11-12T18:00:00Z","expiresAt":"2026-11-12T18:10:00Z","heads":["ERERERERERERERERERERERERERERERERERERERERERE","IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI"]}""";
    const string Evidence = """{"v":1,"account":"test","generation":"00000000000000000000000000000000","genesis":"MzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzM","by":"11111111111111111111111111111111","add":"IiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiI","purpose":"capSign","key":"aYvqY9xEo0RmP_FCmuoQhC3ye2uZHvJYZrLGwCzcxb4","versions":[1,2],"frontier":"-vUDxokA3Tm9p5nxPJjy0JzSLrdy4zmILa0P5AKijTE","verifiers":"hUVK8Ya6OjMAZtHfwU9E_leoe26xAOmvIA5F8fgwWTw","target":"44444444444444444444444444444444","request":"0f8fad5b-d9cb-469f-a165-70867728950e","consent":"CBiML1bZBIcKoXQSwFr2UlFZy2VePhdY5x4ePmhOyAY","nonce":"VVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVVU","issuedAt":"2026-11-12T18:00:00Z","expiresAt":"2026-11-12T18:10:00Z"}""";
    static readonly TestKey Person = TestKey.Named("2366 throwaway capSign"), Host = TestKey.Named("2366 throwaway hostSign");
    static readonly TestKey ConsentKey = TestKey.Named("2366 throwaway person sign");
    static byte[] Repeat(byte b) => Enumerable.Repeat(b, 32).ToArray();
    static string B(byte[] b) => Codec.Base64Url(b);
    static Instant At(string time) => Instant.Parse("2026-11-12T" + time + "Z");
    static PersonCommand Consent(string body = Command) => PersonCommand.Parse(Framing.Utf8(PersonCommand.Canonical(Framing.Utf8(body), ConsentKey.Sign(PersonCommand.Signed(Framing.Utf8(body))), null)));
    static VerifierSupportMember[] Members => [new(new('1', 32), Repeat(0x22), "phone", Person.Public), new(new('4', 32), Repeat(0x11), "host", Host.Public)];
    static View Roster => new("test", new('0', 32), Repeat(0x33), [Repeat(0x11), Repeat(0x22)], Members);
    static VerifierSupportContext Context => new(new('4', 32), Consent(), Repeat(0x55), At("18:00:00"), At("18:10:00"), At("18:05:00"));
    sealed record View(string Account, string Generation, byte[] Genesis, IReadOnlyList<byte[]> Values, IReadOnlyList<VerifierSupportMember> Members) : IVerifierSupportRoster
    {
        public IReadOnlyList<byte[]> Heads => Members.Any(m => m.Key is null) ? throw new InvalidOperationException("missing keys must refuse before hashing") : Values;
        public bool Covers(IReadOnlyList<byte[]> heads) => heads.Count == Values.Count && heads.Zip(Values).All(p => p.First.SequenceEqual(p.Second));
    }
    static JsonObject Statement(VerifierSupportMember member, Action<JsonObject>? change = null, string domain = Label, string suffix = "")
    {
        var body = JsonNode.Parse(Evidence)!.AsObject();
        body["by"] = member.Id; body["add"] = B(member.Add); body["purpose"] = member.Kind == "host" ? "hostSign" : "capSign";
        body["key"] = B(Framing.Sha256(member.Key!));
        body["verifiers"] = B(Framing.Sha256(Framing.F([Framing.Label("agent-bridge verifier set v1"), .. Members.Select(m => Framing.F(Convert.FromHexString(m.Id), m.Add, Framing.Label(m.Kind), Framing.Label(m.Kind == "host" ? "hostSign" : "capSign"), m.Key!))])));
        change?.Invoke(body);
        var bytes = Framing.Utf8(body.ToJsonString() + suffix);
        return new JsonObject { ["body"] = B(bytes), ["sig"] = B((member.Kind == "host" ? Host : Person).Sign(Framing.F(Framing.Label(domain), bytes))) };
    }
    static byte[] Wire(Action<JsonObject>? change = null, string domain = Label, string suffix = "") =>
        Framing.Utf8(new JsonArray(Members.Select(m => (JsonNode)Statement(m, change, domain, suffix)).ToArray()).ToJsonString());
    static string? Check(byte[] bytes, VerifierSupportContext? context = null, IVerifierSupportRoster? roster = null) =>
        VerifierSupport.Check(bytes, roster ?? Roster, context ?? Context);

    [Fact]
    public void Documented_frames_and_digests_have_exact_bytes()
    {
        Assert.Equal("faf503c68900dd39bda799f13c98f2d09cd22eb772e339882dad0fe402a28d31", Codec.Hex(VerifierSupport.Frontier(Roster.Heads)));
        var point = Convert.FromHexString("046b17d1f2e12c4247f8bce6e563a440f277037d812deb33a0f4a13945d898c2964fe342e2fe1a7f9b8ee7eb4a7c0f9e162bce33576b315ececbb6406837bf51f5");
        Assert.Equal("85454af186ba3a330066d1dfc14f44fe57a87b6eb100e9af200e45f1f830593c", Codec.Hex(VerifierSupport.Set([Members[0] with { Key = point }])));
        Assert.Equal("06018e6b6add7818124a97139227953463d53bd051e4d56d82a15c8238d2ab9b", Codec.Hex(Framing.Sha256(VerifierSupport.Message(Framing.Utf8(Evidence)))));
        Assert.Equal("08188c2f56d904870aa17412c05af6525159cb655e3e1758e71e1e3e684ec806", Codec.Hex(Framing.Sha256(PersonCommand.Signed(Framing.Utf8(Command)))));
    }

    [Fact]
    public void Complete_signed_set_verifies_exact_bytes_but_absence_partial_duplicate_and_reordered_sets_refuse()
    {
        Assert.Null(Check(Wire()));
        Assert.Null(Check(Wire(suffix: " \n")));
        foreach (var entries in new[] { Array.Empty<JsonNode>(), new JsonNode[] { Statement(Members[0]) }, new JsonNode[] { Statement(Members[1]), Statement(Members[1]) }, new JsonNode[] { Statement(Members[1]), Statement(Members[0]) } })
            Assert.Equal(Unsupported, Check(Framing.Utf8(new JsonArray(entries).ToJsonString())));
    }

    [Theory]
    [InlineData("account")] [InlineData("generation")] [InlineData("genesis")] [InlineData("by")] [InlineData("add")]
    [InlineData("purpose")] [InlineData("key")] [InlineData("frontier")] [InlineData("verifiers")] [InlineData("target")]
    [InlineData("request")] [InlineData("consent")] [InlineData("nonce")]
    public void Resigned_mismatched_bindings_refuse(string field)
    {
        Assert.Null(Check(Wire()));
        Assert.Equal(Unsupported, Check(Wire(b => b[field] = field switch {
            "account" => "other", "generation" or "by" or "target" => new string('a', 32),
            "request" => "0f8fad5b-d9cb-469f-a165-70867728950f",
            "purpose" => b["purpose"]!.GetValue<string>() == "hostSign" ? "capSign" : "hostSign", _ => B(new byte[32]) })));
    }

    [Theory]
    [InlineData("agent-bridge roster entry v1")] [InlineData("agent-bridge person command v1")]
    [InlineData("agent-bridge agent command v1")] [InlineData("agent-bridge link-attest v1")]
    [InlineData("agent-bridge host key binding v1")] [InlineData("agent-bridge verifier key binding v1")]
    public void Other_domains_and_unsigned_tampering_refuse(string domain)
    {
        Assert.Null(Check(Wire()));
        Assert.Equal(Unsupported, Check(Wire(domain: domain)));
        var wire = JsonNode.Parse(Wire())!.AsArray();
        wire[0]!["body"] = B([.. Codec.FromBase64Url(wire[0]!["body"]!.GetValue<string>(), "test"), 0x20]);
        Assert.Equal(Unsupported, Check(Framing.Utf8(wire.ToJsonString())));
        wire = JsonNode.Parse(Wire())!.AsArray();
        wire[0]!["sig"] = B(Host.Sign(Framing.F(Framing.Label(Label), Codec.FromBase64Url(wire[0]!["body"]!.GetValue<string>(), "test"))));
        Assert.Equal(Unsupported, Check(Framing.Utf8(wire.ToJsonString())));
        Assert.Equal(Unsupported, Check(Wire(), Context with { Nonce = Repeat(0x77) }));
        Assert.Equal(Unsupported, Check(Wire(), Context with { Command = Consent(Command + " ") }));
    }

    [Theory]
    [InlineData("[1]")] [InlineData("[2,1]")] [InlineData("[2,2]")] [InlineData("[0,2]")] [InlineData("[2,256]")] [InlineData("[2.0]")]
    public void Version_support_is_strict_and_per_member(string versions)
    {
        Assert.Null(Check(Wire()));
        foreach (var member in Members)
        {
            Assert.Equal(Unsupported, Check(Wire(b => { if (b["by"]!.GetValue<string>() == member.Id) { b["versions"] = JsonNode.Parse(versions); } })));
        }
    }

    [Fact]
    public void Command_heads_must_cover_the_collection_frontier_even_when_consent_matches()
    {
        Assert.Null(Check(Wire()));
        var proposed = JsonNode.Parse(Command)!.AsObject();
        proposed["heads"] = new JsonArray(JsonValue.Create(B(Repeat(0x11))));
        var command = Consent(proposed.ToJsonString());
        var wire = Wire(b => b["consent"] = B(Framing.Sha256(PersonCommand.Signed(command.BodyBytes))));
        Assert.Equal(Unsupported, Check(wire, Context with { Command = command }));
    }

    [Theory]
    [InlineData("issuedAt", "2026-11-12T18:00:01Z")]
    [InlineData("expiresAt", "2026-11-12T18:09:59Z")]
    public void Resigned_valid_times_must_match_the_retained_host_window(string field, string time)
    {
        Assert.Null(Check(Wire()));
        Assert.Equal(Unsupported, Check(Wire(b => b[field] = time)));
    }

    [Fact]
    public void Time_rules_are_conjunctive_at_exact_boundaries()
    {
        Assert.Null(Check(Wire()));
        Assert.Null(Check(Wire(), Context with { Now = At("18:10:00") }));
        Assert.Null(Check(Wire(), Context with { Now = At("17:58:00") }));
        Assert.Equal(Unsupported, Check(Wire(), Context with { Now = At("18:10:01") }));
        Assert.Equal(Unsupported, Check(Wire(), Context with { Now = At("17:57:59") }));
        Assert.Equal(Unsupported, Check(Wire(), Context with { Floor = At("18:00:00") }));
        Assert.Equal(Unsupported, Check(Wire(), Context with { HighWater = At("18:10:01") }));
        var proposed = JsonNode.Parse(Command)!.AsObject(); proposed["expiresAt"] = "2026-11-12T18:10:01Z";
        var command = Consent(proposed.ToJsonString());
        Assert.Equal(Unsupported, Check(Wire(b => { b["expiresAt"] = "2026-11-12T18:10:01Z"; b["consent"] = B(Framing.Sha256(PersonCommand.Signed(command.BodyBytes))); }), Context with { Command = command, ExpiresAt = At("18:10:01") }));
    }

    [Fact]
    public void Strict_parser_retains_codes_and_support_flags_cannot_supply_evidence()
    {
        Assert.Null(Check(Wire()));
        var extra = Statement(Members[0], b => b["supportsHostSign"] = true);
        Assert.Equal(Refusal.Unknown, Assert.Throws<RosterFormatException>(() => VerifierSupport.Parse(Framing.Utf8(extra.ToJsonString()))).Code);
        Assert.Equal(Unsupported, Check(Wire(b => b["supportsHostSign"] = true)));
        var command = JsonNode.Parse(Command)!.AsObject(); command["supportsHostSign"] = true;
        Assert.Equal(Refusal.Unknown, Assert.Throws<RosterFormatException>(() => PersonCommand.Parse(Framing.Utf8(PersonCommand.Canonical(Framing.Utf8(command.ToJsonString()), ConsentKey.Sign([1]), null)))).Code);
        foreach (var bytes in new[] { Wire(suffix: new string(' ', 900)), Framing.Utf8(new string(' ', 132097) + System.Text.Encoding.UTF8.GetString(Wire())) })
            Assert.Equal(Unsupported, Check(bytes));
    }

    [Fact]
    public void Real_person_without_enrolled_capSign_is_unsupported_before_any_hashing()
    {
        Assert.Null(Check(Wire()));
        var world = new World { AccountId = "test", GenerationId = new('0', 32) }; world.Genesis("P");
        var verifier = new RosterVerifier(world.AccountId, world.GenerationId, () => TimeSpan.Zero);
        Assert.Equal(Verdict.Accepted, Assert.Single(verifier.Offer(world.Entry("G"))).Verdict);
        var real = new RosterVerifierSupportView(verifier);
        Assert.Null(Assert.Single(real.Members).Key);
        Assert.Equal(Unsupported, Check(Wire(), roster: Roster with { Members = real.Members }));
    }
}
