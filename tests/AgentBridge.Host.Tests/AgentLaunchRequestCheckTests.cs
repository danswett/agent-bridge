using System.Text;
using System.Text.Json;
using AgentBridge.Host;
using Xunit;

public sealed class AgentLaunchRequestCheckTests
{
    static JsonElement Request(string fields = "") =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["request"] = Guid.NewGuid().ToString(),
            ["permissions"] = "ask",
            ["prompt"] = "Build the assigned slice",
        }).WithFields(fields);

    [Fact]
    public void A_plain_ask_only_request_is_allowed() =>
        Assert.Null(AgentLaunchRequestCheck.Check(Request(), []));

    [Theory]
    [InlineData("\"permissions\":\"allowAll\"")]
    [InlineData("\"permissions\":\"ask-only\"")]
    [InlineData("\"permissions\":\"ASK\"")]
    [InlineData("\"permissions\":\"\"")]
    [InlineData("\"permissions\":null")]
    public void Anything_other_than_explicit_ask_permissions_is_refused(string fields) =>
        Assert.Contains("permissions", AgentLaunchRequestCheck.Check(Request(fields), []));

    [Fact]
    public void Absent_permissions_cannot_inherit_an_unsafe_machine_default() =>
        Assert.Contains("permissions", AgentLaunchRequestCheck.Check(
            JsonSerializer.SerializeToElement(new { request = "one" }), []));

    [Theory]
    [InlineData("\"resume\":\"session\"", "resume")]
    [InlineData("\"transfer\":{\"session\":\"session\"}", "transfer")]
    [InlineData("\"attachments\":[{\"id\":\"attachment\"}]", "attachments")]
    [InlineData("\"extraArgs\":[\"--allow-all\"]", "extraArgs")]
    [InlineData("\"agentScope\":\"read\"", "agentScope")]
    [InlineData("\"agentScope\":null", "agentScope")]
    public void Agent_controlled_unsafe_fields_are_refused(string fields, string reason) =>
        Assert.Contains(reason, AgentLaunchRequestCheck.Check(Request(fields), []));

    [Theory]
    [InlineData("\"profile\":\"yolo\"")]
    [InlineData("\"profile\":\"allow-all\"")]
    [InlineData("\"profile\":\"allow_all\"")]
    [InlineData("\"profile\":\"ALLOW-ALL\"")]
    [InlineData("\"profile\":\"\"")]
    [InlineData("\"profile\":\" \"")]
    [InlineData("\"profile\":\"--allow-all-tools\"")]
    [InlineData("\"profile\":\"home\"")]
    [InlineData("\"profile\":null")]
    public void Agent_controlled_profiles_are_refused_without_echoing_the_value(string fields) =>
        Assert.Equal("An agent launch cannot set profile.", AgentLaunchRequestCheck.Check(Request(fields), []));

    [Fact]
    public void Configured_extra_arguments_are_refused_without_disclosing_values()
    {
        var reason = AgentLaunchRequestCheck.Check(Request(), ["--private-value"]);
        Assert.Contains("extra arguments", reason);
        Assert.DoesNotContain("--private-value", reason);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"text\"")]
    [InlineData("{\"request\":\"one\",\"permissions\":false}")]
    [InlineData("{\"request\":\"one\",\"permissions\":\"ask\",\"prompt\":42}")]
    [InlineData("{\"permissions\":\"ask\"}")]
    public void Malformed_request_shapes_have_a_readable_refusal(string json) =>
        Assert.False(string.IsNullOrWhiteSpace(AgentLaunchRequestCheck.Check(
            JsonSerializer.Deserialize<JsonElement>(json), [])));

    [Fact]
    public void Duplicate_fields_cannot_make_the_check_and_launcher_disagree()
    {
        var request = JsonSerializer.Deserialize<JsonElement>(
            "{\"request\":\"one\",\"permissions\":\"allowAll\",\"permissions\":\"ask\"}");
        Assert.Contains("duplicate", AgentLaunchRequestCheck.Check(request, []));
    }

    [Theory]
    [InlineData("x", 16384)]
    [InlineData("\u00e9", 8192)]
    public void A_prompt_of_exactly_16_KiB_is_allowed_whole(string unit, int count)
    {
        var prompt = string.Concat(Enumerable.Repeat(unit, count));
        var request = Request(JsonSerializer.Serialize(new { prompt })[1..^1]);
        Assert.Equal(16384, Encoding.UTF8.GetByteCount(prompt));
        Assert.Null(AgentLaunchRequestCheck.Check(request, []));
        Assert.Equal(prompt, request.GetProperty("prompt").GetString());
    }

    [Theory]
    [InlineData("x", 16384)]
    [InlineData("\u00e9", 8192)]
    public void A_prompt_one_byte_over_16_KiB_is_refused(string unit, int count)
    {
        var prompt = string.Concat(Enumerable.Repeat(unit, count)) + "x";
        Assert.Equal(16385, Encoding.UTF8.GetByteCount(prompt));
        Assert.Contains("16 KiB", AgentLaunchRequestCheck.Check(
            Request(JsonSerializer.Serialize(new { prompt })[1..^1]), []));
    }

    [Theory]
    [InlineData("/command")]
    [InlineData("!shell")]
    public void Existing_protocol_prompt_safety_is_preserved(string prompt) =>
        Assert.Contains("command", AgentLaunchRequestCheck.Check(
            Request(JsonSerializer.Serialize(new { prompt })[1..^1]), []));
}

file static class RequestFields
{
    public static JsonElement WithFields(this JsonElement request, string fields)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(request)!;
        using var additions = JsonDocument.Parse("{" + fields + "}");
        foreach (var field in additions.RootElement.EnumerateObject())
        {
            values[field.Name] = field.Value.Clone();
        }
        return JsonSerializer.SerializeToElement(values);
    }
}
