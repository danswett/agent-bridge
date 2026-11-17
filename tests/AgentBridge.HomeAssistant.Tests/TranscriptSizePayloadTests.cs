using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using Xunit;

public class TranscriptSizePayloadTests
{
    [Fact]
    public void Status_attributes_publish_size_provenance_and_unavailability_without_a_zero_fallback()
    {
        var session = FakeSessions.Copilot("88888888-8888-4888-8888-888888888888") with
        {
            TranscriptBytes = 123,
            TranscriptBytesProvenance = new(FakeSessions.T0, "file-size", "synthetic-host"),
        };
        var attributes = HaPayloads.StatusAttributes(session, "example", "host", FakeSessions.T0);
        Assert.Equal(123L, (long?)attributes["transcript_bytes"]);
        Assert.Equal(session.Id, (string?)attributes["session_id"]);
        Assert.Equal("file-size", (string?)attributes["transcript_bytes_provenance"]!["source"]);
        Assert.Equal("synthetic-host", (string?)attributes["transcript_bytes_provenance"]!["host_id"]);
        Assert.NotNull(attributes["transcript_bytes_provenance"]!["observed_at"]);
        Assert.False(attributes.ContainsKey("transcript_bytes_unavailable"));

        var missing = HaPayloads.StatusAttributes(session with
        {
            TranscriptBytes = null,
            TranscriptBytesUnavailable = "access-denied",
        }, "example", "host", FakeSessions.T0);
        Assert.False(missing.ContainsKey("transcript_bytes"));
        Assert.Equal("access-denied", (string?)missing["transcript_bytes_unavailable"]);
    }
}
