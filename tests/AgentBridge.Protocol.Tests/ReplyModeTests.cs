using System.Text.Json;
using AgentBridge.Protocol;
using Xunit;

public class ReplyModeTests
{
    static ReplyRequest Request(string fields) => JsonSerializer.Deserialize<ReplyRequest>(
        $$"""{"id":"s1","request":"6f1c2a9e-4b7d-4f0a-9b8e-2d1c3e4f5a6b","text":"carry on"{{fields}}}""",
        ProtocolJson.Options)!;

    static JsonElement Wire<T>(T value) => JsonSerializer.SerializeToElement(value, ProtocolJson.Options);

    [Theory]
    [InlineData("queue")]
    [InlineData("steer")]
    public void The_requested_mode_survives_reading_and_forwarding_a_reply(string mode)
    {
        var request = Request($",\"mode\":\"{mode}\"");

        Assert.Null(Replies.Check(request));
        Assert.Equal(mode, Wire(request).GetProperty("mode").GetString());
    }

    [Theory]
    [InlineData("\"future\"")]
    [InlineData("\"Queue\"")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void An_unknown_or_wrong_type_request_mode_is_refused_never_guessed_as_steer(string mode)
    {
        Assert.ThrowsAny<JsonException>(() => Request($",\"mode\":{mode}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"mode\":null")]
    public void An_older_request_or_null_mode_is_steer(string fields)
    {
        var request = Request(fields);

        Assert.Null(Replies.Check(request));
        var json = Wire(request);
        Assert.Equal("steer", json.TryGetProperty("mode", out var mode) ? mode.GetString() : "steer");
    }

    [Theory]
    [InlineData("queue")]
    [InlineData("steer")]
    public void A_result_preserves_the_mode_actually_used_independently_of_its_outcome(string mode)
    {
        var result = Received.Read<ReplyResult>(JsonSerializer.Deserialize<JsonElement>(
            $$"""{"outcome":"unconfirmed","mode":"{{mode}}","stage":"queued"}"""));

        var json = Wire(result);
        Assert.Equal(AnswerOutcome.Unconfirmed, result.Outcome);
        Assert.Equal(mode, json.GetProperty("mode").GetString());
        Assert.Equal("queued", json.GetProperty("stage").GetString());
    }

    [Fact]
    public void An_older_hosts_result_is_steer_and_does_not_claim_the_request_was_queued()
    {
        var result = Received.Read<ReplyResult>(JsonSerializer.Deserialize<JsonElement>("""{"outcome":"delivered"}"""));
        var json = Wire(result);

        Assert.Equal("steer", json.TryGetProperty("mode", out var mode) ? mode.GetString() : "steer");
        Assert.False(json.TryGetProperty("stage", out _));
    }

    [Theory]
    [InlineData("""{"outcome":"queued","stage":"future"}""")]
    [InlineData("""{"outcome":"future","stage":"queued"}""")]
    [InlineData("""{"outcome":"unconfirmed","stage":"future"}""")]
    public void An_unrecognised_final_state_or_stage_never_becomes_a_delivered_reply(string json)
    {
        var result = Received.Read<ReplyResult>(JsonSerializer.Deserialize<JsonElement>(json));

        Assert.Equal(AnswerOutcome.Unconfirmed, result.Outcome);
    }

    [Fact]
    public void Queue_progress_is_interim_and_distinct_from_a_final_outcome()
    {
        var progress = Received.Read<DeliveryProgress>(JsonSerializer.Deserialize<JsonElement>(
            """{"stage":"queued","working":true}"""));

        Assert.Equal("queued", progress.Stage);
        Assert.True(progress.Working);
        Assert.False(Wire(progress).TryGetProperty("outcome", out _));
    }
}
