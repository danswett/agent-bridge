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
            ["Messages"] = new JsonArray(messages.Select(m => m.DeepClone()).ToArray()),
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rollback_or_loss_of_the_v2_store_holds_dispatch_instead_of_replaying_v1(bool missing)
    {
        Write(1, [Message("r")]);
        var inbox = new DurableInbox(Pathname);
        Assert.NotNull(inbox.Peek("node"));
        inbox.Start("node", "r", Now);
        if (missing) { File.Delete(Pathname); }
        else { File.Copy(Pathname + ".v1", Pathname, overwrite: true); }
        if (missing) { Assert.Throws<FileNotFoundException>(() => new DurableInbox(Pathname).Peek("node")); }
        else { Assert.Throws<InvalidDataException>(() => new DurableInbox(Pathname).Peek("node")); }
    }

    [Fact]
    public void Failed_migration_does_not_dispatch_or_replace_the_original()
    {
        Write(1, [Message("r")]);
        var original = File.ReadAllText(Pathname);
        Directory.CreateDirectory(Pathname + ".v1");
        Assert.Throws<UnauthorizedAccessException>(() => new DurableInbox(Pathname).Peek("node"));
        Assert.Equal(original, File.ReadAllText(Pathname));
    }

    [Fact]
    public void First_snapshot_survives_interruption_before_its_version_marker_is_written()
    {
        Directory.CreateDirectory(Pathname + ".v2");
        Assert.Throws<UnauthorizedAccessException>(() =>
            new DurableInbox(Pathname).Enqueue("node", "r", "sender", "private", Now));
        Assert.True(File.Exists(Pathname));
        Assert.False(File.Exists(Pathname + ".v2"));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));

        Directory.Delete(Pathname + ".v2");
        var recovered = new DurableInbox(Pathname);
        var pending = recovered.Peek("node");
        Assert.NotNull(pending);
        Assert.Equal("r", pending.Rid);
        Assert.Equal("private", pending.Payload);
        Assert.True(File.Exists(Pathname + ".v2"));
        Assert.Equal(pending, recovered.Enqueue("node", "r", "other", "retry", Now.AddMinutes(1)));
        File.Delete(Pathname);
        Assert.Throws<FileNotFoundException>(() => new DurableInbox(Pathname).Peek("node"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Migration_recovers_both_sides_of_the_atomic_move_without_losing_attempt_fences(bool moved)
    {
        var started = Message("started");
        started["StartedAt"] = Now.ToString("O");
        Write(1, [Message("queued"), started, Message("uncertain", "unconfirmed")]);
        var original = File.ReadAllText(Pathname);
        File.Copy(Pathname, Pathname + ".v1");
        started["Attempt"] = 1;
        var temporary = Pathname + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, new JsonObject { ["Version"] = 2,
            ["Messages"] = new JsonArray(Message("queued"), started, Message("uncertain", "unconfirmed")),
            ["Retired"] = new JsonArray() }.ToJsonString());
        if (moved) { File.Move(temporary, Pathname, overwrite: true); }

        var recovered = new DurableInbox(Pathname);
        Assert.Equal("queued", recovered.Peek("node")!.Rid);
        var attempt = recovered.Find("node", "started")!;
        Assert.Equal(Now, attempt.StartedAt);
        Assert.Equal(1, attempt.Attempt);
        Assert.Equal("unconfirmed", recovered.Find("node", "uncertain")!.Outcome);
        Assert.Equal(original, File.ReadAllText(Pathname + ".v1"));
        Assert.True(File.Exists(Pathname + ".v2"));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        File.Copy(Pathname + ".v1", Pathname, overwrite: true);
        Assert.Throws<InvalidDataException>(() => new DurableInbox(Pathname).Peek("node"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Capacity_refusal_history_is_bounded_per_node_and_globally_without_displacing_admitted_replies(bool total)
    {
        var limit = total ? 1000 : 100;
        var admitted = Enumerable.Range(0, limit).Select(n => Message("admitted-" + n, node: "node" + n / 100)).ToList();
        Write(2, admitted);
        var node = total ? "overflow0" : "node0";
        var refused = new DurableInbox(Pathname).Enqueue(node, "seed", "sender", "never admitted", Now);
        var history = Enumerable.Range(0, limit + 2).Select(n => JsonSerializer.SerializeToNode(
            refused with { Node = total ? "overflow" + n / 100 : node, Rid = "refused-" + n })!.AsObject());
        Write(2, admitted.Concat(history));

        var inbox = new DurableInbox(Pathname);
        var newest = inbox.Enqueue(node, "newest", "sender", "never admitted either", Now);
        Assert.Equal("refused", newest.Outcome);
        var saved = JsonNode.Parse(File.ReadAllText(Pathname))!["Messages"]!.AsArray();
        Assert.Equal(limit, saved.Count(m => m!["Outcome"]?.GetValue<string>() == "refused"));
        Assert.Equal(limit * 2, saved.Count);
        inbox = new DurableInbox(Pathname);
        Assert.Null(inbox.Find(node, "refused-0"));
        Assert.Equal(newest, inbox.Enqueue(node, "newest", "other", "retry", Now.AddMinutes(1), out var duplicate));
        Assert.True(duplicate);
        var payloads = saved.ToDictionary(m => (m!["Node"]!.GetValue<string>(), m["Rid"]!.GetValue<string>()),
            m => m!["Payload"]!.GetValue<string>());
        foreach (var m in admitted)
        {
            Assert.Equal("private", payloads[(m["Node"]!.GetValue<string>(), m["Rid"]!.GetValue<string>())]);
        }
    }

    [Fact]
    public void Capacity_refusals_expire_after_a_day_without_renewal_or_expiring_admitted_fences()
    {
        var started = Message("started", node: "protected");
        started["StartedAt"] = Now.ToString("O");
        started["Attempt"] = 1;
        var cleaned = Message("cleaned", "delivered", "protected");
        cleaned["ConsumedTokens"] = JsonSerializer.SerializeToNode(new[] { new InboxCleanupFence("token", 1, 1, "host", "process") });
        Write(2, Enumerable.Range(0, 100).Select(n => Message("pending-" + n, node: "full")).Concat(
            [Message("final", "refused", "protected"), Message("uncertain", "unconfirmed", "protected"), started, cleaned]));
        var inbox = new DurableInbox(Pathname);
        inbox.ResolveAndFence("retired", InboxResolution.IncarnationEnded, Now);
        var refused = inbox.Enqueue("full", "overflow", "sender", "private", Now);
        inbox = new DurableInbox(Pathname);
        Assert.Equal(refused, inbox.Enqueue("full", "overflow", "other", "retry", Now.AddDays(1).AddTicks(-1), out var duplicate));
        Assert.True(duplicate);
        inbox.Enqueue("other", "trigger", "sender", "new", Now.AddDays(1));
        inbox = new DurableInbox(Pathname);
        Assert.Null(inbox.Find("full", "overflow"));
        foreach (var rid in new[] { "final", "uncertain", "started", "cleaned" }) { Assert.NotNull(inbox.Find("protected", rid)); }
        Assert.Equal("refused", inbox.Enqueue("retired", "new", "sender", "blocked", Now.AddDays(1)).Outcome);
        var again = inbox.Enqueue("full", "overflow", "sender", "retry", Now.AddDays(1), out duplicate);
        Assert.False(duplicate);
        Assert.Equal(Now.AddDays(1), again.QueuedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Capacity_refusal_tags_cannot_make_admitted_attempts_or_uncertainty_prunable(bool uncertain)
    {
        var message = Message("r", uncertain ? "unconfirmed" : "refused");
        message["CapacityRefused"] = true;
        if (!uncertain) { message["Attempt"] = 1; }
        Write(2, [message]);
        var original = File.ReadAllText(Pathname);
        Assert.Throws<InvalidDataException>(() => new DurableInbox(Pathname).Peek("node"));
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
        var refused = inbox.Enqueue(total ? "new-node" : "node0", "overflow", "s", "new", Now.AddYears(1));
        Assert.Equal("refused", refused.Outcome);
        Assert.Empty(refused.Payload);
        Assert.NotNull(refused.DeliveredAt);
        inbox.Forget("node0", "r0");
        for (var n = 0; n < 3; n++) { inbox.ForgetExcept(new HashSet<string>()); }
        Assert.NotNull(new DurableInbox(Pathname).Find("node0", "r0"));
        Assert.Equal(refused, new DurableInbox(Pathname).Find(refused.Node, "overflow"));
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

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Consumed_tokens_are_one_shot_until_explicit_resolution_permanently_fences_the_node(int resolution)
    {
        var inbox = new DurableInbox(Pathname);
        inbox.Enqueue("node", "r", "sender", "private", Now);
        inbox.Start("node", "r", Now, "host-1", "process-1");
        var fence = new InboxCleanupFence("token", 7, 1, "host-1", "process-1");
        Assert.Throws<InvalidOperationException>(() => inbox.ConsumeCleanup("node", "r", fence with { Attempt = 2 }));
        Assert.Throws<InvalidOperationException>(() => inbox.ConsumeCleanup("node", "r", fence with { HostRequest = "other" }));
        Assert.Throws<InvalidOperationException>(() => inbox.ConsumeCleanup("node", "r", fence with { Incarnation = "other" }));
        Assert.True(inbox.ConsumeCleanup("node", "r", fence));
        inbox.Deliver("node", "r", Now, "unconfirmed");
        inbox.Forget("node");
        inbox = new DurableInbox(Pathname);
        Assert.False(inbox.ConsumeCleanup("node", "r", fence));
        Assert.Equal(7, inbox.Find("node", "r")!.ConsumedTokens![0].Revision);
        inbox.ResolveAndFence("node", (InboxResolution)resolution, Now);
        inbox.Enqueue("other", "r", "s", "private", Now);
        inbox.Forget("other");
        inbox.ForgetExcept(new HashSet<string>());
        inbox = new DurableInbox(Pathname);
        Assert.Null(inbox.Find("node", "r"));
        Assert.False(inbox.ConsumeCleanup("node", "r", fence));
        Assert.Equal("refused", inbox.Enqueue("node", "r", "s", "retry", Now).Outcome);
        Assert.Equal("refused", inbox.Enqueue("node", "new", "s", "new identity", Now).Outcome);
        Assert.Null(inbox.Peek("node"));
        Assert.Empty(JsonNode.Parse(File.ReadAllText(Pathname))!["Messages"]!.AsArray());
        Assert.Single(JsonNode.Parse(File.ReadAllText(Pathname))!["Retired"]!.AsArray());
    }

    [Fact]
    public async Task Concurrent_instances_can_consume_a_cleanup_token_only_once()
    {
        var inbox = new DurableInbox(Pathname);
        inbox.Enqueue("node", "r", "sender", "private", Now);
        inbox.Start("node", "r", Now, "host-1", "process-1");
        var fence = new InboxCleanupFence("token", 7, 1, "host-1", "process-1");
        var consumed = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            new DurableInbox(Pathname).ConsumeCleanup("node", "r", fence))));
        Assert.Single(consumed, c => c);
        Assert.Single(new DurableInbox(Pathname).Find("node", "r")!.ConsumedTokens!);
    }

    [Fact]
    public void Malformed_receipt_identity_cannot_silently_discard_its_fence()
    {
        var m = Message("r", "delivered");
        m["ConsumedTokens"] = JsonNode.Parse("""[{"Token":"t","Revision":1,"Attempt":1,"Incarnation":"process-1"}]""");
        Write(2, [m]);
        var original = File.ReadAllText(Pathname);
        Assert.Throws<InvalidDataException>(() => new DurableInbox(Pathname).Peek("node"));
        Assert.Equal(original, File.ReadAllText(Pathname));
    }
}
