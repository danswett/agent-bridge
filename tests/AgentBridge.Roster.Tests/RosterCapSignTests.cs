using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using AgentBridge.Roster;
using static RosterFixture;

namespace AgentBridge.Roster.Tests;

// The v3 person descriptor with capSign (docs/protocol/devices.md "Version and descriptor",
// "Enrollment transcript"; collision rows L800-802). Rows L792-799 are host evidence collection.
public sealed class RosterCapSignTests
{
    static readonly TestKey Cap = TestKey.Named("app capSign");
    const string At = "2026-11-20T21:00:00Z";

    static JsonObject AppV3(TestKey? cap = null, string presence = Presence.Key) => PersonV3(App, AppSign, cap ?? Cap, "app", "windows", "Desk", presence);

    static JsonObject Genesis(JsonObject device, int v = 3)
    {
        var b = Body("genesis", 0, null, App, At);
        (b["v"], b["device"], b["approval"]) = (v, device, new JsonObject { ["method"] = "genesis" });
        return b;
    }

    static string? Refusal(JsonNode body)
    {
        try { EntryBody.Parse(Bytes(body)); return null; }
        catch (RosterFormatException e) { return e.Code; }
    }

    [Theory]
    [InlineData(Presence.Key)]
    [InlineData(Presence.WebAuthn)]
    [InlineData(Presence.None)]
    public void A_v3_person_genesis_round_trips_through_the_builder_for_every_presence(string presence)
    {
        var webauthn = presence == Presence.WebAuthn ? ParsedCredential() : null;
        var device = PersonDescriptorV3.Write(Account, Generation, App.Public, AppSign.Public, presence, webauthn, Cap.Public, Cap.Sign, "app", "windows", "Desk");
        var body = PersonDescriptorV3.Genesis(Account, Generation, At, device);
        var parsed = EntryBody.Parse(body);

        Assert.Equal(Bytes(AppV3(presence: presence)), device);
        Assert.Equal(Bytes(Genesis(AppV3(presence: presence))), body);
        Assert.Equal(3, parsed.Version);
        Assert.Equal(Cap.Public, parsed.Device!.CapSign);
        Assert.True(parsed.HasCapSign);
        Assert.Equal(device, EntryBody.DeviceBytes(body));
    }

    [Fact]
    public void A_v3_genesis_is_accepted_without_the_host_sign_switch_and_a_foreign_proof_is_never_built()
    {
        var body = Bytes(Genesis(AppV3()));
        var v = new RosterVerifier(Account, Generation, () => TimeSpan.Zero);

        Assert.Equal(Verdict.Accepted, Assert.Single(v.Offer([Entry.Parse(Encoding.UTF8.GetBytes(Entry.Canonical(body, AppSign.Sign(Entry.SignedBy(body)))))])).Verdict);
        Assert.False(v.AcceptHostSign);
        var e = Assert.Throws<RosterFormatException>(() => PersonDescriptorV3.Write(Account, Generation, App.Public, AppSign.Public, Presence.Key, null, Cap.Public, AppSign.Sign, "app", "windows", "Desk"));
        Assert.Equal(AgentBridge.Roster.Refusal.CapProof, e.Code);
    }

    // Row L791: P lacks enrolled capSign. It keeps its v1 parse and gains no evidence key.
    [Fact]
    public void A_person_without_an_enrolled_capSign_still_parses_as_v1_and_is_never_upgraded()
    {
        var parsed = EntryBody.Parse(Bytes(Genesis(Person(App, AppSign, "app", "windows", "Desk", Presence.Key), v: 1)));

        Assert.Equal(1, parsed.Version);
        Assert.Null(parsed.Device!.CapSign);
        Assert.False(parsed.HasCapSign);
    }

    // Row L800, parse half: the empty name proves `device` comes before platform/name.
    [Theory]
    [InlineData("dh")]
    [InlineData("sign")]
    [InlineData("webauthn")]
    public void A_capSign_equal_to_its_own_dh_sign_or_webauthn_key_is_refused_at_parse_as_device(string key)
    {
        var device = AppV3(key switch { "dh" => App, "sign" => AppSign, _ => PwaCredential }, key == "webauthn" ? Presence.WebAuthn : Presence.Key);
        device["name"] = "";

        Assert.Equal(AgentBridge.Roster.Refusal.Device, Refusal(Genesis(device)));
    }

    static readonly BigInteger N = BigInteger.Parse("0ffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632551", System.Globalization.NumberStyles.HexNumber);

