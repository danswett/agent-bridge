using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using Xunit;

public sealed partial class ReplyFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_successful_clear_relinquishes_ownership_even_if_the_person_retypes_the_same_prefix(bool clearOnly)
    {
        var clears = 0;
        injector.Result = new(InjectionStatus.Interrupted, "partial native write") { TextWritten = 5, EnterWritten = false };
        injector.Sent = i =>
        {
            var text = injector.Plans[^1].Plan.Sends[i].Text;
            if (text == "agent report")
                injector.Screen = _ => clearOnly ? ["Allow this command?", "1. Yes"] : CopilotBox("agent");
            if (text == "\u0005\u0015")
            {
                clears++;
                injector.Screen = _ => CopilotBox("agent");
                injector.Result = new(InjectionStatus.Typed, "ok");
            }
        };
        var replier = Replier();
        Assert.Equal(AnswerOutcome.Unconfirmed, (await replier.ReplyAsync(new()
        { Id = Id, Request = Guid.NewGuid().ToString(), Text = "agent report" })).Outcome);
        injector.Screen = _ => CopilotBox("agent");
        var owner = await replier.ReplyAsync(new()
        { Id = Id, Request = Guid.NewGuid().ToString(), Text = "owner reply" });
        Assert.Equal(1, clears);
        Assert.Equal(AnswerOutcome.Refused, owner.Outcome);
        Assert.All(injector.Plans.Skip(1), p => Assert.All(p.Plan.Sends, s => Assert.False(s.Enter)));
        Assert.Contains("agent", (await injector.ReadScreenAsync(100)).Rows![3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queues_are_bounded_and_cancelled_or_expired_work_never_types(bool cancel)
    {
        Fixture.Append(events, Copilot.TurnStart(second++));
        await source.RefreshAsync();
        using var cancellation = new CancellationTokenSource();
        var replier = Replier(capacity: 1, lifetime: TimeSpan.FromMilliseconds(400));
        var request = new ReplyRequest { Id = Id, Request = Guid.NewGuid().ToString(), Text = "queued report" };
        var first = replier.ReplyAsync(request, cancellationToken: cancellation.Token);
        await Task.Delay(100);
        var same = replier.ReplyAsync(request);
        var retry = replier.ReplyAsync(request with { Request = Guid.NewGuid().ToString() });
        var overflow = await replier.ReplyAsync(request with { Request = Guid.NewGuid().ToString(), Text = "overflow" }).WaitAsync(Patience);
        Assert.Equal(AnswerOutcome.Busy, overflow.Outcome);
        if (cancel) cancellation.Cancel();
        var result = await first.WaitAsync(Patience);
        Assert.Contains(cancel ? "cancelled" : "expired", result.Reason);
        Assert.DoesNotContain(cancel ? "expired" : "cancelled", result.Reason);
        Assert.Equal(result, await same.WaitAsync(Patience));
        Assert.Equal(result, await retry.WaitAsync(Patience));
        Fixture.Append(events, Copilot.TurnEnd(second++));
        await source.RefreshAsync();
        await Task.Delay(100);
        Assert.Empty(injector.Plans);
        Assert.Equal(0, replier.QueuedSessions);
    }
    [Fact]
    public async Task The_next_owner_reply_can_clear_expired_host_text_but_never_revives_its_submission()
    {
        injector.Sent = i => { if (i == 0) injector.Screen = _ => ["Allow this command?", "1. Yes"]; };
        var replier = Replier();
        Assert.Equal(AnswerOutcome.Unconfirmed, (await replier.ReplyAsync(new()
        { Id = Id, Request = Guid.NewGuid().ToString(), Text = "agent report" })).Outcome);
        injector.Screen = _ => CopilotBox("agent report");
        injector.Sent = i =>
        {
            if (injector.Plans[^1].Plan.Sends[i].Text == "\u0005\u0015") injector.Screen = _ => CopilotIdle;
        };
        Assert.Equal(AnswerOutcome.Delivered, (await replier.ReplyAsync(new()
        { Id = Id, Request = Guid.NewGuid().ToString(), Text = "owner reply" })).Outcome);
        Assert.False(injector.Plans[1].Plan.Sends.Single().Enter);
        Assert.Equal("owner reply", injector.Plans[2].Plan.Sends[0].Text);
    }

    [Fact]
    public async Task New_id_retries_of_queued_work_share_one_delivery_and_telemetry_never_logs_words()
    {
        Fixture.Append(events, Copilot.TurnStart(second++));
        await source.RefreshAsync();
        var logs = new List<string>();
        var replier = Replier(log: line => { lock (logs) logs.Add(line); });
        var request = new ReplyRequest { Id = Id, Request = Guid.NewGuid().ToString(), Text = "private report" };
        var first = replier.ReplyAsync(request, "private peer");
        await Task.Delay(100);
        var retry = replier.ReplyAsync(request with { Request = Guid.NewGuid().ToString() }, "private peer");
        await HostWait.UntilAsync(() => { lock (logs) return logs.Any(l => l.Contains("deduplicated", StringComparison.Ordinal)); }, "the retry admission", Patience);
        Fixture.Append(events, Copilot.TurnEnd(second++));
        await source.RefreshAsync();
        Assert.Equal(AnswerOutcome.Delivered, (await first.WaitAsync(Patience)).Outcome);
        Assert.Equal(AnswerOutcome.Delivered, (await retry.WaitAsync(Patience)).Outcome);
        Assert.Single(injector.Plans);
        lock (logs)
        {
            Assert.Contains(logs, line => line.Contains("queued", StringComparison.Ordinal));
            Assert.DoesNotContain(logs, line => line.Contains("private", StringComparison.Ordinal));
        }

    }

    [Fact]
    public async Task A_late_identical_record_does_not_suppress_the_current_delivery_Enter()
    {
        var fullBox = CopilotBox("report");
        injector.Sent = i =>
        {
            if (i != 0 || injector.Plans.Count != 1) return;
            Fixture.Append(events, Prompt("report"));
            injector.Screen = _ => [.. fullBox.Take(4), "\u2503", .. fullBox.Skip(4)];
        };
        injector.OnInject = _ => { Fixture.Append(events, Prompt("report")); injector.Screen = _ => CopilotIdle; };
        Assert.Equal(AnswerOutcome.Delivered, (await Replier().ReplyAsync(new()
        { Id = Id, Request = Guid.NewGuid().ToString(), Text = "report" })).Outcome);
        Assert.Equal(2, injector.Plans.Count);
        Assert.True(injector.Plans[1].Plan.Sends.Single().Enter);
    }

    [Fact]
    public async Task A_receipt_never_denies_sending_and_then_submits_automatically_afterwards()
    {
        injector.Sent = i => { if (i == 0) injector.Screen = _ => ["Allow this command?", "1. Yes"]; };
        var replier = Replier();
        var request = new ReplyRequest { Id = Id, Request = Guid.NewGuid().ToString(), Text = "report" };
        var result = await replier.ReplyAsync(request);
        Assert.Equal(AnswerOutcome.Unconfirmed, result.Outcome);
        Assert.Contains("uncertain", result.Reason);
        injector.Sent = null;
        injector.Screen = _ => CopilotBox("report");
        await Task.Delay(200);
        Assert.Single(injector.Plans);
        Assert.Equal(result, await replier.ReplyAsync(request with { Request = Guid.NewGuid().ToString() }));
    }

    [Fact]
    public async Task Recovery_keeps_the_delivery_admission_until_its_last_possible_key()
    {
        injector.Sent = i => { if (i == 0) injector.Screen = _ => ["Allow this command?", "1. Yes"]; };
        var delivery = Replier(submitPatience: TimeSpan.FromSeconds(2)).ReplyAsync(new()
        { Id = Id, Request = Guid.NewGuid().ToString(), Text = "report" });
        await HostWait.UntilAsync(() => injector.Plans.Count == 1, "the paste", Patience);
        var stop = gate.PrepareStopAsync(TimeSpan.FromSeconds(4));
        await Task.Delay(250);
        Assert.False(stop.IsCompleted);
        injector.Sent = null;
        injector.OnInject = _ => { Fixture.Append(events, Prompt("report")); injector.Screen = _ => CopilotIdle; };
        injector.Screen = _ => CopilotBox("report");
        Assert.Equal(AnswerOutcome.Delivered, (await delivery.WaitAsync(Patience)).Outcome);
        using var quiet = await stop.WaitAsync(Patience);
        Assert.NotNull(quiet);
        var count = injector.Plans.Count;
        await Task.Delay(100);
        Assert.Equal(count, injector.Plans.Count);
    }
}
