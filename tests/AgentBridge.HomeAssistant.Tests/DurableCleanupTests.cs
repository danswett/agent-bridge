using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using Xunit;

public sealed class DurableCleanupTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ab-cleanup-" + Guid.NewGuid().ToString("N"));
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-17T13:30:00-08:00");
    static readonly InboxCleanupFence Fence = new("token", 7, 1, "host-1", "process-1");
    string Pathname => Path.Combine(folder, "inbox.json");
    public DurableCleanupTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, recursive: true);

    DurableInbox Started()
    {
        var inbox = new DurableInbox(Pathname);
        inbox.Enqueue("node", "first", "sender", "private", Now);
        inbox.Enqueue("node", "second", "sender", "later", Now.AddSeconds(1));
        inbox.Start("node", "first", Now, Fence.HostRequest, Fence.Incarnation);
        return inbox;
    }

    [Theory]
    [InlineData("completion-before-clear")]
    [InlineData(" ")]
    public void Reserving_a_clear_cannot_also_assert_its_completion(string receipt)
    {
        var inbox = Started();
        Assert.Throws<ArgumentException>(() => inbox.ConsumeCleanup("node", "first", Fence with { Receipt = receipt }));
        Assert.Null(new DurableInbox(Pathname).Peek("node")!.ConsumedTokens);
    }

    [Fact]
    public void A_lost_clear_receipt_never_grants_a_second_mutation_after_restart()
    {
        var inbox = Started();
        Assert.True(inbox.ConsumeCleanup("node", "first", Fence));
        inbox = new DurableInbox(Pathname);
        Assert.False(inbox.ConsumeCleanup("node", "first", Fence));
        Assert.Null(Assert.Single(inbox.Peek("node")!.ConsumedTokens!).Receipt);
        Assert.NotNull(inbox.Peek("node")!.StartedAt);
    }

    [Fact]
    public void A_different_token_cannot_repeat_a_clear_for_the_same_attempt()
    {
        var inbox = Started();
        Assert.True(inbox.ConsumeCleanup("node", "first", Fence));
        Assert.False(new DurableInbox(Pathname).ConsumeCleanup("node", "first",
            Fence with { Token = "replacement-token", Revision = 8 }));
        Assert.Single(inbox.Peek("node")!.ConsumedTokens!);
    }

    static InboxCleanupReceipt Proof(InboxCleanupFence? identity = null) =>
        new(identity ?? Fence, "receipt-1", InputFenced: true, BufferEmpty: true, NeverAccepted: true);

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Incomplete_completion_evidence_keeps_the_attempt_fenced(bool fenced, bool empty, bool neverAccepted)
    {
        var inbox = Started();
        inbox.ConsumeCleanup("node", "first", Fence);
        Assert.Throws<ArgumentException>(() => inbox.CompleteCleanup("node", "first",
            Proof() with { InputFenced = fenced, BufferEmpty = empty, NeverAccepted = neverAccepted }));
        Assert.Throws<InvalidOperationException>(() => inbox.Requeue("node", "first", Fence with { Receipt = "invented" }));
        Assert.NotNull(new DurableInbox(Pathname).Peek("node")!.StartedAt);
    }

    [Theory]
    [InlineData("token")]
    [InlineData("revision")]
    [InlineData("attempt")]
    [InlineData("request")]
    [InlineData("incarnation")]
    [InlineData("rid")]
    [InlineData("node")]
    public void A_receipt_for_another_identity_cannot_release_the_head(string mismatch)
    {
        var inbox = Started();
        inbox.ConsumeCleanup("node", "first", Fence);
        var identity = mismatch switch
        {
            "token" => Fence with { Token = "other" },
            "revision" => Fence with { Revision = 8 },
            "attempt" => Fence with { Attempt = 2 },
            "request" => Fence with { HostRequest = "other" },
            "incarnation" => Fence with { Incarnation = "other" },
            _ => Fence,
        };
        Assert.Throws<InvalidOperationException>(() => inbox.CompleteCleanup(
            mismatch == "node" ? "other" : "node", mismatch == "rid" ? "second" : "first", Proof(identity)));
        Assert.Null(Assert.Single(new DurableInbox(Pathname).Peek("node")!.ConsumedTokens!).Receipt);
        Assert.NotNull(inbox.Peek("node")!.StartedAt);
    }

    [Fact]
    public void Completion_requires_a_reservation_and_cannot_change_its_receipt()
    {
        var inbox = Started();
        Assert.Throws<InvalidOperationException>(() => inbox.CompleteCleanup("node", "first", Proof()));
        inbox.ConsumeCleanup("node", "first", Fence);
        var completed = inbox.CompleteCleanup("node", "first", Proof());
        Assert.Equal(completed, new DurableInbox(Pathname).CompleteCleanup("node", "first", Proof()));
        Assert.Throws<InvalidOperationException>(() => inbox.CompleteCleanup("node", "first", Proof() with { Receipt = "other" }));
        Assert.NotNull(inbox.Peek("node")!.StartedAt);
    }

    [Fact]
    public void A_crash_after_proof_storage_can_requeue_in_place_once_and_start_a_fresh_attempt()
    {
        var inbox = Started();
        var original = inbox.Peek("node")!;
        inbox.ConsumeCleanup("node", "first", Fence);
        var completed = inbox.CompleteCleanup("node", "first", Proof());
        inbox = new DurableInbox(Pathname);
        inbox.Requeue("node", "first", completed);
        inbox.Requeue("node", "first", completed);
        var pending = inbox.Peek("node")!;
        Assert.Null(pending.StartedAt);
        Assert.Equal(original.Payload, pending.Payload);
        Assert.Equal(original.QueuedAt, pending.QueuedAt);
        Assert.Equal(original.Sender, pending.Sender);
        Assert.Equal(["first", "second"], JsonNode.Parse(File.ReadAllText(Pathname))!["Messages"]!
            .AsArray().Select(m => m!["Rid"]!.GetValue<string>()));
        Assert.Throws<InvalidOperationException>(() => inbox.Start("node", "first", Now));
        Assert.Throws<InvalidOperationException>(() => inbox.Start("node", "first", Now, "host-1", "process-1"));
        Assert.Throws<InvalidOperationException>(() => inbox.Start("node", "first", Now, "host-2", "process-2"));
        inbox.Start("node", "first", Now.AddMinutes(1), "host-2", "process-1");
        Assert.Equal(2, inbox.Peek("node")!.Attempt);
        Assert.Throws<InvalidOperationException>(() => inbox.Requeue("node", "first", completed));
        Assert.Equal(completed, inbox.CompleteCleanup("node", "first", Proof()));
        Assert.False(inbox.ConsumeCleanup("node", "first", Fence with { Attempt = 2, HostRequest = "host-2" }));
        Assert.NotNull(inbox.Peek("node")!.StartedAt);
    }

    [Fact]
    public void A_final_unconfirmed_attempt_cannot_be_requeued_even_with_a_cleanup_receipt()
    {
        var inbox = Started();
        inbox.ConsumeCleanup("node", "first", Fence);
        var completed = inbox.CompleteCleanup("node", "first", Proof());
        inbox.Deliver("node", "first", Now, "unconfirmed");
        inbox = new DurableInbox(Pathname);
        Assert.Throws<InvalidOperationException>(() => inbox.Requeue("node", "first", completed));
        Assert.Equal("second", inbox.Peek("node")!.Rid);
        inbox.Forget("node", "first");
        Assert.Equal("unconfirmed", inbox.Find("node", "first")!.Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Storage_failure_never_clears_the_started_marker(bool afterCompletion)
    {
        var inbox = Started();
        inbox.ConsumeCleanup("node", "first", Fence);
        var completed = Fence with { Receipt = "receipt-1" };
        if (afterCompletion) { completed = inbox.CompleteCleanup("node", "first", Proof()); }
        var original = File.ReadAllText(Pathname);
        File.Delete(Pathname + ".v2");
        Directory.CreateDirectory(Pathname + ".v2");
        if (afterCompletion) { Assert.Throws<UnauthorizedAccessException>(() => inbox.Requeue("node", "first", completed)); }
        else { Assert.Throws<UnauthorizedAccessException>(() => inbox.CompleteCleanup("node", "first", Proof())); }
        Assert.Equal(original, File.ReadAllText(Pathname));
        Assert.NotNull(JsonNode.Parse(original)!["Messages"]![0]!["StartedAt"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void An_empty_receipt_reference_is_not_a_completion(string reference)
    {
        var inbox = Started();
        inbox.ConsumeCleanup("node", "first", Fence);
        Assert.Throws<ArgumentException>(() => inbox.CompleteCleanup("node", "first", Proof() with { Receipt = reference }));
        Assert.Throws<ArgumentException>(() => inbox.Requeue("node", "first", Fence with { Receipt = reference }));
        Assert.Null(Assert.Single(inbox.Peek("node")!.ConsumedTokens!).Receipt);
    }
}
