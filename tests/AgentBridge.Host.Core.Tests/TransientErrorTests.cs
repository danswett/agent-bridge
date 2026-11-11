using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Core.Parsing;
using Xunit;

public class TransientErrorTests
{
    // #1843: four sessions on 2026-11-10 stopped on these exact data fields.
    const string EmptyResponse = """{"errorType":"query","message":"No response was returned. Send your message again to retry."}""";

    [Fact]
    public void The_recorded_empty_response_is_eligible_for_host_retry()
    {
        using var record = JsonDocument.Parse(EmptyResponse);

        Assert.True(CopilotTurnErrors.CanRetry(record.RootElement));
        Assert.True(CopilotTurnErrors.EndsTurn(record.RootElement));
    }

    [Theory]
    [InlineData("authorization")]
    [InlineData("quota")]
    [InlineData("policy")]
    [InlineData("model_call")]
    [InlineData("hook")]
    [InlineData("execution")]
    [InlineData("network")]
    [InlineData("unknown")]
    [InlineData("")]
    public void Empty_response_words_do_not_make_other_error_types_retryable(string type)
    {
        var data = JsonNode.Parse(EmptyResponse)!;
        data["errorType"] = type;

        Assert.False(CopilotTurnErrors.CanRetry(JsonSerializer.SerializeToElement(data)));
    }

    [Theory]
    [InlineData("statusCode", "401")]
    [InlineData("statusCode", "403")]
    [InlineData("statusCode", "429")]
    [InlineData("statusCode", "\"401\"")]
    [InlineData("statusCode", "{}")]
    [InlineData("remediation", "\"sign_in\"")]
    [InlineData("remediation", "\"unknown\"")]
    [InlineData("remediation", "42")]
    public void Structured_status_or_remediation_blocks_even_the_known_empty_response(string field, string value)
    {
        var data = JsonNode.Parse(EmptyResponse)!;
        data[field] = JsonNode.Parse(value);

        Assert.False(CopilotTurnErrors.CanRetry(JsonSerializer.SerializeToElement(data)));
    }

    [Theory]
    [InlineData("session event delivery failed: session host did not acknowledge the session.background_tasks_changed event within 120s")]
    [InlineData("No response was returned. Sign in again.")]
    [InlineData("Prefix: No response was returned. Send your message again to retry.")]
    [InlineData("No response was returned. Send your message again to retry. Quota exceeded.")]
    [InlineData("fetch failed")]
    [InlineData("ETIMEDOUT")]
    public void Unknown_messages_and_the_unrecoverable_host_wedge_are_not_retryable(string message)
    {
        Assert.False(CopilotTurnErrors.CanRetry(JsonSerializer.SerializeToElement(new { errorType = "query", message })));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{"errorType":"query"}""")]
    [InlineData("""{"message":"No response was returned. Send your message again to retry."}""")]
    [InlineData("""{"errorType":3,"message":{}}""")]
    public void Missing_or_reshaped_error_fields_fail_closed(string json)
    {
        using var record = JsonDocument.Parse(json);

        Assert.False(CopilotTurnErrors.CanRetry(record.RootElement));
    }
}
