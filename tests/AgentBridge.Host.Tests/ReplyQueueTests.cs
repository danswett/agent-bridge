using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using Xunit;

public sealed partial class ReplyFlowTests
{
    [Fact]
    public async Task Busy_replies_wait_host_side_and_a_later_owner_reply_is_not_refused()
    {
        Fixture.Append(events, Copilot.TurnStart(second++));
        await source.RefreshAsync();
        var replier = Replier();
        using var stop = new CancellationTokenSource();
        replier.Stopping = stop.Token;
        try
        {
            var agent = replier.ReplyAsync(new() { Id = Id, Request = Guid.NewGuid().ToString(), Text = "agent report" });
            await Task.Delay(150);
            Assert.Empty(injector.Plans);
            var owner = replier.ReplyAsync(new() { Id = Id, Request = Guid.NewGuid().ToString(), Text = "owner reply" });
            await Task.Delay(150);
            Assert.Empty(injector.Plans);
            Fixture.Append(events, Copilot.TurnEnd(second++));
            await source.RefreshAsync();

            Assert.Equal(AnswerOutcome.Delivered, (await agent.WaitAsync(Patience)).Outcome);
            Assert.Equal(AnswerOutcome.Delivered, (await owner.WaitAsync(Patience)).Outcome);
            Assert.Equal(["agent report", "owner reply"], injector.Plans.Select(p => p.Plan.Sends[0].Text));
        }
        finally { stop.Cancel(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_wrapped_reply_is_submitted_from_its_new_box_without_retyping(bool mixedWraps)
    {
        var words = mixedWraps ? string.Concat(Enumerable.Repeat("hello world ", 57))[..674] : new string('x', 674);
        injector.Sent = i =>
        {
            if (i == 0)
                injector.Screen = _ => [.. CopilotIdle.Take(3), "\u2503 " + words[..200],
                    "\u2503 " + words[200..336], "\u2503 " + words[336..], CopilotIdle[4], " Interactive \u00B7 @ files \u00B7 # issues"];
        };
        injector.OnInject = _ => { Fixture.Append(events, Prompt(words)); injector.Screen = _ => CopilotIdle; };
        var replier = Replier();
        using var stop = new CancellationTokenSource();
        replier.Stopping = stop.Token;
        try
        {
            var result = await replier.ReplyAsync(new() { Id = Id, Request = Guid.NewGuid().ToString(), Text = words }).WaitAsync(Patience);

            Assert.Equal(AnswerOutcome.Delivered, result.Outcome);
            Assert.Single(injector.Plans, p => p.Plan.Sends.Any(s => s.Text.Length > 0));
            Assert.Contains(injector.Plans, p => p.Plan.Sends.All(s => s.Text.Length == 0) && p.Plan.Sends.Any(s => s.Enter));
        }
        finally { stop.Cancel(); }
    }

    [Fact]
    public async Task A_pending_paste_never_submits_a_dialog_and_the_owner_waits_for_its_safe_recovery()
    {
        injector.Sent = i =>
        {
            if (i == 0) injector.Screen = _ => ["Allow this command?", " 1. Yes", " 2. No"];
        };
        var replier = Replier();
        var agent = await replier.ReplyAsync(new() { Id = Id, Request = Guid.NewGuid().ToString(), Text = "agent report" });
        Assert.Equal(AnswerOutcome.Unconfirmed, agent.Outcome);
        var owner = replier.ReplyAsync(new() { Id = Id, Request = Guid.NewGuid().ToString(), Text = "owner reply" });
        await Task.Delay(150);
        Assert.False(owner.IsCompleted);
        Assert.Single(injector.Plans);

        injector.Sent = null;
        injector.OnInject = plan =>
        {
            Fixture.Append(events, Prompt(plan.Sends[0].Text.Length == 0 ? "agent report" : plan.Sends[0].Text));
            injector.Screen = _ => CopilotIdle;
        };
        injector.Screen = _ => CopilotBox("agent report");

        Assert.Equal(AnswerOutcome.Delivered, (await owner.WaitAsync(Patience)).Outcome);
        Assert.Equal(3, injector.Plans.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_partial_native_write_is_cleared_without_submitting_and_does_not_block_the_owner(bool workerThrows)
    {
        injector.Result = new(AgentBridge.Host.Delivery.InjectionStatus.Interrupted, "the injector failed while typing (partial error 8)");
        injector.Sent = i =>
        {
            var text = injector.Plans[^1].Plan.Sends[i].Text;
            if (text == "agent report") injector.Screen = _ => CopilotBox("agent");
            if (text == "\u0005\u0015")
            {
                injector.Screen = _ => CopilotIdle;
                injector.Result = new(AgentBridge.Host.Delivery.InjectionStatus.Typed, "ok");
            }
        };
        if (workerThrows) injector.Holding = () =>
        {
            injector.Holding = null;
            injector.Screen = _ => CopilotBox("agent");
            throw new AgentBridge.Host.Delivery.InjectorWorkerException(new IOException("worker failed"), Task.CompletedTask);
        };
        var replier = Replier();
        Assert.Equal(AnswerOutcome.Unconfirmed, (await replier.ReplyAsync(new()
            { Id = Id, Request = Guid.NewGuid().ToString(), Text = "agent report" })).Outcome);

        var owner = await replier.ReplyAsync(new()
            { Id = Id, Request = Guid.NewGuid().ToString(), Text = "owner reply" }).WaitAsync(Patience);

        Assert.Equal(AnswerOutcome.Delivered, owner.Outcome);
        Assert.Contains(injector.Plans, p => p.Plan.Sends.Any(s => s.Text == "\u0005\u0015" && !s.Enter));
    }
}
