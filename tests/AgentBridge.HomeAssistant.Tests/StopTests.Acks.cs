using System.Text.Json;
using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;
using Xunit;

public sealed partial class StopTests
{
    JsonElement EndAck => Detail.GetProperty("end_ack");

    [Theory]
    [InlineData(StopOutcome.Ended, false, "ended")]
    [InlineData(StopOutcome.Ended, true, "ended")]
    [InlineData(StopOutcome.Stale, false, "already_ended")]
    [InlineData(StopOutcome.Busy, false, "ending")]
    [InlineData(StopOutcome.Refused, false, "refused")]
    [InlineData(StopOutcome.Failed, false, "refused")]
    [InlineData(StopOutcome.Confirm, false, "refused")]
    public async Task End_results_acknowledge_the_host_request_without_guessing_from_the_button(
        StopOutcome result, bool forced, string expected)
    {
        outcome = _ => Task.FromResult(new StopResult(result, "host reason")
        {
            Forced = forced, Confirm = result == StopOutcome.Confirm ? "token-1" : null,
        });
        await using var c = await Ready(SessionStatus.Idle);
        var press = ha.Press(SessionEntities.Stop(Node));
        c.Observe(press);
        await Pass(c);
        await c.DeliveredAsync();
        await Pass(c, 0.01);

        Assert.Equal(Assert.Single(stopped).Request, EndAck.GetProperty("rid").GetString());
        Assert.Equal(Node, EndAck.GetProperty("node").GetString());
        Assert.Equal(expected, EndAck.GetProperty("outcome").GetString());
        Assert.Equal("idle", EndAck.GetProperty("state_before").GetString());
        Assert.Equal(forced, EndAck.GetProperty("killed").GetBoolean());
        Assert.Equal("host reason", EndAck.GetProperty("reason").GetString());
        Assert.Equal(TimeSpan.Zero, EndAck.GetProperty("at").GetDateTimeOffset().Offset);
        var button = (await ha.GetStateAsync(SessionEntities.Stop(Node)))!;
        Assert.Equal(press.State.State, button.State);
        Assert.Equal(stopped[0].Request, button.Attributes.GetProperty("last_rid").GetString());
    }

    [Fact]
    public async Task An_end_timeout_is_not_acknowledged_as_ended()
    {
        outcome = _ => throw new TimeoutException();
        await using var c = await Ready(SessionStatus.Idle);
        await Press(c);
        Assert.Equal("timeout", EndAck.GetProperty("outcome").GetString());
        Assert.False(EndAck.GetProperty("killed").GetBoolean());
    }

    [Fact]
    public async Task A_missing_session_is_not_confused_with_a_known_session_already_ended()
    {
        await using var c = await Ready(SessionStatus.Idle);
        ha.AfterRead = entity => { if (entity == SessionEntities.Stop(Node)) { sessions.End(A); } };
        await Press(c);
        Assert.Equal("not_found", EndAck.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Distinct_presses_keep_bounded_correlated_results_across_later_activity()
    {
        outcome = _ => Task.FromResult(new StopResult(StopOutcome.Refused, "not permitted"));
        await using var c = await Ready(SessionStatus.Idle);
        for (var n = 0; n < 10; n++) { await Press(c); }
        Status(SessionStatus.Working, turn: 2);
        await Pass(c);

        var history = Detail.GetProperty("end_ack_history").EnumerateArray().ToArray();
        Assert.Equal(stopped.Skip(2).Select(r => r.Request), history.Select(a => a.GetProperty("rid").GetString()));
        Assert.Equal(10, stopped.Select(r => r.Request).Distinct().Count());
        Assert.All(history, ack => Assert.Equal("refused", ack.GetProperty("outcome").GetString()));
        Assert.Equal(stopped[^1].Request, EndAck.GetProperty("rid").GetString());
        Assert.Equal("Reading files", Detail.GetProperty("response").GetString());
    }

    [Fact]
    public async Task A_press_during_an_end_has_its_own_ack_without_starting_another_end()
    {
        var release = new TaskCompletionSource<StopResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        outcome = _ => release.Task;
        await using var c = await Ready(SessionStatus.Idle);
        try
        {
            c.Observe(ha.Press(SessionEntities.Stop(Node)));
            await Pass(c);
            c.Observe(ha.Press(SessionEntities.Stop(Node)));
            await Pass(c);
            var laterRid = EndAck.GetProperty("rid").GetString();
            Assert.Equal("ending", EndAck.GetProperty("outcome").GetString());
            release.SetResult(new StopResult(StopOutcome.Refused, "cannot end"));
            await c.DeliveredAsync();
            await Pass(c);
            Assert.NotEqual(Assert.Single(stopped).Request, laterRid);
            Assert.Equal(laterRid, (await ha.GetStateAsync(SessionEntities.Stop(Node)))!.Attributes.GetProperty("last_rid").GetString());
            Assert.Equal(2, Detail.GetProperty("end_ack_history").GetArrayLength());
        }
        finally { release.TrySetResult(new StopResult(StopOutcome.Refused)); }
    }
}
