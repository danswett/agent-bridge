using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Fleet;
using AgentBridge.Protocol;
using AgentBridge.Relay;
using Xunit;

public sealed class FleetProducerTests
{
    const string Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    static async Task<JsonElement> Publish(ISessionSource source, TimeProvider time)
    {
        JsonElement record = default;
        await new FleetPublisher(source, "fixture", time: time).PublishAsync((r, _) => { record = r.Clone(); return Task.CompletedTask; });
        Assert.All(record.GetProperty("sessions").EnumerateArray(), s => Assert.True(s.TryGetProperty("observedAt", out _), "producer observation absent"));
        Assert.Null(RosterFields.Check(record, "fixture", out _, out _, out _));
        return Assert.Single(record.GetProperty("sessions").EnumerateArray());
    }

    [Fact]
    public async Task Transcript_facts_reach_publication_without_private_text_or_sender_inference()
    {
        using var f = new Fixture();
        f.Processes.Run(AgentKind.Copilot, 101);
        File.WriteAllLines(f.CopilotSession(Id, 101), [Copilot.User("private-question", 1), Copilot.Assistant("private-reply", 2)]);
        await using var source = f.Source();
        await source.RefreshAsync();
        var row = await Publish(source, f.Time);
        Assert.Equal(0, row.GetProperty("openAsks").GetInt32());
        Assert.Equal("none", row.GetProperty("attention").GetProperty("kind").GetString());
        Assert.Equal(Fixture.T0.AddSeconds(2), row.GetProperty("lastReplyAt").GetDateTimeOffset());
        Assert.Equal(f.Time.GetUtcNow(), row.GetProperty("observedAt").GetDateTimeOffset());
        Assert.False(row.GetProperty("stale").GetBoolean());
        Assert.DoesNotContain("private-", row.GetRawText());
        Assert.False(row.TryGetProperty("senderVerified", out _));
        Assert.False(row.TryGetProperty("provenance", out _));
    }

    [Fact]
    public async Task Pending_permission_count_is_not_a_boolean_and_staleness_uses_host_observation()
    {
        using var f = new Fixture();
        f.Processes.Run(AgentKind.Copilot, 101);
        File.WriteAllLines(f.CopilotSession(Id, 101), [
            Copilot.Permission("first", "tool-1", Copilot.Prompts["commands"], 1),
            Copilot.Permission("second", "tool-2", Copilot.Prompts["commands"], 2)]);
        await using var source = f.Source();
        await source.RefreshAsync();
        var row = await Publish(source, f.Time);
        Assert.Equal(2, row.GetProperty("openAsks").GetInt32());
        Assert.Equal("permission", row.GetProperty("attention").GetProperty("kind").GetString());
        Assert.False(row.TryGetProperty("lastReplyAt", out _));
        f.Time.Advance(TimeSpan.FromSeconds(61));
        Assert.True((await Publish(source, f.Time)).GetProperty("stale").GetBoolean());
    }
}