    public static TheoryData<string, string, string> Defects => new()
    {
        { "capSign", Codec.Base64Url(Cap.Public[..64]), "length" },
        { "capSign", Codec.Base64Url([.. Cap.Public[..64], (byte)(Cap.Public[64] ^ 1)]), "point" },
        { "capSign", Codec.Base64Url(Cap.Public) + "=", "base64" },
        { "capSignAlg", "ES256", "value" },
        { "capSignProof", Codec.Base64Url([0x30, 0x00]), "der" },
        { "capSignProof", Codec.Base64Url(P256.Der(BigInteger.One, N - 1)), "high-s" },
        { "capSignProof", Codec.Base64Url(AppSign.Sign(CapSignKey.Binding(Account, Generation, App.Public))), "cap-proof" },
        { "capSignProof", Codec.Base64Url(Cap.Sign(CapSignKey.Binding(Account, "0" + Generation[1..], App.Public))), "cap-proof" },
        { "capSignProof", Codec.Base64Url(Cap.Sign(HostKey.Binding(Account, Generation, App.Public))), "cap-proof" },
    };

    [Theory]
    [MemberData(nameof(Defects))]
    public void A_wrong_length_off_curve_or_otherwise_bad_capSign_field_is_refused_with_its_first_code(string field, string value, string code)
    {
        var device = AppV3();
        device[field] = value;

        Assert.Equal(code, Refusal(Genesis(device)));
    }

    [Fact]
    public void A_v3_descriptor_is_only_a_persons_genesis_or_add_with_all_three_capSign_fields()
    {
        var (host, remove, spelled, partial) = (Genesis(AppV3()), Genesis(AppV3()), Genesis(AppV3()), AppV3());
        host["device"] = HostDevice(Host1, "linux", "h");
        remove["op"] = "remove";
        spelled["v"] = JsonNode.Parse("3.0");
        partial.Remove("capSignProof");

        Assert.All(new[] { host, remove, spelled, Genesis(AppV3(), v: 2) }, b => Assert.Equal("version", Refusal(b)));
        Assert.Equal("missing-member", Refusal(Genesis(partial)));
        Assert.Equal("unknown-member", Refusal(Genesis(AppV3(), v: 1)));
    }

    [Fact]
    public void A_v3_add_needs_a_code_or_qr_transcript_and_a_bad_proof_is_refused_first()
    {
        var w = new World();
        w.Genesis();
        var add = JsonNode.Parse(w.AddV3("+P", "G", "P", "G", Cap).Body)!;
        add["approval"] = new JsonObject { ["method"] = "grandfather" };

        Assert.Equal("approval", Refusal(add));
        add["device"]!["capSignProof"] = Codec.Base64Url(AppSign.Sign([1]));
        Assert.Equal("cap-proof", Refusal(add));
    }

    static World Base()
    {
        var w = new World();
        w.Genesis();
        w.Add("+A", "G", "A", "G");
        return w;
    }

    static string? Last(World w, bool hostSign = false) =>
        w.Feed(new RosterVerifier(Account, Generation, () => w.Now) { AcceptHostSign = hostSign }, [.. w.Labels])[^1].Reason;

    // Row L800, history half: an add colliding with an ancestor, either one first.
    [Fact]
    public void An_add_whose_capSign_collides_with_an_ancestors_sign_is_refused_as_host_key_in_either_order()
    {
        var (first, second) = (Base(), Base());
        first.AddV3("+P", "G", "P", "+A", World.Keys("Q").Sign);
        first.Add("+Q", "G", "Q", "+P");
        second.Add("+Q", "G", "Q", "+A");
        second.AddV3("+P", "G", "P", "+Q", World.Keys("Q").Sign);

        Assert.All(new[] { first, second }, w => Assert.Equal(Invalid.HostKey, Last(w)));
    }

    // Row L800, merge half.
    [Fact]
    public void A_merge_joining_branches_whose_adds_collide_on_capSign_is_refused_as_host_key()
    {
        var w = Base();
        w.AddV3("+P", "G", "P", "+A", World.Keys("Q").Sign);
        w.Add("+Q", "G", "Q", "+A");
        w.Merge("m", "G", ["+P", "+Q"]);

        Assert.Equal(Invalid.HostKey, Last(w));
    }

