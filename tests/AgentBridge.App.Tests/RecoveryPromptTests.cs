using AgentBridge.App.Core;
using AgentBridge.App.Core.Feedback;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class RecoveryPromptTests
{
    sealed class Sender : IFeedbackSender
    {
        public readonly List<FeedbackBundle> Calls = [];
        public Task<FeedbackSendResult> SendAsync(FeedbackBundle bundle, CancellationToken ct)
        {
            Calls.Add(bundle);
            return Task.FromResult<FeedbackSendResult>(new FeedbackSendResult.Sent(DateTimeOffset.UnixEpoch));
        }
    }

    [Fact]
    public async Task Recovery_waits_for_the_window_and_opens_only_once_across_loaded_and_activation_events()
    {
        var offer = new RecoveryReportOffer(new PreviousRun(true));
        var closed = new TaskCompletionSource();
        var calls = 0;
        async Task Show() { calls++; await closed.Task; offer.Dismiss(); }
        Assert.False(await offer.OpenWhenReadyAsync(false, Show));
        Assert.Equal(0, calls);
        var first = offer.OpenWhenReadyAsync(true, Show);
        Assert.Equal(1, calls);
        Assert.False(await offer.OpenWhenReadyAsync(true, Show));
        closed.SetResult();
        Assert.True(await first);
        Assert.False(await offer.OpenWhenReadyAsync(true, Show));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_dialog_that_cannot_open_keeps_the_snapshot_for_the_next_window_activation()
    {
        var previous = new PreviousRun(true, "previous");
        var offer = new RecoveryReportOffer(previous);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            offer.OpenWhenReadyAsync(true, () => throw new InvalidOperationException("another dialog is open")));
        Assert.Same(previous, offer.Previous);
        var calls = 0;
        Assert.True(await offer.OpenWhenReadyAsync(true, () => { calls++; offer.Dismiss(); return Task.CompletedTask; }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Clean_or_dismissed_runs_do_not_open_a_window_ready_prompt()
    {
        var clean = new RecoveryReportOffer(new PreviousRun(false));
        var dismissed = new RecoveryReportOffer(new PreviousRun(true));
        dismissed.Dismiss();
        Task Unexpected() => throw new InvalidOperationException("must not open");
        Assert.False(await clean.OpenWhenReadyAsync(true, Unexpected));
        Assert.False(await dismissed.OpenWhenReadyAsync(true, Unexpected));
    }

    [Fact]
    public async Task A_relay_becoming_available_enables_one_accept_without_sending_or_changing_the_reviewed_bundle()
    {
        var offer = new RecoveryReportOffer(new PreviousRun(true));
        var send = new FeedbackSendViewModel(null);
        var bundle = new FeedbackBundle("{}"u8.ToArray(), new byte[16], DateTimeOffset.UnixEpoch, []);
        send.Ready(bundle);
        var sender = new Sender();
        send.RefreshSender(sender);
        Assert.True(send.CanSend);
        Assert.Empty(sender.Calls);
        await offer.AcceptAsync(send);
        send.RefreshSender(null);
        await offer.AcceptAsync(send);
        Assert.Equal(FeedbackSendState.Sent, send.State);
        Assert.Same(bundle, Assert.Single(sender.Calls));
    }

    [Fact]
    public async Task Losing_feedback_availability_blocks_accept_until_the_existing_sender_path_is_available_again()
    {
        var offer = new RecoveryReportOffer(new PreviousRun(true));
        var sender = new Sender();
        var send = new FeedbackSendViewModel(sender);
        send.Ready(new FeedbackBundle("{}"u8.ToArray(), new byte[16], DateTimeOffset.UnixEpoch, []));
        send.RefreshSender(null);
        await offer.AcceptAsync(send);
        Assert.Empty(sender.Calls);
        Assert.True(offer.Visible);
        send.RefreshSender(sender);
        await offer.AcceptAsync(send);
        Assert.Single(sender.Calls);
    }

    [Fact]
    public async Task Recovery_log_fragments_are_excluded_from_the_structured_preview()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ab-recovery-" + Guid.NewGuid().ToString("N"));
        var sources = new AppFeedbackSources(() => AppFeedbackSources.Describe("current", "prerelease", []),
            folder, Path.Combine(folder, "settings.json"), _ => Task.FromResult<AgentBridge.Local.AgentReadinessReport?>(null),
            (_, _, _) => Task.FromResult<AgentBridge.Protocol.HistoryResult?>(null));
        var request = new FeedbackRequest { Recovery = new(new PreviousRun(true, "previous"), "local", null, ["pass", "word=", "MapleBoat7!"]) };
        var bundle = Assert.IsType<FeedbackGatherResult.Ready>(await new FeedbackBundleSource(sources).GatherAsync(request, null, default)).Bundle;
        Assert.Equal("recovery", Assert.Single(bundle.Items).Id);
        Assert.DoesNotContain("LastLogLines", bundle.Text);
        Assert.DoesNotContain("MapleBoat7!", bundle.Text);
    }
}
