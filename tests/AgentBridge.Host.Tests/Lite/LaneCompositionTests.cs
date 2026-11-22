using AgentBridge.Host.Core.AgentMessaging;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Lite;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class LaneCompositionTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "lane-comp-" + Guid.NewGuid().ToString("N"));
    readonly List<string> log = [];
    readonly FakeTimeProvider clock = new();
    bool flag = true, kill = true;
    (bool? Idle, bool? Empty) screen = (true, true);
    Func<DeliveryJournal>? open;

    sealed class Replier : ISessionReplier
    {
        public bool Available => true;
        public Task<ReplyResult> ReplyAsync(ReplyRequest request, string? peer = null, CancellationToken cancellationToken = default, DeliveryWatch? watch = null) =>
            Task.FromResult(new ReplyResult(AnswerOutcome.Delivered));
    }

    public LaneCompositionTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    LaneComposition Create() => new(
        () => new AgentMessagingSettings { Remote = AgentMessagingSettings.OwnAccount, RemoteSet = true, LaneDelivery = flag },
        () => kill, new DeliveryGate(clock), new Replier(), new LaneScreens((_, _) => Task.FromResult(screen), clock), root, log.Add, clock, open);

    static DeliveryJournal Memory() => new(new byte[32], _ => { }, _ => { });
    static DeliveryHeader Header(string id) => new("owner:user", id, null, DateTimeOffset.UnixEpoch, "normal", 0);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Without_the_flag_and_liteKill_nothing_is_built_and_nothing_is_written(bool laneFlag, bool liteKillOn)
    {
        (flag, kill) = (laneFlag, liteKillOn);
        using var lane = Create();
        Assert.Equal(LaneStatus.Off, lane.Resolve().Status);
        Assert.Equal(DeliveryOfferState.Refused, lane.Offer(Guid.NewGuid(), "t", new("user"), "body", () => true).State);
        Assert.False(lane.HasPending("t"));
        Assert.Null(lane.Status(Guid.NewGuid(), "t", new("user")));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public void The_real_journal_opens_once_with_its_private_key_and_observers_never_build_it()
    {
        using var lane = Create();
        Assert.False(lane.HasPending("t"));
        Assert.Empty(Directory.GetFileSystemEntries(root));
        var first = lane.Resolve();
        Assert.Equal(LaneStatus.Ready, first.Status);
        Assert.Same(first.Scheduler, lane.Resolve().Scheduler);
        Assert.Single(Directory.GetFiles(root, "delivery.key", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_journal_that_cannot_open_refuses_for_the_run_even_after_the_flag_cycles_and_logs_once()
    {
        var attempts = 0;
        open = () => attempts++ == 0 ? throw new InvalidDataException("secret path C:\\x") : Memory();
        using var lane = Create();
        var refused = lane.Resolve();
        Assert.Equal(LaneStatus.Refused, refused.Status);
        flag = false; Assert.Equal(LaneStatus.Off, lane.Resolve().Status);
        flag = true; Assert.Equal(LaneStatus.Refused, lane.Resolve().Status);
        Assert.Equal(1, attempts);
        Assert.Equal(DeliveryOfferState.Refused, lane.Offer(Guid.NewGuid(), "t", new("user"), "body", () => true).State);
        var line = Assert.Single(log);
        Assert.DoesNotContain("secret", line + refused.Reason);
    }

    [Fact]
    public async Task Notices_reach_subscribers_in_order_after_the_scheduler_lock_with_the_journal_state_and_a_failing_subscriber_is_isolated()
    {
        open = Memory;
        using var lane = Create();
        var received = new List<LaneNoticeEvent>();
        var release = new ManualResetEventSlim();
        var done = new TaskCompletionSource();
        lane.Notices += _ => throw new InvalidOperationException("boom");
        lane.Notices += e => { Assert.True(release.Wait(TimeSpan.FromSeconds(10))); lock (received) { received.Add(e); } done.TrySetResult(); };
        var scheduler = lane.Resolve().Scheduler!;
        for (var i = 0; i < 10; i++) { Assert.True(scheduler.Enqueue("t", Header(Guid.NewGuid().ToString("D")), "b", DeliveryClass.Owner)); }
        Assert.False(scheduler.Enqueue("t", Header(Guid.NewGuid().ToString("D")), "b", DeliveryClass.Owner));
        lock (received) { Assert.Empty(received); }
        release.Set();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var notice = Assert.Single(received);
        Assert.Equal("t", notice.Notice.Target);
        Assert.Null(notice.State);
        Assert.False(notice.JournalUncertain);
        Assert.Contains(log, l => l.Contains("subscriber failed (InvalidOperationException)") && !l.Contains("boom"));
    }

    [Fact]
    public void An_accepted_offer_raises_Offered_after_the_scheduler_returns_and_observers_see_it_pending()
    {
        open = Memory;
        screen = (false, true);
        using var lane = Create();
        var woken = new List<string>();
        lane.Offered += woken.Add;
        var request = Guid.NewGuid(); var sender = new DeliverySender("user");
        Assert.Equal(DeliveryOfferState.Pending, lane.Offer(request, "t", sender, "body", () => true).State);
        Assert.Equal(["t"], woken);
        Assert.True(lane.HasPending("t"));
        Assert.Equal(DeliveryOfferState.Pending, lane.Status(request, "t", sender)!.State);
        Assert.False(lane.Uncertain);
    }
}
