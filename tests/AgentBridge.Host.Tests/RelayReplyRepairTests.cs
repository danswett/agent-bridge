using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Protocol;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class RelayReplyRepairTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("relay-repair-").FullName;
    string PathName => Path.Combine(folder, "replies.inbox");
    static RelayReplyInbox.Entry Entry(string rid, string peer = "peer", string node = "session") =>
        new(new ReplyRequest { Id = node, Request = rid, Text = "private reply" }, peer, "account");
    static Task<ReplyResult> Delivered(RelayReplyInbox.Entry entry, CancellationToken ct) => Task.FromResult(new ReplyResult(AnswerOutcome.Delivered));
    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public async Task Ordinary_refusals_do_not_pin_the_relay_queue_at_its_capacity()
    {
        var refused = new RelayReplyInbox(PathName, (_, _) => Task.FromResult(new ReplyResult(AnswerOutcome.Refused, "busy")));
        for (var i = 0; i < 100; i++)
        {
            var rid = Guid.NewGuid().ToString("D");
            await refused.ReceiveAsync("session", rid, Entry(rid));
        }
        var next = Guid.NewGuid().ToString("D");
        Assert.Equal(AnswerOutcome.Delivered, (await new RelayReplyInbox(PathName, Delivered).ReceiveAsync("session", next, Entry(next))).Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_poison_or_expired_head_is_refused_without_blocking_the_next_entry(bool expired)
    {
        using var stopped = new CancellationTokenSource(); stopped.Cancel();
        var first = Guid.NewGuid().ToString("D");
        var second = Guid.NewGuid().ToString("D");
        var inbox = new RelayReplyInbox(PathName, Delivered);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inbox.ReceiveAsync("session", first, Entry(first), stopped.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inbox.ReceiveAsync("session", second, Entry(second), stopped.Token));
        var snapshot = JsonNode.Parse(File.ReadAllText(PathName))!;
        var head = snapshot["Messages"]![0]!;
        if (expired) { head["QueuedAt"] = DateTimeOffset.UtcNow.AddMinutes(-3); }
        else { head["Payload"] = "not json"; }
        File.WriteAllText(PathName, snapshot.ToJsonString());
        var seen = new List<string?>();
        var restarted = new RelayReplyInbox(PathName, (entry, ct) => { seen.Add(entry.Reply!.Request); return Delivered(entry, ct); });
        await restarted.RecoverAsync(["session"]);
        Assert.Equal([second], seen);
        Assert.Equal(AnswerOutcome.Refused, (await restarted.ReceiveAsync("session", first, Entry(first))).Outcome);
    }

    [Fact]
    public async Task A_corrupt_file_does_not_escape_recovery_and_prevent_relay_attachment()
    {
        File.WriteAllText(PathName, "invalid snapshot");
        await new RelayReplyInbox(PathName, Delivered).RecoverAsync(["session"]);
    }

    [Fact]
    public async Task Another_peer_cannot_claim_a_retained_request_id_or_observe_its_outcome()
    {
        var rid = Guid.NewGuid().ToString("D");
        var inbox = new RelayReplyInbox(PathName, Delivered);
        Assert.Equal(AnswerOutcome.Delivered, (await inbox.ReceiveAsync("session", rid, Entry(rid, "peer-a"))).Outcome);
        Assert.Equal(AnswerOutcome.Refused, (await inbox.ReceiveAsync("session", rid, Entry(rid, "peer-b"))).Outcome);
    }

    [Fact]
    public async Task Independent_sessions_do_not_wait_for_an_occupied_dispatch()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inbox = new RelayReplyInbox(PathName, async (entry, ct) =>
        {
            if (entry.Reply!.Id == "first") { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
            return new(AnswerOutcome.Delivered);
        });
        var rid = Guid.NewGuid().ToString("D");
        var first = inbox.ReceiveAsync("first", rid, Entry(rid, node: "first"));
        Task<ReplyResult>? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = inbox.ReceiveAsync("second", rid, Entry(rid, node: "second"));
            Assert.Equal(AnswerOutcome.Delivered, (await second.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        }
        finally { release.TrySetResult(); await first; if (second is not null) { await second; } }
    }

    [Fact]
    public async Task A_dispatch_exception_is_terminal_unconfirmed_and_is_never_retyped()
    {
        var rid = Guid.NewGuid().ToString("D");
        var inbox = new RelayReplyInbox(PathName, (_, _) => throw new InvalidOperationException("private reply"));
        Assert.Equal(AnswerOutcome.Unconfirmed, (await inbox.ReceiveAsync("session", rid, Entry(rid))).Outcome);
        Assert.Equal(AnswerOutcome.Unconfirmed, (await new RelayReplyInbox(PathName, (_, _) => throw new Exception("must not dispatch"))
            .ReceiveAsync("session", rid, Entry(rid))).Outcome);
        Assert.DoesNotContain("private reply", File.ReadAllText(PathName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    public async Task An_invalid_request_id_is_a_refusal_not_a_dispatch_or_exception(string rid)
        => Assert.Equal(AnswerOutcome.Refused, (await new RelayReplyInbox(PathName, Delivered).ReceiveAsync("session", rid, Entry(rid))).Outcome);
}
