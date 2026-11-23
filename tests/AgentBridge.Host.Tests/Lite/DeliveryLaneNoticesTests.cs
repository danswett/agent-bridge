using System.Collections.Concurrent;
using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core.Diagnostics;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Lite;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class DeliveryLaneNoticesTests
{
    const string Target = "target-private";
    const string Body = "body-private";
    static readonly DeliverySender Sender = new("sender-private");

    sealed class Records(TimeProvider clock) : IDiagnosticsJournal
    {
        readonly DiagnosticsJournal journal = new(clock);
        public bool Reject;
        public bool Record(JournalEntry entry) => !Reject && journal.Record(entry);
        public JournalSnapshot Read(int count, int bytes) => journal.Read(count, bytes);
    }

    sealed class Replier : ISessionReplier
    {
        public Func<ReplyRequest, ReplyResult> Reply = _ => throw new InvalidOperationException("Unexpected reply.");
        public bool Available => true;
        public Task<ReplyResult> ReplyAsync(ReplyRequest request, string? peer = null, CancellationToken cancellationToken = default, DeliveryWatch? watch = null) =>
            Task.FromResult(Reply(request));
    }

    sealed class Fake : IDisposable
    {
        public readonly FakeTimeProvider Clock = new();
        public readonly ConcurrentQueue<string> Log = new();
        public readonly string Folder = Path.Combine(Path.GetTempPath(), "s5-" + Guid.NewGuid().ToString("N"));
        public readonly Records Diagnostics;
        public readonly Replier Reply = new();
        public readonly DeliveryJournal Journal;
        public readonly LaneComposition Lane;
        public readonly DeliveryLaneNotices Notices;
        public readonly HostControl Control;
        public bool Flag = true, Mode = true, FailWrite;
        public int Opens;
        public Fake()
        {
            Diagnostics = new(Clock);
            Journal = new(new byte[32], _ => { if (FailWrite) { throw new IOException("failure-private"); } }, Log.Enqueue);
            Lane = new(() => new() { LaneDelivery = Flag }, () => Mode, new DeliveryGate(Clock), Reply,
                _ => (true, true), Folder, Log.Enqueue, Clock, () => { Opens++; return Journal; });
            Notices = new(Lane, Diagnostics, Log.Enqueue, "0.1.0");
            Control = new(new HostControlOptions
            {
                Profile = "test", Folder = Folder, Version = "0.1.0", Time = Clock,
                Machine = new("test", "Test", "windows", "0.1.0"), Running = new(true), LaneNotices = Notices,
                SignIn = (_, _, _) => throw new NotSupportedException(), SignedIn = (_, _) => throw new NotSupportedException(),
                Stop = () => throw new NotSupportedException(), Restart = () => throw new NotSupportedException(),
            }, Log.Enqueue);
        }
        public DeliveryLaneScheduler Scheduler => Lane.Resolve().Scheduler ?? throw new InvalidOperationException("Lane not ready.");
        public void Fill(int count, DeliveryClass sender, string priority)
        {
            for (var i = 0; i < count; i++)
            { Scheduler.Enqueue(Target, new(sender == DeliveryClass.System ? "system" : Sender.From, Guid.NewGuid().ToString("D"), null, Clock.GetUtcNow(), priority, 0), Body, sender); }
        }
        public async Task<LaneNoticeEvent> Capture(Func<Task> act)
        {
            var done = new TaskCompletionSource<LaneNoticeEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Saw(LaneNoticeEvent notice) => done.TrySetResult(notice);
            Lane.Notices += Saw;
            try { await act(); return await done.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            finally { Lane.Notices -= Saw; }
        }
        public void Dispose()
        {
            Notices.Dispose(); Lane.Dispose();
            if (Opens == 0) { Journal.Dispose(); }
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Off_or_killed_observation_never_opens_a_journal(bool flag, bool mode)
    {
        using var f = new Fake { Flag = flag, Mode = mode };
        Assert.Null(f.Control.Status().LaneNotices);
        Assert.Null(f.Lane.Status(Guid.NewGuid(), Target, Sender));
        Assert.Equal(0, f.Opens);
        Assert.Empty(f.Diagnostics.Read(10, JournalContract.MaxTotalBytes).Entries);
        Assert.False(Directory.Exists(f.Folder));
    }

    [Theory]
    [InlineData(DeliveryNoticeCause.Overflow, "Dropped", false)]
    [InlineData(DeliveryNoticeCause.LaneFull, null, false)]
    [InlineData(DeliveryNoticeCause.LaneFull, null, true)]
    [InlineData(DeliveryNoticeCause.DemotedForOwner, "Demoted", false)]
    public async Task Notices_have_typed_evidence_and_content_free_status_and_diagnostics(DeliveryNoticeCause cause, string? state, bool reject)
    {
        using var f = new Fake();
        f.Diagnostics.Reject = reject;
        f.Fill(cause == DeliveryNoticeCause.Overflow ? 50 : 10,
            cause == DeliveryNoticeCause.Overflow ? DeliveryClass.Agent : cause == DeliveryNoticeCause.LaneFull ? DeliveryClass.Owner : DeliveryClass.System,
            cause == DeliveryNoticeCause.Overflow ? "fyi" : "urgent");
        var notice = await f.Capture(() =>
        {
            f.Scheduler.Enqueue(Target, new(Sender.From, Guid.NewGuid().ToString("D"), null, f.Clock.GetUtcNow(),
                cause == DeliveryNoticeCause.Overflow ? "normal" : "urgent", 0), Body,
                cause == DeliveryNoticeCause.Overflow ? DeliveryClass.Agent : DeliveryClass.Owner);
            return Task.CompletedTask;
        });
        Assert.Equal(cause, notice.Notice.Cause);
        Assert.Equal(state, notice.State?.ToString());
        var status = Assert.IsType<LaneNoticeStatus>(f.Control.Status().LaneNotices);
        Assert.Equal(cause, status.Cause);
        Assert.Equal(1, status.Count);
        Assert.Equal(reject, status.DiagnosticsRejected);
        var entries = f.Diagnostics.Read(10, JournalContract.MaxTotalBytes).Entries;
        if (reject) { Assert.Empty(entries); Assert.Contains(f.Log, x => x == "lane diagnostics record refused; notice remains visible in status"); }
        else
        {
            var entry = Assert.Single(entries);
            Assert.Equal(JournalKind.DeliveryLaneNotice, entry.Kind);
            Assert.Equal((int)cause, entry.Code);
        }
        var text = JsonSerializer.Serialize(status, ProtocolJson.Options) + JsonSerializer.Serialize(entries, ProtocolJson.Options);
        foreach (var privateText in new[] { Target, Body, Sender.Id, notice.Notice.Item.Id, notice.Notice.Reason })
        { Assert.DoesNotContain(privateText, text, StringComparison.Ordinal); }
        f.Flag = false;
        Assert.Null(f.Control.Status().LaneNotices);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unconfirmed_handoff_or_failed_append_is_never_a_delivery_receipt(bool failedAppend)
    {
        using var f = new Fake();
        var request = Guid.NewGuid();
        f.Reply.Reply = r => { r.HandoffText!(); f.FailWrite = failedAppend; return new(failedAppend ? AnswerOutcome.Delivered : AnswerOutcome.Unconfirmed); };
        var offered = f.Lane.Offer(request, Target, Sender, Body, () => true);
        Assert.Equal(DeliveryOfferState.Pending, offered.State);
        Assert.Contains("not durable acceptance", offered.Reason);
        var notice = await f.Capture(() => f.Scheduler.DeliverNextAsync(Target));
        Assert.Equal(DeliveryNoticeCause.Unconfirmed, notice.Notice.Cause);
        Assert.Equal(DeliveryState.Unconfirmed, notice.State);
        Assert.Equal(failedAppend, notice.JournalUncertain);
        Assert.Equal(failedAppend, f.Control.Status().LaneNotices?.JournalUncertain);
        Assert.Equal(failedAppend ? JournalOutcome.Failed : JournalOutcome.Unavailable, Assert.Single(f.Diagnostics.Read(10, JournalContract.MaxTotalBytes).Entries).Outcome);
        var query = f.Lane.Status(request, Target, Sender) ?? throw new InvalidOperationException("Missing query.");
        Assert.Equal(DeliveryOfferState.Unconfirmed, query.State);
        Assert.Contains("no retry", query.Reason);
        Assert.Equal(DeliveryOfferState.Refused, f.Lane.Status(request, Target, new("other"))?.State);
        Assert.Equal(DeliveryOfferState.Refused, f.Lane.Status(request, "other", Sender)?.State);
    }

    [Fact]
    public async Task Revocation_notifies_without_dispatch_and_disposal_detaches_observation()
    {
        using var f = new Fake();
        var request = Guid.NewGuid();
        var revoked = false;
        f.Lane.Offer(request, Target, Sender, Body, () => true, () => revoked);
        revoked = true;
        var notice = await f.Capture(() => f.Scheduler.DeliverNextAsync(Target));
        Assert.Equal(DeliveryNoticeCause.RevokedAuthority, notice.Notice.Cause);
        Assert.Equal(DeliveryState.Dropped, notice.State);
        Assert.Equal(DeliveryOfferState.Dropped, f.Lane.Status(request, Target, Sender)?.State);
        f.Notices.Dispose();
        f.Fill(10, DeliveryClass.Owner, "urgent");
        await f.Capture(() => { f.Fill(1, DeliveryClass.Owner, "urgent"); return Task.CompletedTask; });
        Assert.Null(f.Control.Status().LaneNotices);
        Assert.Single(f.Diagnostics.Read(10, JournalContract.MaxTotalBytes).Entries);
    }
}
