using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using Xunit;

public sealed partial class LaunchTests
{
    string LaunchStateFile => Path.Combine(folder, "ha-connector.json");

    void ForgetPublishedOutcomes() => ha.Seed(HaContract.LaunchOutcomeTopic(Slug),
        HaContract.Serialize(new JsonObject { ["recent"] = new JsonArray() }));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_ids_are_durable_before_handoff_and_after_completion_when_HA_loses_its_history(bool interrupted)
    {
        var handed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<LaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        answer = _ =>
        {
            handed.TrySetResult(File.ReadAllText(LaunchStateFile));
            return release.Task;
        };
        ha.PublishFailsFor = (topic, _) => topic == HaContract.LaunchOutcomeTopic(Slug);
        string checkpoint;
        await using (var c = await Ready())
        {
            Payload(new JsonObject { ["request"] = "durable-request", ["text"] = "only once" });
            c.Observe(ha.Press(Entity("button", "new_session"), AgentUser));
            await Pass(c);
            checkpoint = await handed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            release.SetResult(new LaunchResult(LaunchOutcome.Started));
            await c.DeliveredAsync();
            await Pass(c);
            if (!interrupted) { checkpoint = File.ReadAllText(LaunchStateFile); }
        }
        File.WriteAllText(LaunchStateFile, checkpoint);
        ha.PublishFailsFor = null;
        ForgetPublishedOutcomes();
        answer = null;
        await using var restarted = await Ready();
        Payload(new JsonObject { ["request"] = "durable-request", ["text"] = "must not replay" });
        await Launch(restarted, AgentUser);

        Assert.Equal("only once", Assert.Single(launched).Prompt);
        Assert.Equal(interrupted ? "unconfirmed" : "started", Outcome()["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task All_outcomes_survive_fifteen_minutes_and_restart_and_pruning_does_not_forget_request_ids()
    {
        DateTimeOffset firstOutcomeAt;
        await using (var c = await Ready())
        {
            ha.Now = time.GetUtcNow();
            for (var i = 0; i < 6; i++)
            {
                Payload(new JsonObject { ["request"] = $"history-request-{i}" });
                await Launch(c, AgentUser);
            }
            Assert.Equal(6, JsonNode.Parse(ha.Retained[HaContract.LaunchOutcomeTopic(Slug)])!["recent"]!.AsArray().Count);
            firstOutcomeAt = DateTimeOffset.Parse(Outcome(5)["at"]!.GetValue<string>());
        }
        time.Advance(TimeSpan.FromMinutes(14));
        ForgetPublishedOutcomes();
        await using (var restarted = await Ready())
        {
            Assert.Equal(6, JsonNode.Parse(ha.Retained[HaContract.LaunchOutcomeTopic(Slug)])!["recent"]!.AsArray().Count);
            time.SetUtcNow(firstOutcomeAt.AddMinutes(15));
            ha.Now = time.GetUtcNow();
            Payload(new JsonObject { ["request"] = "history-at-boundary" });
            restarted.Observe(ha.Press(Entity("button", "new_session"), AgentUser));
            await Pass(restarted, 0);
            await restarted.DeliveredAsync();
            await Pass(restarted, 0);
            Assert.Equal(7, JsonNode.Parse(ha.Retained[HaContract.LaunchOutcomeTopic(Slug)])!["recent"]!.AsArray().Count);
            time.Advance(TimeSpan.FromMinutes(2));
            ha.Now = time.GetUtcNow();
            Payload(new JsonObject { ["request"] = "history-after-window" });
            await Launch(restarted, AgentUser);
            Assert.Equal(5, JsonNode.Parse(ha.Retained[HaContract.LaunchOutcomeTopic(Slug)])!["recent"]!.AsArray().Count);
            Assert.DoesNotContain("history-request-0", ha.Retained[HaContract.LaunchOutcomeTopic(Slug)]);
        }
        ForgetPublishedOutcomes();
        await using var next = await Ready();
        Payload(new JsonObject { ["request"] = "history-request-0" });
        await Launch(next, AgentUser);
        Assert.Equal(8, launched.Count);
    }

    [Fact]
    public async Task A_request_waits_for_a_durable_write_and_the_same_press_can_retry_after_recovery()
    {
        await using var c = await Ready();
        var blocked = LaunchStateFile + ".tmp";
        Directory.CreateDirectory(blocked);
        Payload(new JsonObject { ["request"] = "durable-write-retry" });
        c.Observe(ha.Press(Entity("button", "new_session"), AgentUser));
        await Pass(c);
        Assert.Empty(launched);
        Assert.Contains("durable-write-retry", ha.Retained[HaContract.PromptPayloadTopic(Slug)]);
        Assert.Contains(log, entry => entry.Contains("could not write", StringComparison.Ordinal));

        Directory.Delete(blocked);
        await DrainLaunches(c);
        Assert.Single(launched);
        Assert.Equal("started", Outcome()["outcome"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{\"launch\":false}")]
    public async Task Unreadable_durable_launch_state_blocks_launching_and_is_not_overwritten(string contents)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(LaunchStateFile, contents);
        await using var c = await Ready();
        Payload(new JsonObject { ["request"] = "durable-corrupt-state" });
        await Launch(c, AgentUser);

        Assert.Empty(launched);
        Assert.Equal(contents, File.ReadAllText(LaunchStateFile));
        Assert.Contains(log, entry => entry.Contains("could not read", StringComparison.Ordinal));
    }
}
