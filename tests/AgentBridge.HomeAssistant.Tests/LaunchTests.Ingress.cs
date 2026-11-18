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
    public async Task Overlapping_shared_slot_payloads_and_presses_preserve_both_requests(bool interleaved)
    {
        await using var c = await Ready();
        await SnapshotPayload(c, "ingress-first", "first words");
        if (interleaved) { c.Observe(ha.Press(Entity("button", "new_session"), AgentUser)); }
        await SnapshotPayload(c, "ingress-second", "second words");
        if (!interleaved) { c.Observe(ha.Press(Entity("button", "new_session"), AgentUser)); }
        c.Observe(ha.Press(Entity("button", "new_session"), AgentUser));

        await DrainLaunches(c);

        Assert.Equal(["first words", "second words"], launched.Select(r => r.Prompt));
        Assert.NotEqual(launched[0].Request, launched[1].Request);
        Assert.Contains(Entity("sensor", "new_prompt_payload"), c.WatchedEntities());
    }

    [Fact]
    public async Task A_payload_overwritten_by_the_previous_requests_clear_is_preserved()
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
    public async Task A_repeated_ingress_id_launches_once_even_after_the_card_fallback_window()
    {
        await using var c = await Ready();
        await SnapshotPayload(c, "ingress-repeat", "original words");
        c.Observe(ha.Press(Entity("button", "new_session"), AgentUser));
        await SnapshotPayload(c, "ingress-repeat", "changed words");
        c.Observe(ha.Press(Entity("button", "new_session"), AgentUser));
        await DrainLaunches(c);
        ha.Now = ha.Now.AddSeconds(11);
        await SnapshotPayload(c, "ingress-repeat", "must not replay");
        c.Observe(ha.Press(Entity("button", "new_session"), AgentUser));
        await DrainLaunches(c);

        Assert.Equal("original words", Assert.Single(launched).Prompt);
    }

    [Fact]
    public async Task A_snapshot_without_its_own_press_waits_and_keeps_its_settings()
    {
        await using var c = await Ready();
        ha.Choose(Entity("select", "new_permissions"), HaContract.PermissionAllowAll);
        Payload(new JsonObject
        {
            ["request"] = "ingress-waiting", ["text"] = "own words",
            ["settings"] = new JsonObject { ["permissions"] = HaContract.PermissionAsk },
        });
        var entity = Entity("sensor", "new_prompt_payload");
        c.Observe(new HaTrigger(entity, (await ha.GetStateAsync(entity))!));
        await DrainLaunches(c);
        Assert.Empty(launched);
        c.Observe(ha.Press(Entity("button", "new_session"), AgentUser));
        await DrainLaunches(c);

        Assert.Equal(LaunchProtocol.Ask, Assert.Single(launched).Permissions);
    }
}
