using AgentBridge.HomeAssistant;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using Xunit;

public sealed class AgentMessageAttributionTests
{
    [Fact]
    public Task Host_armed_reply_card_keeps_the_sending_session_principal() => AssertReply(false);

    [Fact]
    public Task Host_armed_reply_card_cleans_and_bounds_the_sending_session_principal() =>
        AssertReply(false, "\u202eSG\u0001 Sender\r\n\t  " + new string('x', 80), "SG Sender " + new string('x', 54));

    [Fact]
    public Task Host_armed_reply_card_does_not_claim_two_matching_prompts_read_together() => AssertReply(true);

    static async Task AssertReply(bool multiple, string sender = "SG Sender", string expectedPrincipal = "SG Sender")
    {
        using var f = new Fixture();
        var id = Guid.NewGuid().ToString();
        var events = f.CopilotSession(id, 100);
        Fixture.Append(events, Copilot.User("Owner prompt", 1), Copilot.TurnStart(2), Copilot.TurnEnd(3));
        f.Processes.Run(AgentKind.Copilot, 100);
        var drivers = new SessionDrivers();
        await using var source = f.Source(drivers: drivers);
        await source.RefreshAsync();
        var ha = new FakeHomeAssistant();
        await using var connector = new HaConnector(new HaConnectorOptions
        {
            MachineName = "Fixture machine",
            Version = "0.1.0",
            StateFile = Path.Combine(f.Root, "ha-connector.json"),
            Drivers = drivers,
            FieldPinDelay = TimeSpan.Zero,
            SelectDelay = TimeSpan.Zero,
        }, ha, source, () => null, time: f.Time,
            answer: (_, _) => Task.FromResult(new AnswerResult(AnswerOutcome.Delivered)),
            reply: (reply, _) =>
            {
                Fixture.Append(events, Copilot.User(reply.Text!, 10), Copilot.TurnStart(11), Copilot.TurnEnd(12));
                if (multiple) { Fixture.Append(events, Copilot.User(reply.Text!, 20), Copilot.TurnStart(21), Copilot.TurnEnd(22)); }
                return Task.FromResult(new ReplyResult(AnswerOutcome.Delivered));
            });
        await connector.TickAsync();
        connector.Observe(ha.SendFromCard(HaContract.NodeId(id), "Agent message", driver: "agent", rid: "fixture-reply", sender: sender));
        f.Time.Advance(TimeSpan.FromSeconds(1));
        await connector.TickAsync();
        await connector.DeliveredAsync();
        await source.RefreshAsync();

        if (multiple)
        {
            // Matching words cannot decide which of two prompts an agent sent.
            Assert.Null(source.Get(id)!.Activity.UserMessageFrom);
            var history = await source.HistoryAsync(id, null, 20);
            var prompts = history.Turns!.Where(turn => turn.Prompt?.Text == "Agent message").ToList();
            Assert.Equal(2, prompts.Count);
            Assert.All(prompts, turn => Assert.Null(turn.Prompt!.From));
        }
        else
        {
            var live = source.Get(id)!;
            Assert.Equal(UserMessageFrom.Agent, live.Activity.UserMessageFrom);
            Assert.Equal(DriverKind.Agent, live.Driver);
            Assert.Equal(expectedPrincipal, live.DriverPrincipal);
        }
    }
}
