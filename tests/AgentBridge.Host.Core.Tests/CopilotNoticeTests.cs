using System.Text;
using System.Text.Json;
using AgentBridge.Host.Core.Conversation;
using AgentBridge.Host.Core.Parsing;
using AgentBridge.Host.Core.Transcripts;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public class CopilotNoticeTests
{
    [Theory]
    [InlineData(false, false, "user.message")]
    [InlineData(false, true, "user.message")]
    [InlineData(true, false, "user.message")]
    [InlineData(true, true, "user.message")]
    [InlineData(false, false, "assistant.turn_start")]
    [InlineData(false, true, "assistant.turn_start")]
    [InlineData(true, false, "assistant.turn_start")]
    [InlineData(true, true, "assistant.turn_start")]
    public void An_unreadable_or_dropped_turn_boundary_cannot_reuse_a_completed_notices_reply_target(bool skim, bool dropped, string type)
    {
        var state = new SessionState();
        var parser = new CopilotParser(state, TimeProvider.System);
        void Read(string line)
        {
            if (skim) { parser.Skim(Encoding.UTF8.GetBytes(line)); }
            else { parser.Parse(Encoding.UTF8.GetBytes(line)); }
        }
        foreach (var line in new[] { Copilot.User("Owner question.", 1), Copilot.TurnStart(2),
            Copilot.Assistant("Owner answer.", 3), Copilot.TurnEnd(4), Notice(5), Copilot.TurnStart(6),
            Copilot.Assistant("Completed notice reply.", 7), Copilot.User("Queued owner question.", 8),
            Copilot.TurnEnd(9) }) { Read(line); }
        var completed = state.Notice!.Id;
        var damaged = $"{{\"type\":\"{type}\",\"data\":";
        if (dropped) { ((ILineSink)parser).Dropped(Encoding.UTF8.GetBytes(damaged)); }
        else { Read(damaged); }
        if (type == "user.message") { Read(Copilot.TurnStart(10)); }
        Read(Copilot.Assistant("Reply after the unreadable boundary.", 11));

        Assert.Equal("Owner answer.", state.OwnResponse);
        Assert.NotEqual(completed, state.Notice!.Id);
        Assert.Equal(TurnKind.Unknown, state.Notice.Kind);
        Assert.Equal("Reply after the unreadable boundary.", state.Notice.Parts.Single().Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_turn_error_after_skipped_bytes_stays_a_host_composed_error_not_an_unknown_notice(bool skim)
    {
        var state = new SessionState();
        var parser = new CopilotParser(state, TimeProvider.System);
        parser.Skim(Encoding.UTF8.GetBytes(Copilot.User("Owner question.", 1)));
        parser.Skim(Encoding.UTF8.GetBytes(Copilot.TurnStart(2)));
        ((ILineSink)parser).Skipped();
        foreach (var line in new[]
        {
            Copilot.Event("session.error", new { errorType = "session", statusCode = 429, message = "Synthetic private error." }, 3),
            Copilot.Assistant("\u26A0\uFE0F Turn error: Synthetic private error.", 4),
        })
        {
            if (skim) { parser.Skim(Encoding.UTF8.GetBytes(line)); }
            else { parser.Parse(Encoding.UTF8.GetBytes(line)); }
        }
        Assert.Equal("\u26A0\uFE0F Turn error: Copilot hit a rate limit (429).", state.OwnResponse);
        Assert.Null(state.Notice);
    }

    [Fact]
    public void Skipped_bytes_never_promote_a_reply_with_unknown_ownership_to_the_owner()
    {
        var state = new SessionState();
        var parser = new CopilotParser(state, TimeProvider.System);
        foreach (var line in new[] { Copilot.User("Owner question.", 1), Notice(2), Copilot.TurnStart(3) })
        {
            parser.Skim(Encoding.UTF8.GetBytes(line));
        }
        ((ILineSink)parser).Skipped();
        parser.Parse(Encoding.UTF8.GetBytes(Copilot.Assistant("Unattributed reply.", 4)));
        Assert.Null(state.OwnResponse);
        Assert.Null(state.Response);
        Assert.Equal("Unattributed reply.", state.Notice!.Parts.Single().Text);
        Assert.Equal(TurnKind.Unknown, state.Notice.Kind);
    }

    [Fact]
    public void Live_notice_response_identity_matches_its_stored_part_after_reasoning()
    {
        var state = new SessionState();
        var parser = new CopilotParser(state, TimeProvider.System);
        var notice = Notice(1);
        var reply = Copilot.Assistant("Notice answer.", 3, reasoning: "Synthetic reasoning.");
        foreach (var line in new[] { notice, Copilot.TurnStart(2), reply }) { parser.Parse(Encoding.UTF8.GetBytes(line)); }
        var turn = new TurnBuilder(state.Notice!.Id, Fixture.T0, state.Notice.Prompt);
        turn.Record(TurnIds.Of(Encoding.UTF8.GetBytes(reply)));
        using var doc = JsonDocument.Parse(reply);
        new CopilotHistory().Apply(doc.RootElement, Fixture.T0.AddSeconds(3), turn,
            new HistoryContext(new Dictionary<string, string?>(), new Dictionary<string, string>(), true));
        Assert.Equal(turn.Build(true).Parts.Single(p => p.Kind == HistoryPartKind.Response).Id, state.Notice.Parts.Single().Id);
    }

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
        Assert.Equal(TurnKind.Notification, state.Notice!.Kind);
        Assert.NotEqual(owner, state.Notice.Id);
        Assert.Equal("Notice reply.", state.Notice.Parts.Single().Text);
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
        Assert.Equal([TurnKind.User, TurnKind.Notification, TurnKind.Scheduled], page.Turns.Select(t => t.Kind));
        var newest = reader.Read("fixture", AgentKind.Copilot, path, null, 1,
            new HistorySeed(new Dictionary<string, string?>(), new Dictionary<string, string>()), true);
        var alone = reader.Read("fixture", AgentKind.Copilot, path, newest.Before, 1,
            new HistorySeed(new Dictionary<string, string?>(), new Dictionary<string, string>()), true);
        Assert.Equal(JsonSerializer.Serialize(page.Turns[1], ProtocolJson.Options),
            JsonSerializer.Serialize(alone.Turns!.Single(), ProtocolJson.Options));
    }

    [Fact]
    public void A_second_notice_queued_inside_the_first_does_not_redirect_either_reply_to_the_owner()
    {
        var state = new SessionState();
        var parser = new CopilotParser(state, TimeProvider.System);
        void Read(string line) => parser.Parse(Encoding.UTF8.GetBytes(line));
        Read(Copilot.User("Owner question.", 1));
        Read(Copilot.Assistant("Owner answer.", 2));
        Read(Notice(3));
        Read(Copilot.TurnStart(4));
        var first = state.Notice!.Id;
        Read(Notice(5, "steering"));
        var second = state.Notice!.Id;
        Read(Copilot.Assistant("First notice reply.", 6));
        Assert.Equal(first, state.Notice!.Id);
        Read(Copilot.TurnEnd(7));
        Read(Copilot.TurnStart(8));
        Read(Copilot.Assistant("Second notice reply.", 9));
        Assert.Equal(second, state.Notice!.Id);
        Assert.Equal("Second notice reply.", state.Notice.Parts.Single().Text);
        Assert.Equal("Owner answer.", state.OwnResponse);
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
