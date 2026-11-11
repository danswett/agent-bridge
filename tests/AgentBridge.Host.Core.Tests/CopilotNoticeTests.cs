using System.Text;
using System.Text.Json;
using AgentBridge.Host.Core.Conversation;
using AgentBridge.Host.Core.Parsing;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public class CopilotNoticeTests
{
    static string Notice(int second, string delivery = "idle") => Copilot.Event("user.message",
        new { content = "Synthetic completion notice.", source = "system", delivery }, second);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Notification_replies_never_replace_the_owner_answer_in_live_or_skimmed_activity(bool skim)
    {
        var state = new SessionState();
        var parser = new CopilotParser(state, TimeProvider.System);
        void Read(string line)
        {
            if (skim) { parser.Skim(Encoding.UTF8.GetBytes(line)); }
            else { parser.Parse(Encoding.UTF8.GetBytes(line)); }
        }
        Read(Copilot.User("Owner question.", 1));
        Read(Copilot.TurnStart(2));
        Read(Copilot.Assistant("Owner answer.", 3));
        Read(Copilot.TurnEnd(4));
        var owner = state.ResponseTurn;
        Read(Notice(5));
        Read(Copilot.TurnStart(6));
        Read(Copilot.Assistant("Notice reply.", 7));
        Read(Copilot.Assistant("", 8, tools: ["tool"]));
        Read(Copilot.TurnEnd(9));

        Assert.Equal("Owner answer.", state.OwnResponse);
        Assert.Equal(owner, state.ResponseTurn);
        Read(Copilot.User("Next owner question.", 10));
        Read(Copilot.TurnStart(11));
        Read(Copilot.Assistant("Next owner answer.", 12));
        Assert.Equal("Next owner answer.", state.OwnResponse);
        Assert.Equal(state.TurnId, state.ResponseTurn);
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("steering")]
    public async Task Stored_notifications_are_separate_from_owner_and_scheduled_answers(string delivery)
    {
        using var f = new Fixture();
        var path = f.CopilotSession("55555555-5555-4555-8555-555555555555", 100);
        var owner = Copilot.User("Owner question.", 1);
        var notice = Notice(5, delivery);
        Fixture.Append(path, owner, Copilot.TurnStart(2), Copilot.Assistant("Owner answer.", 3), Copilot.TurnEnd(4),
            notice, Copilot.TurnStart(6), Copilot.Assistant("Notice reply.", 7), Copilot.TurnEnd(8),
            Copilot.Event("user.message", new { content = "Scheduled prompt.", source = "schedule-4" }, 9),
            Copilot.TurnStart(10), Copilot.Assistant("Scheduled answer.", 11));
        var reader = new HistoryReader(new HistoryBudgets(), new byte[32]);
        var page = await Task.Run(() => reader.Read("fixture", AgentKind.Copilot, path, null, 20,
            new HistorySeed(new Dictionary<string, string?>(), new Dictionary<string, string>()), true));

        Assert.Equal(3, page.Turns!.Count);
        Assert.Equal(TurnIds.Of(Encoding.UTF8.GetBytes(owner)), page.Turns[0].Id);
        Assert.Equal(TurnIds.Of(Encoding.UTF8.GetBytes(notice)), page.Turns[1].Id);
        Assert.Equal(["Owner answer.", "Notice reply.", "Scheduled answer."],
            page.Turns.Select(t => t.Parts.Single(p => p.Kind == HistoryPartKind.Response).Text));
        Assert.Equal(UserMessageFrom.Schedule, page.Turns[2].Prompt!.From);
    }

    [Fact]
    public void Queued_owner_waits_for_its_assistant_turn_in_the_sanitized_26_event_order()
    {
        var state = new SessionState();
        var clock = new FakeTimeProvider(Fixture.T0);
        var parser = new CopilotParser(state, clock);
        void Read(string line) => parser.Parse(Encoding.UTF8.GetBytes(line));
        Read(Copilot.User("Earlier owner question.", 1));
        Read(Copilot.Assistant("Earlier owner answer.", 2));
        Read(Notice(3));
        Read(Copilot.TurnStart(4));
        Read(Copilot.Assistant("Older notification reply.", 5, tools: ["tool"]));
        var ownerAnswer = state.ResponseTurn;
        string[] types = ["tool.execution_complete", "assistant.turn_end", "assistant.turn_start", "user.message",
            "model.turn_started", "model.model_call_started", "model.captured_assignment_context", "model.model_call_success",
            "model.message", "model.response", "model.turn_ended", "model.messages_snapshot", "session.usage_record",
            "assistant.message", "tool.execution_start", "hook.start", "hook.end", "tool.execution_complete",
            "assistant.turn_end", "assistant.turn_start", "session.usage_record", "assistant.message",
            "assistant.turn_end", "hook.start", "hook.end", "session.usage_checkpoint"];
        int[] offsets = [0, 147, 148, 186, 192, 192, 1393, 1394, 1394, 1394, 1394, 1395, 8799,
            8809, 8825, 8848, 8849, 11173, 11344, 11345, 16974, 16979, 16983, 17274, 17360, 17577];
        for (var i = 0; i < types.Length; i++)
        {
            clock.SetUtcNow(Fixture.T0.AddSeconds(10).AddMilliseconds(offsets[i]));
            object data = i switch
            {
                3 => new { content = "Queued owner question.", delivery = "steering" },
                13 => new { content = "Previous run progress.", toolRequests = new[] { new { toolCallId = "tool" } } },
                21 => new { content = "Real owner answer." },
                _ => new { },
            };
            Read(JsonSerializer.Serialize(new { type = types[i], id = "fixture-" + i, timestamp = clock.GetUtcNow(), data }));
            if (i is 3 or 13)
            {
                Assert.Equal(ownerAnswer, state.ResponseTurn);
                Assert.NotEqual(state.TurnId, state.ResponseTurn);
                Assert.Equal("Earlier owner answer.", state.OwnResponse);
            }
            if (i == 21)
            {
                Assert.Equal(state.TurnId, state.ResponseTurn);
                Assert.Equal("Real owner answer.", state.OwnResponse);
            }
        }
    }
}
