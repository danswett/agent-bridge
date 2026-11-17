using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using Xunit;

public sealed class DurableInboxV2Tests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ab-inbox-v2-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-17T11:30:00-08:00");
    string Pathname => Path.Combine(folder, "inbox.json");
    public DurableInboxV2Tests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, recursive: true);
    void Write(int version, IEnumerable<JsonObject> messages) =>
        File.WriteAllText(Pathname, new JsonObject { ["Version"] = version,
            ["Messages"] = new JsonArray(messages.Select(m => (JsonNode)m).ToArray()),
            ["Retired"] = version == 2 ? new JsonArray() : null }.ToJsonString());
    static JsonObject Message(string rid, string? outcome = null, string node = "node") =>
        JsonSerializer.SerializeToNode(new InboxMessage(node, rid, "sender", outcome is null ? "private" : "", Now,
            outcome is null ? null : Now, Outcome: outcome))!.AsObject();

    [Fact]
    public void Migration_preserves_order_fences_started_attempts_and_keeps_the_protected_original()
    {
        var started = Message("started");
        started["StartedAt"] = Now.ToString("O");
        Write(1, [Message("queued"), started, Message("uncertain", "unconfirmed")]);
        var original = File.ReadAllText(Pathname);
        var inbox = new DurableInbox(Pathname);
        Assert.Equal("queued", inbox.Peek("node")!.Rid);
        Assert.Equal(2, JsonNode.Parse(File.ReadAllText(Pathname))!["Version"]!.GetValue<int>());
        Assert.Equal(original, File.ReadAllText(Pathname + ".v1"));
        inbox.Forget("node");
        Assert.Equal("started", new DurableInbox(Pathname).Peek("node")!.Rid);
        Assert.NotNull(inbox.Find("node", "uncertain"));
        Assert.Equal(1, JsonNode.Parse(File.ReadAllText(Pathname))!["Messages"]![0]!["Attempt"]!.GetValue<int>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Unknown_versions_never_fall_back_to_a_legacy_snapshot(int version)
    {
        Write(version, [Message("r")]);
        var original = File.ReadAllText(Pathname);
        File.WriteAllText(Pathname + ".v1", """{"Version":1,"Messages":[]}""");
        Assert.Throws<InvalidDataException>(() => new DurableInbox(Pathname).Peek("node"));
        Assert.Equal(original, File.ReadAllText(Pathname));
    }

    [Fact]
    public void Failed_migration_does_not_dispatch_or_replace_the_original()
    {
        Write(1, [Message("r")]);
        var original = File.ReadAllText(Pathname);
        Directory.CreateDirectory(Pathname + ".v1");
        Assert.ThrowsAny<IOException>(() => new DurableInbox(Pathname).Peek("node"));
        Assert.Equal(original, File.ReadAllText(Pathname));
    }

    [Theory]
    [InlineData("unconfirmed", false)]
    [InlineData("unconfirmed", true)]
    [InlineData("refused", false)]
    [InlineData("refused", true)]
    [InlineData("token", false)]
    [InlineData("token", true)]
    public void Pins_survive_restart_forget_and_both_capacity_limits(string kind, bool total)
    {
        var messages = Enumerable.Range(0, total ? 1000 : 100).Select(n =>
        {
            var m = Message("r" + n, kind == "token" ? "delivered" : kind, total ? "node" + n / 100 : "node0");
            if (kind == "token") { m["ConsumedTokens"] = JsonNode.Parse($$"""[{"Token":"token-{{n}}","Revision":1,"Attempt":1,"HostRequest":"host-{{n}}","Incarnation":"process-1"}]"""); }
            return m;
        });
        Write(2, messages);
        var inbox = new DurableInbox(Pathname);
        Assert.Throws<InvalidOperationException>(() => inbox.Enqueue(total ? "new-node" : "node0", "overflow", "s", "new", Now.AddYears(1)));
        inbox.Forget("node0", "r0");
        inbox.ForgetExcept(new HashSet<string>());
        Assert.NotNull(new DurableInbox(Pathname).Find("node0", "r0"));
    }

    [Fact]
    public void Final_refusal_is_a_durable_tombstone_not_permission_to_resend()
    {
        var inbox = new DurableInbox(Pathname);
        inbox.Enqueue("node", "r", "sender", "private", Now);
        var refused = inbox.Deliver("node", "r", Now, "refused");
        inbox.Forget("node");
        Assert.Equal(refused, new DurableInbox(Pathname).Enqueue("node", "r", "other", "retry", Now.AddYears(1)));
        Assert.Equal("refused", refused.Outcome);
        Assert.Empty(refused.Payload);
        Assert.Null(inbox.Peek("node"));
    }
}
