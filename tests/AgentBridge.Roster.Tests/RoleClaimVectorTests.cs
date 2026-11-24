using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AgentBridge.Roster;

// docs/protocol/vectors/role-claim.json (docs/design/role-ids.md, "Slice 1 wire format"): a role
// holder claim's exact bytes and refusals. Nothing signs, verifies, publishes or reads claims yet.
public class RoleClaimVectorTests
{
    static readonly JsonObject Vectors = VectorFiles.Read("role-claim.json");
    static JsonObject Valid => Vectors["valid"]!.AsObject();
    static string Text(JsonNode? node) => node!.GetValue<string>();
    static byte[] Utf8(JsonNode? text) => Framing.Utf8(Text(text));
    static RoleId Role => RoleId.Parse(Text(JsonNode.Parse(Text(Valid["json"]))!["roleId"]), "roleId");
    static RoleClaim Claim => RoleClaim.Parse(Utf8(Valid["json"]), Role);

    public static TheoryData<string> Refused() => [.. Vectors["refused"]!.AsArray().Select(v => Text(v!["name"]))];
    public static TheoryData<string> WrongPurposes() => [.. Vectors["wrongPurposes"]!.AsArray().Select(Text)];

    [Fact]
    public void A_valid_claim_carries_a_role_guid_and_a_different_session_guid()
    {
        Assert.Equal("6f1c2a4e-3b5d-4c7e-9f80-1a2b3c4d5e6f", Claim.RoleId.Value);
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", Claim.SessionId.Value);
    }

    [Fact]
    public void A_claim_writes_back_byte_for_byte_and_signs_its_pinned_body_under_its_purpose()
    {
        Assert.Equal(Utf8(Valid["json"]), Claim.Bytes());
        Assert.Equal(Utf8(Valid["body"]), Claim.BodyBytes());
        Assert.Equal(RoleClaim.Purpose, Text(Vectors["purpose"]));
        Assert.Equal(Text(Valid["signingInputSha256"]), Convert.ToHexStringLower(SHA256.HashData(Claim.SigningInput(RoleClaim.Purpose))));
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void A_refused_vector_is_refused_with_its_code(string name)
    {
        var vector = Vectors["refused"]!.AsArray().Single(v => Text(v!["name"]) == name)!;
        Assert.Equal(Text(vector["code"]), Assert.Throws<RosterFormatException>(() => RoleClaim.Parse(Utf8(vector["json"]), Role)).Code);
    }

    [Theory]
    [MemberData(nameof(WrongPurposes))]
    public void A_claim_refuses_to_frame_its_body_under_any_other_purpose(string purpose) =>
        Assert.Equal(Refusal.Purpose, Assert.Throws<RosterFormatException>(() => Claim.SigningInput(purpose)).Code);

    [Fact]
    public void A_host_key_binding_signature_and_a_role_binding_signature_never_verify_as_each_other()
    {
        var v = Vectors["crossPurpose"]!;
        var key = TestKey.Named(Text(v["key"]));
        var hostKey = HostKey.Binding(Text(v["account"]), Text(v["generation"]), key.Public);
        var role = Framing.F(Framing.Label(RoleClaim.BindingPurpose), Utf8(v["account"]), Utf8(v["generation"]), key.Public);
        var (hostKeySig, roleSig) = (Codec.FromBase64Url(Text(v["hostKeyBinding"]), "v"), Codec.FromBase64Url(Text(v["roleBinding"]), "v"));
        Assert.Equal((Text(v["hostKeyBinding"]), Text(v["roleBinding"])), (Codec.Base64Url(key.Sign(hostKey)), Codec.Base64Url(key.Sign(role))));
        Assert.True(P256.Verify(key.Public, hostKey, hostKeySig) && P256.Verify(key.Public, role, roleSig));
        Assert.False(P256.Verify(key.Public, role, hostKeySig) || P256.Verify(key.Public, hostKey, roleSig));
    }
}
