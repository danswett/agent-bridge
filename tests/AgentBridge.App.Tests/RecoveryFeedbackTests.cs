using AgentBridge.App.Core;
using AgentBridge.App.Core.Feedback;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class RecoveryFeedbackTests : IDisposable
{
    readonly string folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ab-recovery-" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() => Directory.Delete(folder, true);
    AppFeedbackSources Sources() => new(() => AppFeedbackSources.Describe("current", "prerelease", []),
        folder, Path.Combine(folder, "settings.json"), _ => Task.FromResult<AgentBridge.Local.AgentReadinessReport?>(null),
        (_, _, _) => Task.FromResult<AgentBridge.Protocol.HistoryResult?>(null));
    static FeedbackBundle Bundle() => new("{}"u8.ToArray(), new byte[16], DateTimeOffset.UnixEpoch, []);

    sealed class Sender : IFeedbackSender
    {
        public readonly List<FeedbackBundle> Calls = [];
        public FeedbackSendResult Result = new FeedbackSendResult.Sent(DateTimeOffset.UnixEpoch);
        public Task<FeedbackSendResult> SendAsync(FeedbackBundle bundle, CancellationToken ct)
        {
            Calls.Add(bundle);
            return Task.FromResult(Result);
        }
    }

    sealed class Source(Func<Task<FeedbackGatherResult>> gather) : IFeedbackBundleSource
    {
        public Task<FeedbackGatherResult> GatherAsync(FeedbackRequest request, IProgress<FeedbackGatherStage>? progress, CancellationToken ct) => gather();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Previous_crash_or_hang_is_previewed_and_redacted_before_one_accept_sends_the_exact_bundle(bool hang)
    {
        var token = "ghp_" + new string('x', 30);
        File.WriteAllText(Path.Combine(folder, "app.log"), "render tick " + token + "\n");
        var marker = RunMarker.Start(folder, "1.2.3");
        if (hang) { marker.RecordHang(42, TimeSpan.FromSeconds(15), ["render tick " + token]); }
        else { marker.RecordCrash(new InvalidOperationException("private conversation")); }
        var previous = RunMarker.Start(folder, "next").Previous;
        var offer = new RecoveryReportOffer(previous);
        var sources = Sources();
        var request = await sources.RecoveryRequestAsync(previous, "local", "session-id", default);
        Assert.Equal(previous, request.Recovery!.Run);
        Assert.Equal("local", request.Recovery.Machine);
        Assert.Equal("session-id", request.Recovery.SessionId);
        Assert.Empty(request.Recovery.LastLogLines);
        Assert.Null(request.SessionTurnsFor);
        var sender = new Sender();
        var send = new FeedbackSendViewModel(sender);
        var bundle = Assert.IsType<FeedbackBundle>(await send.PrepareAsync(new FeedbackBundleSource(sources, privateNames: []), request));
        Assert.Contains(bundle.Items, i => i.Id == "recovery" && !i.Optional);
        Assert.Contains("1.2.3", bundle.Text);
        Assert.DoesNotContain("session-id", bundle.Text);
        Assert.Contains(hang ? "SecondsStalled" : nameof(InvalidOperationException), bundle.Text);
        Assert.DoesNotContain("render tick", bundle.Text);
        Assert.DoesNotContain(token, bundle.Text);
        Assert.DoesNotContain("private conversation", bundle.Text);
        Assert.False(FeedbackRedactor.BundleHasSecret(bundle.Text));
        Assert.Empty(sender.Calls);
        await offer.AcceptAsync(send);
        await offer.AcceptAsync(send);
        Assert.Same(bundle, Assert.Single(sender.Calls));
        Assert.False(offer.Visible);
    }

    [Fact]
    public async Task Dismiss_consumes_only_the_previous_snapshot_and_sends_nothing()
    {
        RunMarker.Start(folder, "previous");
        var active = RunMarker.Start(folder, "active");
        var offer = new RecoveryReportOffer(active.Previous);
        var sender = new Sender();
        var send = new FeedbackSendViewModel(sender);
        send.Ready(Bundle());
        offer.Dismiss();
        offer.Dismiss();
        await offer.AcceptAsync(send);
        Assert.False(offer.Visible);
        Assert.Null(offer.Previous);
        Assert.Empty(sender.Calls);
        Assert.Equal("active", RunMarker.ReadPrevious(folder).Version);
        active.Complete();
        Assert.False(RunMarker.ReadPrevious(folder).Unclean);
    }

    [Fact]
    public async Task A_clean_previous_run_never_offers_or_sends_a_report()
    {
        var offer = new RecoveryReportOffer(RunMarker.Start(folder, "first").Previous);
        var sender = new Sender();
        var send = new FeedbackSendViewModel(sender);
        send.Ready(Bundle());
        await offer.AcceptAsync(send);
        Assert.False(offer.Visible);
        Assert.Empty(sender.Calls);
    }

    [Fact]
    public async Task A_failed_accept_is_not_replayed_by_another_accept()
    {
        var offer = new RecoveryReportOffer(new PreviousRun(true));
        var sender = new Sender { Result = new FeedbackSendResult.Failed(FeedbackFailureReason.Offline, FeedbackRetry.WhenOnline) };
        var send = new FeedbackSendViewModel(sender);
        send.Ready(Bundle());
        await offer.AcceptAsync(send);
        await offer.AcceptAsync(send);
        Assert.Single(sender.Calls);
        Assert.True(send.CanRetry);
    }

    [Fact]
    public async Task An_unavailable_sender_keeps_the_preview_read_only_and_does_not_consume_the_offer()
    {
        var previous = new PreviousRun(true, "previous");
        var offer = new RecoveryReportOffer(previous);
        var sources = Sources();
        var send = new FeedbackSendViewModel(null);
        var bundle = await send.PrepareAsync(new FeedbackBundleSource(sources), await sources.RecoveryRequestAsync(previous, "local", null, default));
        Assert.NotNull(bundle);
        Assert.False(send.CanSend);
        await offer.AcceptAsync(send);
        Assert.True(offer.Visible);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_refused_or_timed_out_prefilled_report_cannot_be_sent(bool timeout)
    {
        var sender = new Sender();
        var send = new FeedbackSendViewModel(sender);
        var source = new Source(() => timeout ? Task.FromException<FeedbackGatherResult>(new TimeoutException())
            : Task.FromResult<FeedbackGatherResult>(new FeedbackGatherResult.Refused(FeedbackRefusalReason.BundleTooLarge)));
        Assert.Null(await send.PrepareAsync(source, new FeedbackRequest()));
        Assert.False(send.CanSend);
        Assert.NotEmpty(send.Message);
        Assert.Empty(sender.Calls);
    }

    [Fact]
    public async Task A_late_prefill_cannot_replace_the_newer_preview_or_sent_bytes()
    {
        var sender = new Sender();
        var send = new FeedbackSendViewModel(sender);
        var old = new TaskCompletionSource<FeedbackGatherResult>();
        var first = send.PrepareAsync(new Source(() => old.Task), new FeedbackRequest());
        var latest = Bundle();
        await send.PrepareAsync(new Source(() => Task.FromResult<FeedbackGatherResult>(new FeedbackGatherResult.Ready(latest))), new FeedbackRequest());
        old.SetResult(new FeedbackGatherResult.Ready(Bundle()));
        Assert.Null(await first);
        await send.SendAsync();
        Assert.Same(latest, Assert.Single(sender.Calls));
    }
}
