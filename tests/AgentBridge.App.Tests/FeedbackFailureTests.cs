using AgentBridge.App.Core;
using Xunit;

public sealed class FeedbackFailureTests
{
    sealed class FakeSender : IFeedbackSender
    {
        public Queue<Func<CancellationToken, Task<FeedbackSendResult>>> Script = new();
        public int Calls;
        public Task<FeedbackSendResult> SendAsync(CancellationToken cancel) { Calls++; return Script.Dequeue()(cancel); }
        public FakeSender Then(FeedbackSendResult r) { Script.Enqueue(_ => Task.FromResult(r)); return this; }
    }

    static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_sent_report_says_thanks_with_the_time_and_cannot_be_sent_twice()
    {
        var sender = new FakeSender().Then(new FeedbackSent(T0));
        var vm = new FeedbackSendViewModel(sender);
        await vm.SendAsync();
        Assert.Equal(FeedbackSendState.Sent, vm.State);
        Assert.StartsWith("Thanks. Sent at ", vm.Message);
        await vm.SendAsync();
        Assert.Equal(1, sender.Calls);
    }

    [Fact]
    public async Task A_failed_send_offers_Try_again_and_the_retry_sends_again()
    {
        var sender = new FakeSender().Then(new FeedbackFailed(FeedbackFailureCode.Offline)).Then(new FeedbackSent(T0));
        var vm = new FeedbackSendViewModel(sender);
        await vm.SendAsync();
        Assert.Equal(FeedbackSendState.Failed, vm.State);
        Assert.True(vm.CanRetry);
        Assert.Equal(Tone.Bad, vm.Tone);
        Assert.Equal("Couldn't send. Try again when you're online.", vm.Message);
        await vm.RetryAsync();
        Assert.Equal(FeedbackSendState.Sent, vm.State);
        Assert.Equal(2, sender.Calls);
    }

    [Fact]
    public async Task Every_upload_failure_shows_the_same_words_and_allows_Try_again()
    {
        foreach (var code in Enum.GetValues<FeedbackFailureCode>())
        {
            var vm = new FeedbackSendViewModel(new FakeSender().Then(new FeedbackFailed(code, TimeSpan.FromSeconds(30))));
            await vm.SendAsync();
            Assert.Equal("Couldn't send. Try again when you're online.", vm.Message);
            Assert.True(vm.CanRetry);
        }
    }

    [Fact]
    public async Task Typed_words_and_the_preview_survive_a_failure_and_are_cleared_on_close()
    {
        var vm = new FeedbackSendViewModel(new FakeSender().Then(new FeedbackFailed(FeedbackFailureCode.Offline)));
        vm.Draft.TypedText = "it broke";
        vm.Draft.ReviewedPreview = "preview";
        await vm.SendAsync();
        Assert.Equal("it broke", vm.Draft.TypedText);
        Assert.Equal("preview", vm.Draft.ReviewedPreview);
        vm.Close();
        Assert.Equal("", vm.Draft.TypedText);
        Assert.Equal("", vm.Draft.ReviewedPreview);
    }

    [Fact]
    public async Task Too_large_and_refused_offer_no_retry()
    {
        var vm = new FeedbackSendViewModel(new FakeSender().Then(new FeedbackTooLarge(300_000, 200_000)));
        await vm.SendAsync();
        Assert.Equal(FeedbackSendState.TooLarge, vm.State);
        Assert.False(vm.CanRetry);

        var refused = new FeedbackSendViewModel(new FakeSender().Then(new FeedbackRefused("shape")));
        await refused.SendAsync();
        Assert.Equal(FeedbackSendState.Refused, refused.State);
        Assert.False(refused.CanRetry);
        Assert.DoesNotContain("shape", refused.Message);
    }

    [Fact]
    public async Task A_throwing_sender_never_puts_its_message_on_screen()
    {
        var sender = new FakeSender();
        sender.Script.Enqueue(_ => throw new InvalidOperationException("ghp_secret Bearer abc user@example.com"));
        var vm = new FeedbackSendViewModel(sender);
        await vm.SendAsync();
        Assert.Equal(FeedbackSendState.Failed, vm.State);
        Assert.Equal(FeedbackFailureCode.Unexpected, vm.FailureCode);
        Assert.DoesNotContain("ghp_", vm.Message);
        Assert.DoesNotContain("example.com", vm.Speech);
    }

    [Fact]
    public async Task Cancelling_a_send_returns_to_ready_without_an_error()
    {
        var sender = new FakeSender();
        var gate = new TaskCompletionSource<FeedbackSendResult>();
        sender.Script.Enqueue(c => { c.Register(() => gate.TrySetCanceled(c)); return gate.Task; });
        var vm = new FeedbackSendViewModel(sender);
        var run = vm.SendAsync();
        Assert.True(vm.IsSending);
        vm.Cancel();
        await run;
        Assert.Equal(FeedbackSendState.Ready, vm.State);
        Assert.Equal("", vm.Message);
    }

    [Fact]
    public async Task A_second_press_while_sending_does_not_start_a_second_send()
    {
        var sender = new FakeSender();
        var gate = new TaskCompletionSource<FeedbackSendResult>();
        sender.Script.Enqueue(_ => gate.Task);
        var vm = new FeedbackSendViewModel(sender);
        var first = vm.SendAsync();
        await vm.SendAsync();
        gate.SetResult(new FeedbackSent(T0));
        await first;
        Assert.Equal(1, sender.Calls);
    }

    [Fact]
    public void Report_this_shows_beside_an_error_line_and_hands_over_only_the_action_and_key()
    {
        var line = new ReceiptLine("failed", "Couldn't send it: the machine said no.", null, Tone.Bad, ReceiptMark.None, null);
        var offer = ReportThisOffer.For("reply", line);
        Assert.True(offer.Visible);
        Assert.StartsWith("Report this: ", offer.Speech);
        ReportThisContext? got = null;
        Assert.True(offer.Press(c => got = c));
        Assert.Equal(new ReportThisContext("reply", "failed"), got);
    }

    [Theory]
    [InlineData(Tone.Ok)]
    [InlineData(Tone.Warn)]
    [InlineData(Tone.Progress)]
    [InlineData(Tone.None)]
    public void Report_this_is_not_offered_beside_a_line_that_is_not_an_error(Tone tone)
    {
        var offer = ReportThisOffer.For("answer", new ReceiptLine("x", "Sent", null, tone, ReceiptMark.None, null));
        Assert.False(offer.Visible);
        Assert.False(offer.Press(_ => Assert.Fail("opened")));
    }
}
