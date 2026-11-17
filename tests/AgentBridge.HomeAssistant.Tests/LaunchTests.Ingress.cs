using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using Xunit;

public sealed partial class LaunchTests
{
    async Task SnapshotPayload(HaConnector c, string id, string text)
    {
        Payload(new JsonObject { ["request"] = id, ["text"] = text });
        var entity = Entity("sensor", "new_prompt_payload");
        c.Observe(new HaTrigger(entity, (await ha.GetStateAsync(entity))!));
    }

    async Task DrainLaunches(HaConnector c)
    {
        await Pass(c);
        await c.DeliveredAsync();
        await Pass(c);
        await c.DeliveredAsync();
        await Pass(c);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Overlapping_payloads_and_presses_preserve_both_requests_before_the_next_tick(bool interleaved)
    {
        await using var c = await Ready();
        await SnapshotPayload(c, "ingress-first", "first words");
        if (interleaved) { c.Observe(ha.Press(Entity("button", "new_session"), AgentUser)); }
        await SnapshotPayload(c, "ingress-second", "second words");
        if (!interleaved) { c.Observe(ha.Press(Entity("button", "new_session"), AgentUser)); }
        c.Observe(ha.Press(Entity("button", "new_session"), AgentUser));
        await DrainLaunches(c);
        Assert.Equal(["first words", "second words"], launched.Select(r => r.Prompt));
        Assert.Equal(["ingress-second", "ingress-first"],
            JsonNode.Parse(ha.Retained[HaContract.LaunchOutcomeTopic(Slug)])!["recent"]!.AsArray().Select(o => o!["request"]!.GetValue<string>()));
        Assert.Contains(Entity("sensor", "new_prompt_payload"), c.WatchedEntities());
    }

    [Fact]
    public async Task A_payload_overwritten_by_the_previous_requests_clear_is_still_launched()
    {
        await using var c = await Ready();
        await SnapshotPayload(c, "ingress-before", "before clear");
        var replaced = false;
        ha.BeforePublish = topic =>
        {
            if (topic != HaContract.PromptPayloadTopic(Slug) || replaced) { return; }
            replaced = true;
            SnapshotPayload(c, "ingress-during", "during clear").GetAwaiter().GetResult();
            c.Observe(ha.Press(Entity("button", "new_session"), AgentUser));
        };
        c.Observe(ha.Press(Entity("button", "new_session"), AgentUser));
        await DrainLaunches(c);
        Assert.Equal(["before clear", "during clear"], launched.Select(r => r.Prompt));
    }

    [Fact]
    public async Task A_captured_payload_lost_at_restart_is_reported_without_replaying_its_id()
    {
        var c = await Ready();
        await SnapshotPayload(c, "ingress-saved", "saved words");
        await Pass(c);
        await c.DisposeAsync();
        await using var restarted = await Ready();
        Payload(new JsonObject { ["request"] = "ingress-saved", ["text"] = "must not replay" });
        await Launch(restarted, AgentUser);
        Assert.Empty(launched);
        Assert.Equal("lost-restart", Outcome()["outcome"]!.GetValue<string>());
    }

    [Fact]
    public async Task Legacy_clients_see_starting_while_a_request_waits_in_the_queue()
    {
        var release = new TaskCompletionSource<LaunchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        answer = _ => release.Task;
        await using var c = await Ready();
        c.Observe(ha.Press(Entity("button", "new_session")));
        await Pass(c);
        await SnapshotPayload(c, "ingress-legacy", "queued prompt");
        c.Observe(ha.Press(Entity("button", "new_session"), AgentUser));
        await Pass(c);
        release.SetResult(new LaunchResult(LaunchOutcome.Started));
        Assert.Equal("starting", Outcome()["outcome"]!.GetValue<string>());
        Assert.Equal("queued", Outcome()["phase"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_agents_press_without_a_request_never_uses_the_cards_permissions_or_prompt()
    {
        await using var c = await Ready();
        ha.Choose(Entity("select", "new_permissions"), HaContract.PermissionAllowAll);
        ha.Type(Entity("text", "new_prompt"), "not the agent's prompt");
        await Launch(c, AgentUser);
        Assert.Empty(launched);
        Assert.Contains("request", Note, StringComparison.OrdinalIgnoreCase);
    }
}
