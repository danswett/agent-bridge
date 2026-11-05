using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.App.Core;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

// How far an answer or a reply has got, where the host says (sessions.progress, #598, #600), and
// the timing marks the app keeps of each in its log, against a host on a real pipe.
[Trait("Category", "Network")]
public sealed class DeliveryProgressTests : IAsyncLifetime
{
    readonly FakeHost host = new();
    HostConnection connection = null!;
    Deliveries deliveries = null!;
    readonly List<string> log = [];
    // Each stage an entry showed, in order, as Changed told it.
    readonly List<string?> stages = [];

    async Task StartAsync(bool progress)
    {
        if (progress) { host.Capabilities.Add(Capabilities.SessionsProgress); }
        host.Sessions.Add(Make.Session("s1"));
        host.Start();
        connection = host.Connect(log: line => { lock (log) { log.Add(line); } });
        deliveries = new Deliveries(connection, new LockedDispatcher()) { RetryDelay = TimeSpan.FromMilliseconds(100) };
        deliveries.Changed += e => { lock (stages) { if (stages.Count == 0 || stages[^1] != e.Stage) { stages.Add(e.Stage); } } };
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "connected");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (connection is not null) { await connection.DisposeAsync(); }
        await host.DisposeAsync();
    }

    string? Logged(string request)
    {
        lock (log) { return log.LastOrDefault(l => l.Contains(request[..8], StringComparison.Ordinal)); }
    }

    [Fact]
    public async Task An_answer_to_a_host_offering_progress_shows_Sent_then_Passed_to_the_agent_then_its_outcome_and_logs_its_timings()
    {
        await StartAsync(progress: true);
        var typed = new TaskCompletionSource();
        var confirmed = new TaskCompletionSource();
        host.OnAsync(Ops.SessionAnswer, async _ =>
        {
            var served = ServedRequest.Current!;
            served.Progress(new DeliveryProgress(DeliveryProgress.Received));
            await FakeHost.Until(typed.Task);
            served.Progress(new DeliveryProgress(DeliveryProgress.Typed, Working: false));
            await FakeHost.Until(confirmed.Task);
            return new AnswerResult(AnswerOutcome.Delivered) { Timings = new DeliveryTimings { Typed = 40, Confirmed = 90 } };
        });

        var entry = deliveries.Submit("answer|s1|q1", Ops.SessionAnswer, r => new AnswerRequest { Id = "s1", Question = "q1", Request = r, Text = "yes" }, o => o != "busy");
        await Wait.UntilAsync(() => deliveries.Get(entry.Key)!.Stage == DeliveryProgress.Received, "Sent");
        Assert.Equal("Answer sent", QuestionViewModel.Describe(deliveries.Get(entry.Key)!, null).Speech);
        typed.SetResult();
        await Wait.UntilAsync(() => deliveries.Get(entry.Key)!.Stage == DeliveryProgress.Typed, "Typed");
        Assert.Equal("Answer passed to the agent", QuestionViewModel.Describe(deliveries.Get(entry.Key)!, null).Speech);
        Assert.Equal(DeliveryState.Sending, deliveries.Get(entry.Key)!.State);
        confirmed.SetResult();
        await Wait.UntilAsync(() => deliveries.Get(entry.Key)!.State == DeliveryState.Done, "an outcome");

        Assert.Equal("delivered", deliveries.Get(entry.Key)!.Outcome);
        Assert.True(Assert.Single(host.Sent(Ops.SessionAnswer)).GetProperty("progress").GetBoolean());
        lock (stages) { Assert.Equal(new string?[] { null, DeliveryProgress.Received, DeliveryProgress.Typed }, stages); }
        // One line in the app's log, in ms from the tap, with the host's own timings beside them.
        await Wait.UntilAsync(() => Logged(entry.Request) is not null, "the timing line");
        Assert.Matches(new Regex($@"^answer {entry.Request[..8]} delivered: sent \d+ ms, received \d+ ms, typed \d+ ms, result \d+ ms; host: typed 40 ms, confirmed 90 ms$"), Logged(entry.Request)!);
    }

    [Fact]
    public async Task A_reply_typed_into_a_working_session_is_Up_next_and_a_stage_behind_it_changes_nothing_not_even_its_timing_marks()
    {
        await StartAsync(progress: true);
        host.OnAsync(Ops.SessionReply, _ =>
        {
            var served = ServedRequest.Current!;
            // A repeat replays the stages so far: typed first here, then an earlier one.
            served.Progress(new DeliveryProgress(DeliveryProgress.Typed, Working: true));
            served.Progress(new DeliveryProgress(DeliveryProgress.Received));
            // From a newer host: read as nothing.
            served.Progress(new DeliveryProgress("pondering"));
            return Task.FromResult<object?>(new ReplyResult(AnswerOutcome.Delivered));
        });

        var entry = deliveries.Submit("reply|s1", Ops.SessionReply, r => new ReplyRequest { Id = "s1", Request = r, Text = "and add a test" }, _ => false, replayAfterRestart: false);
        await Wait.UntilAsync(() => deliveries.Get(entry.Key)!.State == DeliveryState.Done, "an outcome");

        lock (stages) { Assert.Equal(new string?[] { null, Deliveries.Queued }, stages); }
        Assert.Equal("Reply up next, after the agent's current step", ReceiptWords.InFlight(answer: false, deliveries.Get(entry.Key)! with { State = DeliveryState.Sending }, null, stalled: false).Speech);
        // Marked only as the stage moved on: the earlier stage replayed after it adds nothing.
        await Wait.UntilAsync(() => Logged(entry.Request) is not null, "the timing line");
        Assert.Matches(new Regex($@"^reply {entry.Request[..8]} delivered: sent \d+ ms, queued \d+ ms, result \d+ ms$"), Logged(entry.Request)!);
    }

    [Fact]
    public async Task A_host_without_progress_is_asked_for_none_and_a_reply_says_Sending_until_its_outcome()
    {
        await StartAsync(progress: false);
        var outcome = new TaskCompletionSource();
        host.OnAsync(Ops.SessionReply, async _ =>
        {
            await FakeHost.Until(outcome.Task);
            return new ReplyResult(AnswerOutcome.Delivered);
        });

        var entry = deliveries.Submit("reply|s1", Ops.SessionReply, r => new ReplyRequest { Id = "s1", Request = r, Text = "carry on" }, _ => false, replayAfterRestart: false);
        await Wait.UntilAsync(() => host.Sent(Ops.SessionReply).Count == 1, "the reply sent");
        Assert.Equal("Sending reply", ReceiptWords.InFlight(answer: false, deliveries.Get(entry.Key)!, null, stalled: false).Speech);
        outcome.SetResult();
        await Wait.UntilAsync(() => deliveries.Get(entry.Key)!.State == DeliveryState.Done, "an outcome");

        Assert.False(host.Sent(Ops.SessionReply)[0].TryGetProperty("progress", out _));
        lock (stages) { Assert.Null(Assert.Single(stages)); }
        await Wait.UntilAsync(() => Logged(entry.Request) is not null, "the timing line");
        Assert.Matches(new Regex($@"^reply {entry.Request[..8]} delivered: sent \d+ ms, result \d+ ms$"), Logged(entry.Request)!);
    }

    [Fact]
    public async Task An_answer_that_fits_the_host_s_16_KiB_only_without_the_progress_flag_goes_without_it()
    {
        await StartAsync(progress: true);
        host.On(Ops.SessionAnswer, _ => new AnswerResult(AnswerOutcome.Delivered));

        // 8 bytes under the limit as it is: "progress":true would put it 8 over, and the host refuse it.
        var entry = deliveries.Submit("answer|s1|q1", Ops.SessionAnswer, r =>
        {
            var a = new AnswerRequest { Id = "s1", Question = "q1", Request = r, Text = "" };
            return a with { Text = new string('x', Limits.AnswerRequest - 8 - JsonSerializer.SerializeToUtf8Bytes(a, ProtocolJson.Options).Length) };
        }, o => o != "busy");
        await Wait.UntilAsync(() => deliveries.Get(entry.Key)!.State == DeliveryState.Done, "an outcome");

        Assert.Equal("delivered", deliveries.Get(entry.Key)!.Outcome);
        var sent = Assert.Single(host.Sent(Ops.SessionAnswer));
        Assert.Equal(Limits.AnswerRequest - 8, JsonSerializer.SerializeToUtf8Bytes(sent.Deserialize<AnswerRequest>(ProtocolJson.Options), ProtocolJson.Options).Length);
        Assert.False(sent.TryGetProperty("progress", out _));
    }

    [Fact]
    public async Task A_host_s_outcome_or_timing_name_with_a_line_break_cannot_forge_a_line_in_the_app_s_log()
    {
        await StartAsync(progress: true);
        host.OnAsync(Ops.SessionReply, _ => Task.FromResult<object?>(new Dictionary<string, object>
        {
            ["outcome"] = "delivered\nFORGED LOG ENTRY",
            ["timings"] = new Dictionary<string, object> { ["typed\nFORGED LOG ENTRY"] = 5, ["typed"] = 7, ["bogus"] = 9 },
        }));

        var entry = deliveries.Submit("reply|s1", Ops.SessionReply, r => new ReplyRequest { Id = "s1", Request = r, Text = "go on" }, _ => false, replayAfterRestart: false);
        await Wait.UntilAsync(() => deliveries.Get(entry.Key)!.State == DeliveryState.Done, "an outcome");
        await Wait.UntilAsync(() => Logged(entry.Request) is not null, "the timing line");

        // Only what the protocol names: an outcome it doesn't is "other", and only its timings' names.
        Assert.Matches(new Regex($@"^reply {entry.Request[..8]} other: sent \d+ ms, result \d+ ms; host: typed 7 ms$"), Logged(entry.Request)!);
        lock (log) { Assert.DoesNotContain(log, l => l.Contains("FORGED", StringComparison.Ordinal) || l.Contains('\n')); }
    }

    [Fact]
    public async Task Frames_alternating_between_typed_and_up_next_add_one_mark_not_one_each()
    {
        await StartAsync(progress: true);
        host.OnAsync(Ops.SessionReply, _ =>
        {
            var served = ServedRequest.Current!;
            served.Progress(new DeliveryProgress(DeliveryProgress.Typed, Working: false));
            served.Progress(new DeliveryProgress(DeliveryProgress.Typed, Working: true));
            served.Progress(new DeliveryProgress(DeliveryProgress.Typed, Working: false));
            served.Progress(new DeliveryProgress(DeliveryProgress.Typed, Working: true));
            return Task.FromResult<object?>(new ReplyResult(AnswerOutcome.Delivered));
        });

        var entry = deliveries.Submit("reply|s1", Ops.SessionReply, r => new ReplyRequest { Id = "s1", Request = r, Text = "and add a test" }, _ => false, replayAfterRestart: false);
        await Wait.UntilAsync(() => deliveries.Get(entry.Key)!.State == DeliveryState.Done, "an outcome");
        await Wait.UntilAsync(() => Logged(entry.Request) is not null, "the timing line");

        lock (stages) { Assert.Equal(new string?[] { null, DeliveryProgress.Typed }, stages); }
        Assert.Matches(new Regex($@"^reply {entry.Request[..8]} delivered: sent \d+ ms, typed \d+ ms, result \d+ ms$"), Logged(entry.Request)!);
    }

    // #625, Copilot 4478421982: the timing line went to the app's log, its file and its lock, on the
    // UI thread the delivery finished on, so a slow disk held up input and painting.
    [Fact]
    public async Task A_delivery_s_timing_line_never_holds_up_the_UI_thread_while_the_log_is_slow()
    {
        using var disk = new ManualResetEventSlim();
        host.Sessions.Add(Make.Session("s1"));
        host.On(Ops.SessionReply, _ => new ReplyResult(AnswerOutcome.Delivered));
        host.Start();
        connection = host.Connect(log: line =>
        {
            if (line.StartsWith("reply ", StringComparison.Ordinal)) { disk.Wait(TimeSpan.FromSeconds(30)); }
            lock (log) { log.Add(line); }
        });
        deliveries = new Deliveries(connection, new LockedDispatcher()) { RetryDelay = TimeSpan.FromMilliseconds(100) };
        await Wait.UntilAsync(() => connection.State == LinkState.Connected, "connected");

        var entry = deliveries.Submit("reply|s1", Ops.SessionReply, r => new ReplyRequest { Id = "s1", Request = r, Text = "carry on" }, _ => false, replayAfterRestart: false);
        await Wait.UntilAsync(() => deliveries.Get(entry.Key)!.State == DeliveryState.Done, "an outcome");

        // The UI thread takes its next turns while the log still waits for the disk.
        await Task.Delay(200);
        Assert.Null(Logged(entry.Request));
        disk.Set();
        await Wait.UntilAsync(() => Logged(entry.Request) is not null, "the timing line, once the disk answers");
    }

    [Fact]
    public async Task A_reply_given_while_the_host_is_away_is_marked_sent_once_when_it_goes_not_at_each_refused_try()
    {
        host.Capabilities.Add(Capabilities.SessionsProgress);
        host.Sessions.Add(Make.Session("s1"));
        host.On(Ops.SessionReply, _ => new ReplyResult(AnswerOutcome.Delivered));
        // Not running yet: each try is refused before anything is sent.
        connection = host.Connect(log: line => { lock (log) { log.Add(line); } });
        deliveries = new Deliveries(connection, new LockedDispatcher()) { RetryDelay = TimeSpan.FromMilliseconds(100) };

        var entry = deliveries.Submit("reply|s1", Ops.SessionReply, r => new ReplyRequest { Id = "s1", Request = r, Text = "when you can" }, _ => false, replayAfterRestart: false);
        await Wait.UntilAsync(() => deliveries.Get(entry.Key)!.State == DeliveryState.Waiting, "waiting for the host");
        await Task.Delay(300);
        host.Start();
        await Wait.UntilAsync(() => deliveries.Get(entry.Key)!.State == DeliveryState.Done, "an outcome");
        await Wait.UntilAsync(() => Logged(entry.Request) is not null, "the timing line");

        Assert.Matches(new Regex($@"^reply {entry.Request[..8]} delivered: sent \d+ ms, result \d+ ms$"), Logged(entry.Request)!);
    }
}