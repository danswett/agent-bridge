using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class DurableIngressRolloutTests : IDisposable
{
    const string SessionId = "9eb828a2-6f9b-437b-a4b4-515e91cf91f5";
    static readonly string Node = HaContract.NodeId(SessionId);
    readonly string folder = Directory.CreateTempSubdirectory("ab-ingress-").FullName;
    readonly FakeHomeAssistant ha = new();
    readonly FakeSessions sessions = new();
    readonly FakeTimeProvider time = new(FakeSessions.T0);
    readonly List<string?> delivered = [];
    string StateFile => Path.Combine(folder, "connector.json");
    string InboxFile => StateFile + ".inbox";

    public void Dispose() => Directory.Delete(folder, true);

    HaConnector Connector(bool? enabled = null, bool persistent = true)
    {
        var config = new JsonObject
        {
            ["MachineName"] = "fixture-host",
            ["Version"] = "test",
            ["StateFile"] = persistent ? StateFile : null,
            ["FieldPinDelay"] = TimeSpan.Zero.ToString(),
            ["ReplyCommitWait"] = TimeSpan.Zero.ToString(),
        };
        if (enabled is { } value) { config["DurableReplyIngress"] = value; }
        return new HaConnector(config.Deserialize<HaConnectorOptions>()!, ha, sessions, () => null, time: time,
            answer: (_, _) => Task.FromResult(new AnswerResult(AnswerOutcome.Delivered)),
            reply: (r, _) =>
            {
                delivered.Add(r.Text);
                return Task.FromResult(new ReplyResult(AnswerOutcome.Delivered));
            });
    }

    async Task Pass(HaConnector c)
    {
        time.Advance(TimeSpan.FromSeconds(1));
        await c.TickAsync();
        await c.DeliveredAsync();
    }

    void Idle() => sessions.Set(FakeSessions.Copilot(SessionId, response: "done"));

    [Fact]
    public async Task Default_options_persist_replies_before_dispatch_and_recover_them_in_FIFO_order()
    {
        Idle();
        await using (var first = Connector())
        {
            await Pass(first);
            first.Observe(ha.SendFromCard(Node, "first", rid: "first"));
            first.Observe(ha.SendFromCard(Node, "second", rid: "second"));
            Assert.Empty(delivered);
            Assert.Equal("first", new DurableInbox(InboxFile).Peek(Node)!.Rid);
        }
        await using var restarted = Connector();
        for (var n = 0; n < 4; n++) { await Pass(restarted); }

        Assert.Equal(["first", "second"], delivered);
        Assert.Null(new DurableInbox(InboxFile).Peek(Node));
    }

    [Fact]
    public async Task Explicit_false_uses_legacy_delivery_without_reading_or_modifying_the_saved_inbox()
    {
        Idle();
        await using var c = Connector(enabled: false);
        await Pass(c);
        File.WriteAllText(InboxFile, "unreadable inbox must not block rollback");
        c.Observe(ha.SendFromCard(Node, "legacy", rid: "legacy"));
        for (var n = 0; n < 3; n++) { await Pass(c); }

        Assert.Equal("legacy", Assert.Single(delivered));
        Assert.Equal("unreadable inbox must not block rollback", File.ReadAllText(InboxFile));
        var attributes = JsonNode.Parse(ha.Retained[HaContract.Topics(Node).ActivityAttributes])!;
        Assert.Equal("delivered", attributes["reply_ack"]!["outcome"]!.GetValue<string>());
        Assert.DoesNotContain(ha.Log, p => p.Payload.Contains("\"outcome\":\"queued\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_old_host_without_a_state_file_keeps_legacy_rid_delivery()
    {
        Idle();
        await using var c = Connector(persistent: false);
        await Pass(c);
        c.Observe(ha.SendFromCard(Node, "legacy host", rid: "old-host"));
        for (var n = 0; n < 3; n++) { await Pass(c); }

        Assert.Equal("legacy host", Assert.Single(delivered));
        Assert.False(File.Exists(InboxFile));
    }

    [Fact]
    public async Task A_legacy_sender_without_a_rid_still_delivers_with_default_ingress()
    {
        Idle();
        await using var c = Connector();
        await Pass(c);
        c.Observe(ha.SendFromCard(Node, "legacy sender"));
        for (var n = 0; n < 3; n++) { await Pass(c); }

        Assert.Equal("legacy sender", Assert.Single(delivered));
        Assert.Null(new DurableInbox(InboxFile).Peek(Node));
    }
}
