using AgentBridge.App.Core;
using AgentBridge.App.Core.Feedback;
using Xunit;

public sealed class FeedbackFailureTests
{
    sealed class FakeSender : IFeedbackSender
    {
        public Queue<Func<CancellationToken, Task<FeedbackSendResult>>> Script = new();
        public List<FeedbackBundle> Sent = new();
        public int Calls => Sent.Count;
        public Task<FeedbackSendResult> SendAsync(FeedbackBundle bundle, CancellationToken cancel) { Sent.Add(bundle); return Script.Dequeue()(cancel); }
        public FakeSender Then(FeedbackSendResult r) { Script.Enqueue(_ => Task.FromResult(r)); return this; }
    }

    static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    static FeedbackBundle Bundle() => new(new byte[] { 1, 2, 3 }, new byte[16], T0, Array.Empty<FeedbackItem>());
    static FeedbackSendResult.Failed Fail(FeedbackRetry retry, TimeSpan? after = null, FeedbackFailureReason reason = FeedbackFailureReason.Offline) => new(reason, retry, after);

    static FeedbackSendViewModel Ready(FakeSender sender, Func<DateTimeOffset>? now = null, Action<string, bool>? announce = null)
    {
        var vm = new FeedbackSendViewModel(sender, null, now, announce);
        vm.Ready(Bundle());
        return vm;
    }

    [Fact]
    public async Task Nothing_sends_while_gathering()
    {
        var sender = new FakeSender();
        var vm = new FeedbackSendViewModel(sender);
        Assert.Equal(FeedbackSendState.Gathering, vm.State);
        Assert.False(vm.CanSend);
        await vm.SendAsync();
        Assert.Equal(0, sender.Calls);
        vm.Ready(Bundle());
        Assert.True(vm.CanSend);
    }

    [Fact]
    public async Task A_sent_report_says_thanks_with_the_time_and_cannot_be_sent_twice()
    {
        var sender = new FakeSender().Then(new FeedbackSendResult.Sent(T0));
        var spoken = new List<(string, bool)>();
        var vm = Ready(sender, announce: (s, u) => spoken.Add((s, u)));
        await vm.SendAsync();
        Assert.Equal(FeedbackSendState.Sent, vm.State);
        Assert.StartsWith("Thanks. Sent at ", vm.Message);
        Assert.Equal(vm.Message, vm.Speech);
        Assert.Equal(new[] { ("Sending…", false), (vm.Message, false) }, spoken);
        await vm.SendAsync();
        Assert.Equal(1, sender.Calls);
    }

    [Fact]
    public async Task A_failed_send_says_the_words_assertively_and_Try_again_resends_the_same_bundle()
    {
        var sender = new FakeSender().Then(Fail(FeedbackRetry.WhenOnline)).Then(new FeedbackSendResult.Sent(T0));
        var spoken = new List<(string, bool)>();
        var vm = Ready(sender, announce: (s, u) => spoken.Add((s, u)));
        await vm.SendAsync();
        Assert.Equal(FeedbackSendState.Failed, vm.State);
        Assert.True(vm.CanRetry);
        Assert.Equal(Tone.Bad, vm.Tone);
        Assert.Equal("Couldn't send. Try again when you're online.", vm.Message);
        Assert.Contains(("Couldn't send. Try again when you're online.", true), spoken);
        await vm.RetryAsync();
        Assert.Equal(FeedbackSendState.Sent, vm.State);
        Assert.Equal(2, sender.Calls);
        Assert.Same(sender.Sent[0], sender.Sent[1]);
    }

    [Fact]
    public async Task Every_failure_reason_shows_the_same_words()
    {
        foreach (var reason in Enum.GetValues<FeedbackFailureReason>().Where(r => r != FeedbackFailureReason.Cancelled))
        {
            var vm = Ready(new FakeSender().Then(Fail(FeedbackRetry.Now, reason: reason)));
            await vm.SendAsync();
            Assert.Equal(FeedbackSendState.Failed, vm.State);
            Assert.Equal(reason, vm.FailureReason);
            Assert.Equal("Couldn't send. Try again when you're online.", vm.Message);
        }
    }

