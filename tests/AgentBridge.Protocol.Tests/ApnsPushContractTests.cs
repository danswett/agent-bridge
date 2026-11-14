using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;
using Xunit;

public sealed class ApnsPushContractTests
{
    static JsonElement Materialize(string json)
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = key.ExportParameters(false).Q;
        using var doc = JsonDocument.Parse(json
            .Replace("$syntheticToken", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), StringComparison.Ordinal)
            .Replace("$keyId", Attachments.Base64Url(RandomNumberGenerator.GetBytes(32)), StringComparison.Ordinal)
            .Replace("$publicKey", Attachments.Base64Url([4, .. q.X!, .. q.Y!]), StringComparison.Ordinal)
            .Replace("$auth", Attachments.Base64Url(RandomNumberGenerator.GetBytes(16)), StringComparison.Ordinal));
        return doc.RootElement.Clone();
    }

    static JsonElement Native() => Materialize("""{"token":"$syntheticToken","target":"ios-sandbox","keyId":"$keyId","keys":{"p256dh":"$publicKey","auth":"$auth"}}""");

    public static IEnumerable<object[]> Vectors()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "examples", "push-apns.json")));
        foreach (var v in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            yield return [v.GetProperty("name").GetString()!, v.GetProperty("op").GetString()!, v.GetProperty("accept").GetBoolean(), v.GetProperty("data").GetRawText()];
        }
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Native_operation_vectors_are_read_or_refused(string name, string op, bool accept, string json)
    {
        var data = Materialize(json);
        if (!accept) { Assert.Throws<JsonException>(() => ApnsPushRequest.Read(op, data)); return; }
        var request = ApnsPushRequest.Read(op, data);
        Assert.Equal(op, request.Operation);
        Assert.Equal(data.TryGetProperty("target", out var target) ? target.GetString() : null, request.Target);
        if (op != ApnsPushRequest.Subscribe) { Assert.Null(request.Token); Assert.Null(request.Keys); return; }
        Assert.True(data.GetProperty("token").GetString() == request.Token, name);
        Assert.True(data.GetProperty("keys").GetProperty("p256dh").GetString() == request.Keys?.P256dh, name);
        Assert.DoesNotContain(request.Token!, request.ToString()!);
        Assert.Throws<JsonException>(() => data.Deserialize<PushSubscribeRequest>(ProtocolJson.Options));
    }

    [Theory]
    [InlineData("token", 1024, true)]
    [InlineData("token", 1026, false)]
    [InlineData("token", 63, false)]
    [InlineData("token", 2, true)]
    [InlineData("target", 64, true)]
    [InlineData("target", 65, false)]
    [InlineData("device", 64, true)]
    [InlineData("device", 65, false)]
    public void Native_destination_bounds_are_checked_before_enrollment(string field, int length, bool accept)
    {
        var data = JsonNode.Parse(Native().GetRawText())!.AsObject();
        data[field] = new string('a', length);
        var json = JsonSerializer.SerializeToElement(data);
        if (accept) { Assert.NotNull(ApnsPushRequest.Read(ApnsPushRequest.Subscribe, json)); }
        else { Assert.Throws<JsonException>(() => ApnsPushRequest.Read(ApnsPushRequest.Subscribe, json)); }
    }

    [Theory]
    [InlineData("token", "https://example.invalid")]
    [InlineData("keyId", "not-a-key-identifier")]
    [InlineData("accountId", "caller-account")]
    [InlineData("endpoint", "https://example.invalid")]
    [InlineData("keys", null)]
    public void A_native_subscription_refuses_wrong_destinations_and_authority_fields(string field, string? value)
    {
        var data = JsonNode.Parse(Native().GetRawText())!.AsObject();
        data[field] = value;
        Assert.Throws<JsonException>(() => ApnsPushRequest.Read(ApnsPushRequest.Subscribe, JsonSerializer.SerializeToElement(data)));
    }

    [Fact]
    public void A_native_subscription_refuses_off_curve_keys_and_noncanonical_auth()
    {
        var data = JsonNode.Parse(Native().GetRawText())!.AsObject();
        data["keys"]!["p256dh"] = Attachments.Base64Url([4, .. new byte[64]]);
        Assert.Throws<JsonException>(() => ApnsPushRequest.Read(ApnsPushRequest.Subscribe, JsonSerializer.SerializeToElement(data)));
        data = JsonNode.Parse(Native().GetRawText())!.AsObject();
        data["keys"]!["auth"] = new string('A', 21) + "B";
        Assert.Throws<JsonException>(() => ApnsPushRequest.Read(ApnsPushRequest.Subscribe, JsonSerializer.SerializeToElement(data)));
    }

    [Theory]
    [InlineData("duplicate-key")]
    [InlineData("oversized-json")]
    [InlineData("invalid-text")]
    public void Strict_envelopes_and_nested_keys_cannot_be_read_past(string fault)
    {
        var json = Native().GetRawText();
        json = fault switch
        {
            "duplicate-key" => json.Replace("\"keys\":{", "\"keys\":{\"auth\":\"AA\",", StringComparison.Ordinal),
            "oversized-json" => json.Insert(1, new string(' ', 4096)),
            _ => json.Insert(1, "\"device\":\"\\ud800\","),
        };
        using var doc = JsonDocument.Parse(json);
        Assert.Throws<JsonException>(() => ApnsPushRequest.Read(ApnsPushRequest.Subscribe, doc.RootElement));
    }

    [Fact]
    public void Native_tokens_never_enter_result_shapes_log_formatting_or_parser_errors()
    {
        var data = Native();
        var token = data.GetProperty("token").GetString()!;
        Assert.DoesNotContain(token, ApnsPushRequest.Read(ApnsPushRequest.Subscribe, data).ToString()!);
        object[] results = [new PushSubscribeResult(true, 1), new PushUnsubscribeResult(true), new PushListResult([])];
        Assert.All(results, result => Assert.DoesNotContain(token, JsonSerializer.Serialize(result, ProtocolJson.Options)));
        var invalid = JsonNode.Parse(data.GetRawText())!.AsObject();
        invalid["target"] = token;
        var error = Assert.Throws<JsonException>(() => ApnsPushRequest.Read(ApnsPushRequest.Subscribe, JsonSerializer.SerializeToElement(invalid)));
        Assert.DoesNotContain(token, error.ToString());
    }
}
