using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Protocol;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class RelayReplyInboxTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("relay-inbox-").FullName;
    string PathName => Path.Combine(folder, "replies.json");
    static readonly string First = Guid.NewGuid().ToString("D"), Second = Guid.NewGuid().ToString("D");
    static RelayReplyInbox.Entry Entry(string rid, string text = "private reply") =>
        new(new ReplyRequest { Id = "session", Request = rid, Text = text }, "peer", "account");
    static Task<ReplyResult> Delivered(RelayReplyInbox.Entry entry, CancellationToken ct) =>
        Task.FromResult(new ReplyResult(AnswerOutcome.Delivered));
    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public async Task A_relay_reply_is_on_disk_before_any_dispatch()
    {
        var calls = 0;
        var inbox = new RelayReplyInbox(PathName, (entry, ct) =>
        {
            Assert.Contains("private reply", File.ReadAllText(PathName));
            Assert.Equal(First, entry.Reply!.Request);
            calls++;
            return Delivered(entry, ct);
        });
        Assert.Equal(AnswerOutcome.Delivered, (await inbox.ReceiveAsync("session", First, Entry(First))).Outcome);
        Assert.Equal(1, calls);
        Assert.DoesNotContain("private reply", File.ReadAllText(PathName));
    }

    [Fact]
    public async Task Restart_recovers_unstarted_replies_in_FIFO_order()
    {
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        var before = new RelayReplyInbox(PathName, (_, _) => throw new Xunit.Sdk.XunitException("must not dispatch"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => before.ReceiveAsync("session", First, Entry(First, "first"), stopped.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => before.ReceiveAsync("session", Second, Entry(Second, "second"), stopped.Token));
        var seen = new List<string?>();
        var restarted = new RelayReplyInbox(PathName, (entry, ct) =>
        {
            seen.Add(entry.Reply!.Text);
            return Delivered(entry, ct);
        });
        await restarted.RecoverAsync(["session"]);
        Assert.Equal(["first", "second"], seen);
        await restarted.RecoverAsync(["session"]);
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public async Task An_interrupted_started_reply_is_unconfirmed_and_never_retyped_after_restart()
    {
        using var stopped = new CancellationTokenSource();
        var before = new RelayReplyInbox(PathName, (_, ct) => { stopped.Cancel(); throw new OperationCanceledException(ct); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => before.ReceiveAsync("session", First, Entry(First), stopped.Token));
        var restarted = new RelayReplyInbox(PathName, (_, _) => throw new Xunit.Sdk.XunitException("must not retype"));
        await restarted.RecoverAsync(["session"]);
        Assert.Equal(AnswerOutcome.Unconfirmed, (await restarted.ReceiveAsync("session", First, Entry(First))).Outcome);
    }

    [Fact]
    public async Task A_failed_durable_write_dispatches_nothing()
    {
        File.WriteAllText(PathName, "invalid snapshot");
        var inbox = new RelayReplyInbox(PathName, (_, _) => throw new Xunit.Sdk.XunitException("must not dispatch"));
        Assert.Equal(AnswerOutcome.Refused, (await inbox.ReceiveAsync("session", First, Entry(First))).Outcome);
    }

    [Fact]
    public async Task A_recovered_reply_rechecks_the_current_authorization_before_dispatch()
    {
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        var allowed = true;
        var typed = 0;
        Task<ReplyResult> Dispatch(RelayReplyInbox.Entry entry, CancellationToken ct)
        {
            if (!allowed) { return Task.FromResult(new ReplyResult(AnswerOutcome.Refused, "peer revoked")); }
            typed++;
            return Delivered(entry, ct);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RelayReplyInbox(PathName, Dispatch).ReceiveAsync("session", First, Entry(First), stopped.Token));
        allowed = false;
        var restarted = new RelayReplyInbox(PathName, Dispatch);
        await restarted.RecoverAsync(["session"]);
        Assert.Equal(0, typed);
        Assert.Equal(AnswerOutcome.Refused, (await restarted.ReceiveAsync("session", First, Entry(First))).Outcome);
    }
}