    [Theory]
    [InlineData(FeedbackRetry.Now, true)]
    [InlineData(FeedbackRetry.WhenOnline, true)]
    [InlineData(FeedbackRetry.Never, false)]
    public async Task Try_again_follows_what_the_sender_says(FeedbackRetry retry, bool allowed)
    {
        var sender = new FakeSender().Then(Fail(retry, reason: FeedbackFailureReason.Disabled));
        var vm = Ready(sender);
        await vm.SendAsync();
        Assert.Equal(allowed, vm.CanRetry);
        sender.Then(new FeedbackSendResult.Sent(T0));
        await vm.RetryAsync();
        Assert.Equal(allowed ? 2 : 1, sender.Calls);
    }

    [Fact]
    public async Task After_a_delay_Try_again_shows_when_and_waits_without_retrying_by_itself()
    {
        var clock = T0;
        var sender = new FakeSender().Then(Fail(FeedbackRetry.AfterDelay, TimeSpan.FromSeconds(30), FeedbackFailureReason.RateLimited));
        var vm = Ready(sender, () => clock);
        await vm.SendAsync();
        Assert.Equal(T0.AddSeconds(30), vm.RetryAt);
        Assert.StartsWith("You can try again at ", vm.RetryHint);
        Assert.False(vm.CanRetry);
        await vm.RetryAsync();
        Assert.Equal(1, sender.Calls);
        clock = T0.AddSeconds(31);
        vm.RefreshRetry();
        Assert.True(vm.CanRetry);
        Assert.Equal("", vm.RetryHint);
        Assert.Equal(1, sender.Calls);
    }

    [Fact]
    public async Task Typed_words_and_the_preview_survive_a_failure()
    {
        var vm = Ready(new FakeSender().Then(Fail(FeedbackRetry.WhenOnline)));
        vm.Draft.TypedText = "it broke";
        vm.Draft.ReviewedPreview = "preview";
        await vm.SendAsync();
        Assert.Equal("it broke", vm.Draft.TypedText);
        Assert.Equal("preview", vm.Draft.ReviewedPreview);
    }

    [Fact]
    public async Task Close_after_a_failure_clears_everything_and_nothing_can_be_sent()
    {
        var sender = new FakeSender().Then(Fail(FeedbackRetry.WhenOnline));
        var vm = Ready(sender);
        vm.Draft.TypedText = "it broke";
        vm.Draft.ReviewedPreview = "preview";
        await vm.SendAsync();
        vm.Close();
        Assert.Equal("", vm.Draft.TypedText);
        Assert.Equal("", vm.Draft.ReviewedPreview);
        Assert.Equal("", vm.Message);
        Assert.Equal(FeedbackSendState.Ready, vm.State);
        Assert.Null(vm.FailureReason);
        Assert.False(vm.CanRetry);
        Assert.False(vm.Failed);
        sender.Then(new FeedbackSendResult.Sent(T0));
        await vm.RetryAsync();
        await vm.SendAsync();
        Assert.Equal(1, sender.Calls);
        vm.Cancel();
        vm.Close();
    }

    [Fact]
    public async Task A_result_that_arrives_after_Close_is_dropped()
    {
        var sender = new FakeSender();
        var gate = new TaskCompletionSource<FeedbackSendResult>();
        sender.Script.Enqueue(_ => gate.Task);
        var spoken = new List<string>();
        var vm = Ready(sender, announce: (s, _) => spoken.Add(s));
        var run = vm.SendAsync();
        vm.Close();
        spoken.Clear();
        gate.SetResult(Fail(FeedbackRetry.WhenOnline));
        await run;
        Assert.Equal(FeedbackSendState.Ready, vm.State);
        Assert.Equal("", vm.Message);
        Assert.False(vm.CanRetry);
        Assert.Empty(spoken);
    }

    [Fact]
    public async Task Too_large_offers_no_retry_and_sending_again_needs_a_regathered_bundle()
    {
        var sender = new FakeSender().Then(new FeedbackSendResult.TooLarge(3_000_000, 2_000_000)).Then(new FeedbackSendResult.Sent(T0));
        var vm = Ready(sender);
        await vm.SendAsync();
        Assert.Equal(FeedbackSendState.TooLarge, vm.State);
        Assert.False(vm.CanRetry);
        Assert.False(vm.CanSend);
        await vm.SendAsync();
        Assert.Equal(1, sender.Calls);
        vm.BeginGathering();
        Assert.Equal(FeedbackSendState.Gathering, vm.State);
        Assert.False(vm.CanSend);
        var edited = Bundle();
        vm.Ready(edited);
        Assert.True(vm.CanSend);
        await vm.SendAsync();
        Assert.Same(edited, sender.Sent[1]);
        Assert.Equal(FeedbackSendState.Sent, vm.State);
    }

