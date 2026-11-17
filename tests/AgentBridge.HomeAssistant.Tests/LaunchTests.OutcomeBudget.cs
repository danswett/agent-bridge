using System.Text;
using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using Xunit;

public sealed partial class LaunchTests
{
    [Fact]
    public async Task Published_recent_outcomes_fit_the_byte_budget_without_losing_dedupe()
    {
        answer = _ => Task.FromResult(new LaunchResult(LaunchOutcome.Failed, new string('x', 4000)));
        await using var c = await Ready();
        for (var i = 0; i < 25; i++)
        {
            Payload(new JsonObject { ["request"] = $"review-bytes-{i:D3}" });
            await Launch(c, AgentUser);
        }
        var json = ha.Retained[HaContract.LaunchOutcomeTopic(Slug)];
        Assert.True(Encoding.UTF8.GetByteCount(json) <= 12 * 1024);
        var recent = JsonNode.Parse(json)!["recent"]!.AsArray();
        Assert.Equal("review-bytes-024", recent[0]!["request"]!.GetValue<string>());
        Assert.Equal(4000, recent[0]!["note"]!.GetValue<string>().Length);
        Assert.Equal(25, recent.Count);
        Assert.Null(recent[^1]!["note"]);
        Assert.DoesNotContain(new string('x', 4000), File.ReadAllText(Path.Combine(folder, "ha-connector.json.launch")));
        Payload(new JsonObject { ["request"] = "review-bytes-000" });
        await Launch(c, AgentUser);
        Assert.Equal(25, launched.Count);
    }

    [Fact]
    public async Task Old_tombstones_dedupe_without_being_published_as_recent()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "ha-connector.json.launch"), HaContract.Serialize(new JsonObject
        {
            ["tombstones"] = new JsonArray(new JsonObject
            {
                ["request"] = "budget-old", ["outcome"] = "refused",
                ["at"] = time.GetUtcNow().AddMinutes(-16).ToString("o"),
            }),
        }));
        await using var c = await Ready();
        Assert.Empty(JsonNode.Parse(ha.Retained[HaContract.LaunchOutcomeTopic(Slug)])!["recent"]!.AsArray());
        Payload(new JsonObject { ["request"] = "budget-old" });
        await Launch(c, AgentUser);
        Assert.Empty(launched);
    }
}
