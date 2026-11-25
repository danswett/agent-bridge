using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Core.Parsing;
using AgentBridge.Protocol;
using Xunit;

// A Claude question is over when the transcript has a result with its call's id, whichever order the two
// are read in, and a copy of that result with no timestamp (seen live) changes nothing. Each question lists
// its own option descriptions.
public class StaleAskTests
{
    const string Id = "88888888-8888-4888-8888-888888888888";
    const string ToolUse = "toolu_stale_1";

    static JsonObject Input() => Claude.Ask(("Ship it?", false, ["Yes", "No"]));

    static string Asks(string message) => Claude.AssistantMessage(message, 2, "tool_use", Claude.ToolUse(ToolUse, "AskUserQuestion", Input()));

    sealed class Session_ : IAsyncDisposable
    {
        readonly Fixture F = new();
        readonly string Transcript;
        readonly AgentBridge.Host.Core.SessionSource Source;

        public Session_()
        {
            Transcript = F.ClaudeTranscript(Id);
            F.ClaudeRegistration(Id, 900, Transcript);
            F.Processes.Run(AgentKind.Claude, 900);
            Source = F.Source();
        }

        public async Task<Session> After(params string[] lines)
        {
            Fixture.Append(Transcript, lines);
            await Source.RefreshAsync();
            return Source.Get(Id)!;
        }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            F.Dispose();
        }
    }

    [Fact]
    public async Task A_result_read_before_its_call_does_not_leave_the_question_open()
    {
        await using var s = new Session_();
        await s.After(Claude.User("plan it", 1));

        var session = await s.After(Claude.Answered(ToolUse, 2, new Dictionary<string, string> { ["Ship it?"] = "Yes" }),
            Claude.AssistantMessage("msg_1", 3, "tool_use", Claude.ToolUse(ToolUse, "AskUserQuestion", Input())));

        Assert.Null(session.Question);
        Assert.NotEqual(SessionStatus.Waiting, session.Status);
    }

    // Seen on a real transcript: after the answer's result, Claude Code writes the same result again as a
    // record with no timestamp. It must neither reopen the question nor close a newer one.
    static string WithoutTimestamp(string line)
    {
        var node = JsonNode.Parse(line)!.AsObject();
        node.Remove("timestamp");
        return node.ToJsonString();
    }

    [Fact]
    public async Task A_second_result_with_no_timestamp_does_not_reopen_an_answered_question()
    {
        await using var s = new Session_();
        await s.After(Claude.User("plan it", 1), Claude.Assistant(2, "tool_use", Claude.ToolUse(ToolUse, "AskUserQuestion", Input())));
        var answered = Claude.Answered(ToolUse, 3, new Dictionary<string, string> { ["Ship it?"] = "Yes" });

        var session = await s.After(answered, WithoutTimestamp(answered));

        Assert.Null(session.Question);
        Assert.NotEqual(SessionStatus.Waiting, session.Status);
    }

    [Fact]
    public async Task A_newer_question_stays_open_after_an_older_one_and_its_duplicate_result()
    {
        await using var s = new Session_();
        var answered = Claude.Answered(ToolUse, 3, new Dictionary<string, string> { ["Ship it?"] = "Yes" });
        await s.After(Claude.User("plan it", 1), Claude.Assistant(2, "tool_use", Claude.ToolUse(ToolUse, "AskUserQuestion", Input())), answered, WithoutTimestamp(answered));

        var session = await s.After(Claude.Assistant(4, "tool_use", Claude.ToolUse("toolu_newer", "AskUserQuestion", Input())));

        Assert.Equal("toolu_newer", session.Question?.Id);
        Assert.Equal(SessionStatus.Waiting, session.Status);
    }

    [Fact]
    public void Each_questions_option_descriptions_are_listed_under_its_own_question()
    {
        var input = Claude.Ask(("Ship it?", false, ["Yes", "No"]), ("When?", false, ["Now", "Later"]));
        using var doc = JsonDocument.Parse(input.ToJsonString());

        var question = QuestionMapper.FromClaudeAskUserQuestion("c", doc.RootElement, Fixture.T0);

        Assert.Equal("Ship it?\n\n- **Yes**: About Yes\n- **No**: About No\n\nWhen?\n\n- **Now**: About Now\n- **Later**: About Later", question.Message);
    }

    [Fact]
    public void A_question_with_no_descriptions_is_just_its_text()
    {
        using var doc = JsonDocument.Parse("""{"questions":[{"question":"Ship it?","options":[{"label":"Yes"},{"label":"No"}]},{"question":"When?","options":[{"label":"Now","description":"At once"}]}]}""");

        var question = QuestionMapper.FromClaudeAskUserQuestion("c", doc.RootElement, Fixture.T0);

        Assert.Equal("Ship it?\n\nWhen?\n\n- **Now**: At once", question.Message);
    }
}