    // Row L801: independent branches, each delivery order; both adds stay accepted.
    [Theory]
    [InlineData("sign")]
    [InlineData("dh")]
    [InlineData("hostSign")]
    [InlineData("capSign")]
    public void Sibling_adds_colliding_capSign_with_another_key_confer_no_authority_in_either_order(string key)
    {
        foreach (var order in new[] { new[] { "+P", "+Q" }, ["+Q", "+P"] })
        {
            var w = Base();
            var shared = key switch { "dh" => World.Keys("Q").Dh, "capSign" => TestKey.Named("shared capSign"), _ => World.Keys("Q").Sign };
            w.AddV3("+P", "G", "P", "+A", shared);
            if (key == "hostSign") { w.AddHostV2("+Q", "G", "Q", "+A"); }
            else if (key == "capSign") { w.AddV3("+Q", "G", "Q", "+A", shared); }
            else { w.Add("+Q", "G", "Q", "+A"); }
            w.Rename("nP", "P", "+P", "+P", "Laptop");
            w.Rename("nA", "G", "+A", "+A", "Desk");
            var v = new RosterVerifier(Account, Generation, () => w.Now) { AcceptHostSign = true };

            Assert.All(w.Feed(v, ["G", "+A", .. order]), o => Assert.Equal(Verdict.Accepted, o.Verdict));
            Assert.Equal(Invalid.HostKey, w.Feed(v, "nP").Single().Reason);
            Assert.Equal(Verdict.Accepted, w.Feed(v, "nA").Single().Verdict);
            Assert.All(new[] { "P", "Q" }, d => Assert.True(v.SignerKeyCollides(v.HistoryState(w["+" + d].Hash).ActiveAdd(w.Id(d))!.Value)));
        }
    }

    // Row L802: a later hostSign equal to an earlier capSign, and the reverse arrival.
    [Fact]
    public void A_later_hostSign_equal_to_an_earlier_capSign_and_the_reverse_are_both_host_key()
    {
        var (first, second) = (Base(), Base());
        first.AddV3("+P", "G", "P", "+A", World.Keys("H").Sign);
        first.AddHostV2("+H", "G", "H", "+P");
        second.AddHostV2("+H", "G", "H", "+A");
        second.AddV3("+P", "G", "P", "+H", World.Keys("H").Sign);

        Assert.All(new[] { first, second }, w => Assert.Equal(Invalid.HostKey, Last(w, hostSign: true)));
    }

    static readonly byte[] H = Encoding.UTF8.GetBytes("channel one handshake hash");
    static readonly byte[] NA = Encoding.UTF8.GetBytes("approver nonce 0123456789abcdef!");
    static readonly byte[] NN = Encoding.UTF8.GetBytes("joiner nonce 0123456789abcdefgh!");

    [Fact]
    public void T3_binds_identity_and_exact_descriptor_bytes_is_never_the_v1_transcript_and_gates_the_joiners_accept()
    {
        var w = new World();
        w.Genesis();
        var body = JsonNode.Parse(w.AddV3("+P", "G", "P", "G", Cap).Body)!;
        var d = Bytes(body["device"]!);
        var t3 = Enrollment.CapSignTranscript(Account, Generation, H, NA, NN, d);
        var v1 = Enrollment.Transcript(Account, H, NA, NN);
        // `reencode` adds one space inside the device object: same meaning, different bytes.
        Entry Signed(byte[] transcript, bool reencode = false)
        {
            body["approval"]!["transcript"] = Codec.Base64Url(transcript);
            var text = body.ToJsonString(Raw);
            var bytes = Encoding.UTF8.GetBytes(reencode ? text.Replace("\"kind\":", "\"kind\": ", StringComparison.Ordinal) : text);
            return Entry.Parse(Encoding.UTF8.GetBytes(Entry.Canonical(bytes, World.Keys("G").Sign.Sign(Entry.SignedBy(bytes)))));
        }

        Assert.Equal(Framing.Sha256(Framing.F(Framing.Label("agent-bridge enroll capSign v1"), Framing.Utf8(Account), Framing.Utf8(Generation), H, NA, NN, d)), t3);
        Assert.NotEqual(v1, t3);
        Assert.NotEqual(t3, Enrollment.CapSignTranscript(Account, Generation, H, NA, NN, [.. d, (byte)' ']));
        Assert.NotEqual(t3, Enrollment.CapSignTranscript(Account, "0" + Generation[1..], H, NA, NN, d));
        Assert.True(Enrollment.AcceptsCapSignAdd(Signed(t3), w.Id("G"), d, t3));
        Assert.False(Enrollment.AcceptsCapSignAdd(Signed(v1), w.Id("G"), d, t3));
        Assert.False(Enrollment.AcceptsCapSignAdd(Signed(t3, reencode: true), w.Id("G"), d, t3));
        Assert.False(Enrollment.AcceptsCapSignAdd(Signed(t3), w.Id("A"), d, t3));
    }

    [Fact]
    public void A_v1_joiner_never_accepts_a_v3_add_of_its_own_keys()
    {
        var w = new World();
        w.Genesis();
        w.Add("+P", "G", "P", "G");
        w.AddV3("+P3", "G", "P", "G", Cap);
        var own = w.Entry("+P").Body.Device!;

        Assert.False(EnrollmentJoiner.SameDevice(w.Entry("+P3").Body.Device!, own));
        Assert.True(EnrollmentJoiner.SameDevice(own, own));
        Assert.False(w.Entry("+P3").Body.HasCapSign);
    }
}
