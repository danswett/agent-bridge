using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentBridge.Roster;

// docs/protocol/vectors/role-claim.json (docs/design/role-ids.md, "Slice 1 wire format"): a role
// holder claim's exact bytes and refusals. Nothing signs, verifies, publishes or reads claims yet.
public class RoleClaimVectorTests
{
    static readonly JsonObject Vectors = VectorFiles.Read("role-claim.json");
    static JsonObject Valid => Vectors["valid"]!.AsObject();
    static byte[] Utf8(JsonNode? text) => Encoding.UTF8.GetBytes(text!.GetValue<string>());
    static RoleId Role => RoleId.Parse(JsonNode.Parse(Valid["json"]!.GetValue<string>())!["roleId"]!.GetValue<string>(), "roleId");

    public static TheoryData<string> Refused() => [.. Vectors["refused"]!.AsArray().Select(v => v!["name"]!.GetValue<string>())];
    public static TheoryData<string> WrongPurposes() => [.. Vectors["wrongPurposes"]!.AsArray().Select(v => v!.GetValue<string>())];

    [Fact]
    public void A_valid_claim_carries_a_role_guid_and_a_different_session_guid()
    {
        var claim = RoleClaim.Parse(Utf8(Valid["json"]), Role);
        Assert.Equal("6f1c2a4e-3b5d-4c7e-9f80-1a2b3c4d5e6f", claim.RoleId.Value);
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e", claim.SessionId.Value);
        Assert.NotEqual(claim.RoleId.Value, claim.SessionId.Value);
    }

    [Fact]
    public void A_claim_writes_back_byte_for_byte_and_signs_its_pinned_body_under_its_purpose()
    {
        var claim = RoleClaim.Parse(Utf8(Valid["json"]), Role);
        Assert.Equal(Utf8(Valid["json"]), claim.Bytes());
        Assert.Equal(Utf8(Valid["body"]), claim.BodyBytes());
        Assert.Equal(RoleClaim.Purpose, Vectors["purpose"]!.GetValue<string>());
        Assert.Equal(Valid["signingInputSha256"]!.GetValue<string>(), Convert.ToHexStringLower(SHA256.HashData(claim.SigningInput(RoleClaim.Purpose))));
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void A_refused_vector_is_refused_with_its_code(string name)
    {
        var vector = Vectors["refused"]!.AsArray().Single(v => v!["name"]!.GetValue<string>() == name)!;
        var refusal = Assert.Throws<RosterFormatException>(() => RoleClaim.Parse(Utf8(vector["json"]), Role));
        Assert.Equal(vector["code"]!.GetValue<string>(), refusal.Code);
    }

    [Theory]
    [MemberData(nameof(WrongPurposes))]
    public void A_claim_refuses_to_frame_its_body_under_any_other_purpose(string purpose)
    {
        var claim = RoleClaim.Parse(Utf8(Valid["json"]), Role);
        Assert.Equal(Refusal.Purpose, Assert.Throws<RosterFormatException>(() => claim.SigningInput(purpose)).Code);
    }
}