    [Fact]
    public async Task Refused_offers_no_retry_and_no_send_and_never_shows_the_reason()
    {
        var sender = new FakeSender().Then(new FeedbackSendResult.Refused(FeedbackSendRefusal.NotSignedIn));
        var vm = Ready(sender);
        await vm.SendAsync();
        Assert.Equal(FeedbackSendState.Refused, vm.State);
        Assert.False(vm.CanRetry);
        Assert.False(vm.CanSend);
        Assert.DoesNotContain("NotSignedIn", vm.Message);
        await vm.SendAsync();
        await vm.RetryAsync();
        Assert.Equal(1, sender.Calls);
    }

    [Fact]
    public async Task A_throwing_sender_never_puts_its_message_on_screen()
    {
        var sender = new FakeSender();
        sender.Script.Enqueue(_ => throw new InvalidOperationException("ghp_secret Bearer abc user@example.com"));
        var vm = Ready(sender);
        await vm.SendAsync();
        Assert.Equal(FeedbackSendState.Failed, vm.State);
        Assert.Equal(FeedbackSendViewModel.FailedText, vm.Message);
        Assert.DoesNotContain("ghp_", vm.Message);
        Assert.DoesNotContain("example.com", vm.Speech);
    }

    [Fact]
    public async Task Cancelling_a_send_returns_to_ready_without_an_error()
    {
        var sender = new FakeSender();
        var gate = new TaskCompletionSource<FeedbackSendResult>();
        sender.Script.Enqueue(c => { c.Register(() => gate.TrySetCanceled(c)); return gate.Task; });
        var vm = Ready(sender);
        var run = vm.SendAsync();
        Assert.True(vm.IsSending);
        vm.Cancel();
        await run;
        Assert.Equal(FeedbackSendState.Ready, vm.State);
        Assert.Equal("", vm.Message);
        Assert.True(vm.CanSend);
    }

    [Fact]
    public async Task A_cancelled_send_that_returns_a_result_instead_of_throwing_is_not_an_error()
    {
        var sender = new FakeSender();
        var gate = new TaskCompletionSource<FeedbackSendResult>();
        sender.Script.Enqueue(c => { c.Register(() => gate.TrySetResult(Fail(FeedbackRetry.Never, reason: FeedbackFailureReason.Cancelled))); return gate.Task; });
        var vm = Ready(sender);
        var run = vm.SendAsync();
        vm.Cancel();
        await run;
        Assert.Equal(FeedbackSendState.Ready, vm.State);
        Assert.Equal("", vm.Message);
        Assert.False(vm.Failed);
    }

    [Fact]
    public async Task A_second_press_while_sending_does_not_start_a_second_send()
    {
        var sender = new FakeSender();
        var gate = new TaskCompletionSource<FeedbackSendResult>();
        sender.Script.Enqueue(_ => gate.Task);
        var vm = Ready(sender);
        var first = vm.SendAsync();
        await vm.SendAsync();
        gate.SetResult(new FeedbackSendResult.Sent(T0));
        await first;
        Assert.Equal(1, sender.Calls);
    }

    [Fact]
    public async Task Sending_and_failing_write_nothing_to_disk()
    {
        static string[] Files(string dir) => Directory.GetFileSystemEntries(dir, "*", SearchOption.AllDirectories).Order().ToArray();
        var dir = Path.Combine(Path.GetTempPath(), "fb738-s4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var saved = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = dir;
            var vm = Ready(new FakeSender().Then(Fail(FeedbackRetry.WhenOnline)));
            vm.Draft.TypedText = "words";
            await vm.SendAsync();
            vm.Close();
            Assert.Empty(Files(dir));
        }
        finally { Environment.CurrentDirectory = saved; Directory.Delete(dir, true); }
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