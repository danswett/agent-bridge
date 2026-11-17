using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using Xunit;

public sealed partial class LaunchTests
{
    [Fact]
    public async Task More_than_fifty_requests_keep_their_outcomes_for_fifteen_minutes_and_dedupe_after_restart()
    {
        var c = await Ready();
        for (var i = 0; i < 52; i++)
        {
            Payload(new JsonObject { ["request"] = $"queue-{i:D4}" });
            await Launch(c, AgentUser);
        }
        await Pass(c, 15 * 60);
        Assert.Equal(52, JsonNode.Parse(ha.Retained[HaContract.LaunchOutcomeTopic(Slug)])!["recent"]!.AsArray().Count);
        await c.DisposeAsync();
        // HA's retained history is not the durable dedupe authority.
        ha.Seed(HaContract.LaunchOutcomeTopic(Slug), "{\"recent\":[]}");
        await using var restarted = await Ready();
        Payload(new JsonObject { ["request"] = "queue-0000" });
        await Launch(restarted, AgentUser);
        Assert.Equal(52, launched.Count);
    }

    [Fact]
    public async Task Pending_payloads_are_not_persisted_and_restart_reports_them_lost_without_replaying()
    {
        var release = new TaskCompletionSource<LaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        answer = _ => release.Task;
        var c = await Ready();
        Payload(new JsonObject { ["request"] = "queue-first" });
        c.Observe(ha.Press(Entity("button", "new_session"), AgentUser));
        await Pass(c);
        Payload(new JsonObject { ["request"] = "queue-second", ["text"] = "second prompt" });
        c.Observe(ha.Press(Entity("button", "new_session"), AgentUser));
        await Pass(c);
        Assert.Equal("queued", Outcome()["outcome"]!.GetValue<string>());
        var journal = File.ReadAllText(Path.Combine(folder, "ha-connector.json.launch"));
        release.SetResult(new LaunchResult(LaunchOutcome.Started));
        await c.DeliveredAsync();
        await c.DisposeAsync();
        Assert.DoesNotContain("second prompt", journal, StringComparison.Ordinal);
        Assert.DoesNotContain("Attachments", journal, StringComparison.Ordinal);
        answer = null;
        await using var restarted = await Ready();
        await restarted.DeliveredAsync();
        await Pass(restarted);
        Assert.Single(launched);
        Assert.Equal("lost-restart", Outcome()["outcome"]!.GetValue<string>());
        Assert.Equal("unconfirmed", Outcome(1)["outcome"]!.GetValue<string>());
        Payload(new JsonObject { ["request"] = "queue-first" });
        await Launch(restarted, AgentUser);
        Assert.Single(launched);
    }

    [Fact]
    public async Task A_launch_is_not_dispatched_when_its_durable_journal_cannot_be_written()
    {
        await using var c = await Ready();
        Directory.CreateDirectory(Path.Combine(folder, "ha-connector.json.launch.tmp"));
        Payload(new JsonObject { ["request"] = "queue-no-disk" });
        await Launch(c, AgentUser);
        Assert.Empty(launched);
        Assert.Contains(log, m => m.Contains("launch", StringComparison.OrdinalIgnoreCase));
        Directory.Delete(Path.Combine(folder, "ha-connector.json.launch.tmp"));
        await Pass(c);
        await c.DeliveredAsync();
        await Pass(c);
        Assert.Single(launched);
    }
}
