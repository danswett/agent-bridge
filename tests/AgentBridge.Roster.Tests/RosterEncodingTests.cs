using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentBridge.Roster;
using static RosterFixture;

public sealed class RosterEncodingTests
{
    static Entry Parse(string wire) => Entry.Parse(Encoding.UTF8.GetBytes(wire));

    static string Code(Action parse) => Assert.Throws<RosterFormatException>(parse).Code;

    static Signed WithName(string name)
    {
        var add = RosterVectorTests.Chain()[1];
        var body = JsonNode.Parse(add.Body)!.AsObject();
        body["device"]!["name"] = name;
        return add with { Body = Bytes(body) };
    }

    [Fact]
    public void A_name_of_64_scalars_is_accepted_at_256_bytes_and_one_scalar_more_is_refused()
    {
        Assert.Equal(new string('a', 64), Parse(WithName(new string('a', 64)).Wire).Body.Device!.Name);
        var widest = string.Concat(Enumerable.Repeat("😀", 64));
        Assert.Equal(256, Encoding.UTF8.GetByteCount(widest));
        Assert.Equal(widest, Parse(WithName(widest).Wire).Body.Device!.Name);
        Assert.Equal(Refusal.Name, Code(() => Parse(WithName(new string('a', 65)).Wire)));
    }

    [Fact]
    public void A_hidden_character_that_is_not_a_control_is_kept_for_the_client_to_reveal()
    {
        // Zero-width space and a bidi override: never refused, since Unicode versions differ.
        Assert.Equal("Office\u200B\u202Ebrowser", Parse(WithName("Office\u200B\u202Ebrowser").Wire).Body.Device!.Name);
    }

    [Fact]
    public void Two_copies_of_one_body_with_different_signatures_are_the_same_entry()
    {
        var add = RosterVectorTests.Chain()[1];
        using var platform = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var other = P256.Normalize(platform.SignData(Entry.SignedBy(add.Body), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        var a = Parse(add.Wire);
        var b = Parse(Entry.Canonical(add.Body, other));
        Assert.Equal(a.Hash, b.Hash);
        Assert.NotEqual(a.Sig, b.Sig);
    }

    [Fact]
    public void A_raw_signature_from_webcrypto_becomes_minimal_low_s_der_that_verifies()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var message = Encoding.UTF8.GetBytes("message");
        for (var i = 0; i < 8; i++)
        {
            var der = P256.FromRaw(key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            P256.CheckRosterSignature(der, "sig");
            var point = key.ExportParameters(false).Q;
            Assert.True(P256.Verify([0x04, .. point.X!, .. point.Y!], message, der));
            Assert.Equal(Refusal.HighS, Code(() => P256.CheckRosterSignature(P256.Flip(der), "sig")));
            Assert.Equal(der, P256.Normalize(P256.Flip(der)));
        }
    }

    [Fact]
    public void A_typed_code_drops_spaces_and_matches_only_its_six_ascii_digits()
    {
        Assert.True(Enrollment.Matches("042917", "042 917"));
        Assert.True(Enrollment.Matches("042917", " 0 4 2 9 1 7 "));
        Assert.False(Enrollment.Matches("042917", "42917"));
        Assert.False(Enrollment.Matches("042917", "042918"));
        Assert.False(Enrollment.Matches("042917", "０４２９１７"));
        Assert.False(Enrollment.Matches("042917", "042\t917"));
        Assert.Equal("042 917", Enrollment.Shown("042917"));
    }

    [Fact]
    public void A_code_keeps_its_leading_zeros()
    {
        var exchange = RosterVectorTests.Exchanges()[0];
        var code = Enrollment.Code1(Enrollment.Transcript(exchange.Account, exchange.H, exchange.NA, exchange.NN));
        Assert.Equal(6, code.Length);
        Assert.Equal('0', code[0]);
    }

    [Theory]
    [InlineData("2026-11-03T17:25:10Z", true)]
    [InlineData("2024-02-29T00:00:00Z", true)]
    [InlineData("2026-11-03T17:25:10.123456789Z", true)]
    [InlineData("2025-02-29T00:00:00Z", false)]
    [InlineData("2026-11-03T24:00:00Z", false)]
    [InlineData("0000-01-01T00:00:00Z", false)]
    [InlineData("2026-11-03 17:25:10Z", false)]
    [InlineData("+2026-11-03T17:25:10Z", false)]
    public void At_is_utc_to_the_second_on_a_real_date(string at, bool valid) => Assert.Equal(valid, Times.IsUtc(at));

    [Fact]
    public void The_fingerprint_is_the_first_64_bits_of_the_genesis_hash_in_groups_of_four()
    {
        byte[] hash = [0x7d, 0x0f, 0x3a, 0x2e, 0x1c, 0x4b, 0x4d, 0x5e, .. new byte[24]];
        Assert.Equal("7d0f 3a2e 1c4b 4d5e", Ids.Fingerprint(hash));
    }

    [Fact]
    public void An_entry_is_sized_over_its_fixed_content_not_its_wrapper()
    {
        var add = RosterVectorTests.Chain()[1];
        var spaced = add.Wire.Replace(",", " ,   ", StringComparison.Ordinal);
        Assert.Equal(Parse(add.Wire).Size, Parse(spaced).Size);
        Assert.Equal(add.Body.Length + add.Sig.Length, Parse(add.Wire).Size);
        Assert.Equal(add.Wire, Parse(spaced).Canonical());
    }

    [Fact]
    public void A_presence_challenge_is_bound_to_the_role_and_the_body()
    {
        var body = RosterVectorTests.Chain()[2].Body;
        Assert.NotEqual(Presence.Challenge(Presence.RoleBy, body), Presence.Challenge(Presence.RoleCosigner, body));
        Assert.NotEqual(Presence.Challenge(Presence.RoleBy, body), Presence.Challenge(Presence.RoleBy, [.. body, 0x20]));
    }
}
