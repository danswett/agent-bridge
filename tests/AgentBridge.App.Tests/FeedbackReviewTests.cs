using AgentBridge.App.Core;
using AgentBridge.App.Core.Feedback;
using Xunit;

public sealed class FeedbackReviewTests
{
    sealed class FakeSender : IFeedbackSender
    {
        public int Calls;
        public Task<FeedbackSendResult> SendAsync(FeedbackBundle bundle, CancellationToken cancel) { Calls++; return Task.FromResult<FeedbackSendResult>(new FeedbackSendResult.Sent(DateTimeOffset.UnixEpoch)); }
    }

    sealed class Source : IFeedbackBundleSource
    {
        public List<(FeedbackRequest Request, TaskCompletionSource<FeedbackGatherResult> Done)> Calls = new();
        public Task<FeedbackGatherResult> GatherAsync(FeedbackRequest request, IProgress<FeedbackGatherStage>? progress, CancellationToken cancel)
        {
            var done = new TaskCompletionSource<FeedbackGatherResult>();
            Calls.Add((request, done));
            return done.Task;
        }
    }

    static FeedbackBundle Bundle(params string[] labels) => new(new byte[] { 1 }, new byte[16], DateTimeOffset.UnixEpoch,
        labels.Select((l, i) => new FeedbackItem("i" + i, l, "summary " + l, 1, i > 0)).ToList());
    static async Task Finish(TaskCompletionSource<FeedbackGatherResult> done, Action<TaskCompletionSource<FeedbackGatherResult>> set) { set(done);     await Task.Delay(100); }    static (FeedbackReviewViewModel Vm, FeedbackSendViewModel Send, Source Source, FakeSender Sender) Make()
    {
        var sender = new FakeSender();
        var send = new FeedbackSendViewModel(sender);
        var source = new Source();
        return (new FeedbackReviewViewModel(source, send, "s1", "Build fix"), send, source, sender);
    }

    [Fact]
    public async Task Rows_come_from_the_gathered_bundle_and_start_collapsed_with_turns_off()
    {
        var (vm, send, source, _) = Make();
        vm.Start();
        Assert.Equal(FeedbackSendState.Gathering, send.State);
        await Finish(source.Calls[0].Done, t => t.SetResult(new FeedbackGatherResult.Ready(Bundle("Versions", "Screenshot"))));
        Assert.Equal(new[] { "Versions", "Screenshot" }, vm.Rows.Select(r => r.Label));
        Assert.False(vm.Rows[0].Optional);
        Assert.True(vm.Rows[1].Optional);
        Assert.False(vm.RowsExpanded);
        Assert.False(vm.IncludeSessionTurns);
        Assert.Null(source.Calls[0].Request.SessionTurnsFor);
        Assert.Equal("Include the last turns of \"Build fix\"", vm.TurnsLabel);
        Assert.Equal(FeedbackSendState.Ready, send.State);
    }

    [Fact]
    public async Task Turning_on_turns_or_editing_the_note_gathers_again_with_the_new_request()
    {
        var (vm, _, source, _) = Make();
        vm.Start();
        vm.IncludeSessionTurns = true;
        vm.Note = "it broke";
        Assert.Equal(3, source.Calls.Count);
        Assert.Equal("s1", source.Calls[1].Request.SessionTurnsFor);
        Assert.Equal("it broke", source.Calls[2].Request.Note);
        Assert.Equal("s1", source.Calls[2].Request.SessionTurnsFor);
    }

    [Fact]
    public async Task A_gather_that_finishes_after_a_newer_request_is_discarded()
    {
        var (vm, send, source, _) = Make();
        vm.Start();
        vm.Note = "newer";
        await Finish(source.Calls[0].Done, t => t.SetResult(new FeedbackGatherResult.Ready(Bundle("Old"))));
        Assert.Empty(vm.Rows);
        Assert.Equal(FeedbackSendState.Gathering, send.State);
        await Finish(source.Calls[1].Done, t => t.SetResult(new FeedbackGatherResult.Ready(Bundle("New"))));
        Assert.Equal("New", Assert.Single(vm.Rows).Label);
        Assert.Equal(FeedbackSendState.Ready, send.State);
    }

    [Fact]
    public async Task A_gather_that_finishes_after_close_changes_nothing()
    {
        var (vm, send, source, _) = Make();
        vm.Start();
        vm.Close();
        await Finish(source.Calls[0].Done, t => t.SetResult(new FeedbackGatherResult.Ready(Bundle("Late"))));
        Assert.Empty(vm.Rows);
        Assert.False(send.CanSend);
        Assert.True(send.IsClosed);
    }

    [Fact]
    public async Task Edits_after_the_report_is_sent_do_not_gather_or_change_the_rows()
    {
        var (vm, send, source, _) = Make();
        vm.Start();
        await Finish(source.Calls[0].Done, t => t.SetResult(new FeedbackGatherResult.Ready(Bundle("Versions"))));
        await send.SendAsync();
        vm.Note = "too late";
        Assert.Single(source.Calls);
        Assert.Equal("Versions", Assert.Single(vm.Rows).Label);
    }

    [Theory]
    [InlineData(FeedbackRefusalReason.BundleTooLarge, FeedbackSendState.TooLarge)]
    [InlineData(FeedbackRefusalReason.NoteTooLong, FeedbackSendState.Refused)]
    [InlineData(FeedbackRefusalReason.SessionUnavailable, FeedbackSendState.Refused)]
    public async Task A_refused_gather_shows_through_the_send_states_and_cannot_send(FeedbackRefusalReason reason, FeedbackSendState state)
    {
        var (vm, send, source, sender) = Make();
        vm.Start();
        await Finish(source.Calls[0].Done, t => t.SetResult(new FeedbackGatherResult.Refused(reason)));
        Assert.Equal(state, send.State);
        Assert.NotEmpty(send.Message);
        Assert.False(send.CanSend);
        Assert.Equal(0, sender.Calls);
    }

    [Fact]
    public async Task A_failed_gather_fails_the_send_view_model_and_nothing_sends_until_asked()
    {
        var (vm, send, source, sender) = Make();
        vm.Start();
        await Finish(source.Calls[0].Done, t => t.SetException(new InvalidOperationException("boom")));
        Assert.Equal(FeedbackSendState.Failed, send.State);
        Assert.DoesNotContain("boom", send.Message);
        Assert.Equal(0, sender.Calls);

        vm.Note = "again";
        await Finish(source.Calls[1].Done, t => t.SetResult(new FeedbackGatherResult.Ready(Bundle("Versions"))));
        Assert.Equal(0, sender.Calls);
        await send.SendAsync();
        Assert.Equal(1, sender.Calls);
    }
}
