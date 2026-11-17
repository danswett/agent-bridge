using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using Xunit;

public sealed partial class LaunchTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_refusal_whose_reporting_failed_is_retried_without_another_press(bool journal)
    {
        await using var c = await Ready();
        if (journal) { Directory.CreateDirectory(Path.Combine(folder, "ha-connector.json.launch.tmp")); }
        ha.PublishFailsFor = (topic, _) => !journal && topic == HaContract.LaunchOutcomeTopic(Slug);
        Payload(new JsonObject { ["request"] = "review-retry", ["settings"] = new JsonObject { ["agent"] = "missing" } });
        await Launch(c, AgentUser);
        if (journal) { Directory.Delete(Path.Combine(folder, "ha-connector.json.launch.tmp")); }
        ha.PublishFailsFor = null;
        await Pass(c);
        Assert.Empty(launched);
        Assert.Contains("review-retry", ha.Retained[HaContract.LaunchOutcomeTopic(Slug)], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_daemon_pause_before_dispatch_keeps_the_queued_request()
    {
        await using var c = await Ready();
        ha.PublishFailsFor = (topic, body) =>
        {
            if (topic == HaContract.LaunchOutcomeTopic(Slug) && body.Contains("\"outcome\":\"starting\"", StringComparison.Ordinal)
                && !body.Contains("\"phase\"", StringComparison.Ordinal)) { daemon = "agent-ha-bridge is running"; }
            return false;
        };
        Payload(new JsonObject { ["request"] = "review-pause" });
        await Launch(c, AgentUser);
        Assert.Empty(launched);
        ha.PublishFailsFor = null;
        daemon = null;
        await Pass(c);
        await Pass(c, 2);
        await Pass(c, 6);
        await c.DeliveredAsync();
        await Pass(c);
        Assert.Single(launched);
    }

    [Fact]
    public async Task A_memory_only_connector_can_report_a_launch_outcome()
    {
        await using var c = Connector(durable: false);
        await Pass(c);
        await Pass(c, 2);
        await Pass(c, 6);
        Payload(new JsonObject { ["request"] = "review-memory" });
        await Launch(c, AgentUser);
        Assert.Single(launched);
        Assert.Equal("started", Outcome()["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_person_can_use_the_card_despite_a_leftover_handled_payload()
    {
        await using var c = await Ready();
        Payload(new JsonObject { ["request"] = "review-card" });
        await Launch(c, AgentUser);
        Payload(new JsonObject { ["request"] = "review-card", ["text"] = "old request" });
        ha.Type(Entity("text", "new_prompt"), "card words");
        await Launch(c);
        Assert.Equal(2, launched.Count);
        Assert.Equal("card words", launched[1].Prompt);
        Assert.Equal("{}", ha.Retained[HaContract.PromptPayloadTopic(Slug)]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_refused_id_is_never_replayed_from_the_card_when_its_payload_becomes_stale(bool clearFails)
    {
        await using var c = await Ready();
        ha.PublishFailsFor = (topic, body) => clearFails && topic == HaContract.PromptPayloadTopic(Slug) && body == "{}";
        Payload(new JsonObject { ["request"] = "review-refused", ["settings"] = new JsonObject { ["agent"] = "unknown agent" } });
        await Launch(c, AgentUser);
        if (!clearFails) { Assert.Equal("{}", ha.Retained[HaContract.PromptPayloadTopic(Slug)]); }
        ha.Now = ha.Now.AddSeconds(61);
        await Launch(c, AgentUser);
        Assert.Empty(launched);
        Assert.Equal("refused", Outcome()["outcome"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(0, 10000)]
    [InlineData(25, 0)]
    public async Task Dedupe_history_is_bounded_by_count_and_age(int hours, int expected)
    {
        ha.Seed(HaContract.LaunchOutcomeTopic(Slug), HaContract.Serialize(new JsonObject
        {
            ["recent"] = new JsonArray([.. Enumerable.Range(0, 10001).Select(i => (JsonNode)new JsonObject
            {
                ["request"] = $"review-{i:D5}", ["outcome"] = "refused",
                ["at"] = time.GetUtcNow().AddHours(-hours).AddSeconds(-i).ToString("o"),
            })]),
        }));
        await using var c = await Ready();
        Assert.Equal(expected, JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "ha-connector.json.launch")))!["tombstones"]!.AsArray().Count);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(ha.Retained[HaContract.LaunchOutcomeTopic(Slug)]) <= 12 * 1024);
    }

    [Fact]
    public async Task A_full_pending_queue_refuses_another_admission()
    {
        var release = new TaskCompletionSource<LaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        answer = _ => release.Task;
        await using var c = await Ready();
        c.Observe(ha.Press(Entity("button", "new_session")));
        await Pass(c);
        for (var i = 0; i < 129; i++)
        {
            Payload(new JsonObject { ["request"] = $"review-pending-{i:D3}" });
            c.Observe(ha.Press(Entity("button", "new_session"), AgentUser));
            await Pass(c);
        }
        release.SetResult(new LaunchResult(LaunchOutcome.Started));
        Assert.Equal("refused", Outcome()["outcome"]!.GetValue<string>());
        Assert.Contains("full", Outcome()["note"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_corrupt_journal_is_quarantined_and_launches_fail_closed()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "ha-connector.json.launch"), "{\"recent\":[");
        await using var c = await Ready();
        Payload(new JsonObject { ["request"] = "review-corrupt" });
        await Launch(c, AgentUser);
        Assert.Empty(launched);
        Assert.True(File.Exists(Path.Combine(folder, "ha-connector.json.launch.corrupt")));
        Assert.Contains(log, m => m.Contains("quarantin", StringComparison.OrdinalIgnoreCase));
    }
}
