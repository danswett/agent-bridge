using AgentBridge.Protocol;
using Xunit;

public class CopilotTests
{
    const string Id = "44444444-4444-4444-8444-444444444444";

    sealed class Session_ : IAsyncDisposable
    {
        public readonly Fixture F = new();
        public readonly string Events;
        public readonly AgentBridge.Host.Core.SessionSource Source;

        public Session_()
        {
            Events = F.CopilotSession(Id, 100);
            F.Processes.Run(AgentKind.Copilot, 100);
            Source = F.Source();
        }

        public async Task<Session> After(params string[] lines)
        {
            Fixture.Append(Events, lines);
            await Source.RefreshAsync();
            return Source.Get(Id)!;
        }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            F.Dispose();
        }
    }

    static readonly object Schema = new
    {
        message = "Which one?",
        requestedSchema = new
        {
            properties = new { pick = new { type = "string", title = "Pick", oneOf = new[] { new { @const = "a", title = "Option A (Recommended)" }, new { @const = "b", title = "Option B" } } } },
            required = new[] { "pick" },
        },
    };

    [Fact]
    public async Task A_prompt_makes_it_working_tool_rounds_keep_it_working_and_a_turn_without_tool_requests_ends_it_idle()
    {
        await using var s = new Session_();

        var working = await s.After(Copilot.User("do it", 1), Copilot.TurnStart(2), Copilot.Assistant("", 3, tools: ["c1"]),
            Copilot.ToolStart("c1", "powershell", new { command = "Get-Date\nmore", description = "Read the date" }, 3));
        Assert.Equal(SessionStatus.Working, working.Status);
        Assert.Equal(Fixture.T0.AddSeconds(1), working.StatusSince);
        Assert.Equal("c1", working.Activity.CurrentTool!.Id);
        Assert.Equal("Read the date", working.Activity.CurrentTool.Summary);

        var between = await s.After(Copilot.ToolDone("c1", 4), Copilot.TurnEnd(5));
        Assert.Equal(SessionStatus.Working, between.Status);
        Assert.Equal(Fixture.T0.AddSeconds(1), between.StatusSince);
        Assert.Null(between.Activity.CurrentTool);

        var idle = await s.After(Copilot.TurnStart(6), Copilot.Assistant("all done", 7, model: "model-b", reasoning: "thinking it over"), Copilot.TurnEnd(8));
        Assert.Equal(SessionStatus.Idle, idle.Status);
        Assert.Equal(Fixture.T0.AddSeconds(8), idle.StatusSince);
        Assert.Equal(Fixture.T0.AddSeconds(8), idle.UpdatedAt);
        Assert.Equal("all done", idle.Activity.Response);
        Assert.Equal("thinking it over", idle.Activity.Reasoning);
        Assert.Equal("do it", idle.Activity.UserMessage);
        Assert.Equal("model-b", idle.Model);
        Assert.Equal(1, idle.Activity.TurnCount);
        var tool = Assert.Single(idle.Activity.History!);
        Assert.Equal(ToolStatus.Done, tool.Status);
        Assert.Equal(Fixture.T0.AddSeconds(4), tool.EndedAt);
    }

    [Fact]
    public async Task A_whole_turn_between_two_reads_still_moves_statusSince_to_when_the_new_idle_began()
    {
        await using var s = new Session_();

        await s.After(Copilot.User("one", 1), Copilot.TurnStart(2), Copilot.Assistant("first", 3), Copilot.TurnEnd(4));
        var second = await s.After(Copilot.User("two", 10), Copilot.TurnStart(11), Copilot.Assistant("second", 12), Copilot.TurnEnd(14));

        Assert.Equal(SessionStatus.Idle, second.Status);
        Assert.Equal(Fixture.T0.AddSeconds(14), second.StatusSince);
    }

    [Fact]
    public async Task A_pending_ask_user_makes_it_waiting_until_the_human_response_is_recorded()
    {
        await using var s = new Session_();

        var waiting = await s.After(Copilot.User("ask me", 1), Copilot.TurnStart(2), Copilot.Assistant("", 3, tools: ["q1"]),
            Copilot.ToolStart("q1", "ask_user", Schema, 3));
        Assert.Equal(SessionStatus.Waiting, waiting.Status);
        Assert.Equal(Fixture.T0.AddSeconds(3), waiting.StatusSince);
        var q = waiting.Question!;
        Assert.Equal("q1", q.Id);
        Assert.Equal(QuestionKind.Form, q.Kind);
        Assert.Equal("Which one?", q.Message);
        Assert.Equal(Fixture.T0.AddSeconds(3), q.AskedAt);
        var field = Assert.Single(q.Fields);
        Assert.Equal(FieldType.Choice, field.Type);
        Assert.Equal([new Option("a", "Option A (Recommended)"), new Option("b", "Option B")], field.Options!);

        var answered = await s.After(Copilot.Answered("q1", 9));
        Assert.Null(answered.Question);
        Assert.Equal(SessionStatus.Working, answered.Status);
        Assert.Equal(Fixture.T0.AddSeconds(9), answered.StatusSince);
    }

    [Fact]
    public async Task A_pending_ask_user_is_also_cleared_by_its_tool_completing()
    {
        await using var s = new Session_();

        await s.After(Copilot.User("ask me", 1), Copilot.ToolStart("q1", "ask_user", Schema, 3));
        var done = await s.After(Copilot.ToolDone("q1", 4));

        Assert.Null(done.Question);
        Assert.Equal(SessionStatus.Working, done.Status);
    }

    [Fact]
    public async Task An_abort_ends_the_turn_and_marks_the_tool_it_interrupted_failed()
    {
        await using var s = new Session_();

        var s1 = await s.After(Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.ToolStart("c1", "powershell", new { command = "sleep 100" }, 3),
            Copilot.Event("abort", new { reason = "user initiated" }, 4));

        Assert.Equal(SessionStatus.Idle, s1.Status);
        Assert.Null(s1.Activity.CurrentTool);
        Assert.Equal(ToolStatus.Failed, Assert.Single(s1.Activity.History!).Status);
        Assert.Equal(1, s1.Activity.TurnCount);
    }

    [Fact]
    public async Task A_failed_tool_is_recorded_as_failed()
    {
        await using var s = new Session_();

        var s1 = await s.After(Copilot.User("go", 1), Copilot.ToolStart("c1", "view", new { path = "/nope" }, 2), Copilot.ToolDone("c1", 3, success: false));

        Assert.Equal(ToolStatus.Failed, Assert.Single(s1.Activity.History!).Status);
    }

    [Fact]
    public async Task A_subagents_turns_and_words_are_not_the_sessions_but_its_tool_calls_appear_in_the_history()
    {
        await using var s = new Session_();
        const string sub = "agent-1";

        var s1 = await s.After(Copilot.User("delegate", 1), Copilot.TurnStart(2), Copilot.Assistant("handing off", 3, tools: ["t1"]),
            Copilot.ToolStart("t1", "task", new { description = "Investigate", prompt = "long prompt" }, 3),
            Copilot.TurnStart(4, sub), Copilot.User("subagent task prompt", 4, sub), Copilot.Assistant("sub reply", 5, model: "sub-model", agentId: sub),
            Copilot.ToolStart("s1", "powershell", new { command = "ls" }, 5, sub), Copilot.TurnEnd(6, sub),
            Copilot.Event("session.model_change", new { newModel = "sub-model-2" }, 6, sub));

        Assert.Equal(SessionStatus.Working, s1.Status);
        Assert.Equal("handing off", s1.Activity.Response);
        Assert.Equal("delegate", s1.Activity.UserMessage);
        Assert.Equal("fixture-model", s1.Model);
        Assert.Equal(["t1", "s1"], s1.Activity.History!.Select(t => t.Id));
        Assert.Equal(0, s1.Activity.TurnCount);
    }

    [Fact]
    public async Task A_permission_request_is_an_approval_question_until_it_is_completed()
    {
        await using var s = new Session_();

        var asking = await s.After(Copilot.User("clean up", 1), Copilot.ToolStart("c9", "powershell", new { command = "rm -rf build" }, 2),
            Copilot.Event("permission.requested", new { requestId = "r1", permissionRequest = new { kind = "shell", toolCallId = "c9", toolTitle = "Run a command", args = new { command = "rm -rf build" } } }, 3));
        Assert.Equal(SessionStatus.Waiting, asking.Status);
        var q = asking.Question!;
        Assert.Equal("c9", q.Id);
        Assert.Equal(QuestionKind.Approval, q.Kind);
        Assert.Contains("rm -rf build", q.Message);
        Assert.Equal(["approve", "deny"], Assert.Single(q.Fields).Options!.Select(o => o.Value));

        var granted = await s.After(Copilot.Event("permission.completed", new { requestId = "r1", toolCallId = "c9", result = new { kind = "approved" } }, 4));
        Assert.Null(granted.Question);
        Assert.Equal(SessionStatus.Working, granted.Status);
    }

    [Fact]
    public async Task Only_finished_compactions_are_counted_and_the_model_follows_model_changes()
    {
        await using var s = new Session_();

        var s1 = await s.After(Copilot.Event("session.start", new { sessionId = Id, selectedModel = "start-model" }, 0),
            Copilot.Event("session.compaction_start", new { }, 1), Copilot.Event("session.compaction_complete", new { success = false }, 2),
            Copilot.Event("session.compaction_complete", new { success = true }, 3), Copilot.Event("session.model_change", new { newModel = "picked-model" }, 4));

        Assert.Equal(1, s1.Activity.Compactions);
        Assert.Equal("picked-model", s1.Model);
    }

    [Fact]
    public async Task Without_a_workspace_name_the_title_is_the_first_prompt_on_one_line()
    {
        using var f = new Fixture();
        var events = f.CopilotSession(Id, 100, name: null);
        Fixture.Append(events, Copilot.User("  fix the\n\nbuild   please ", 1), Copilot.User("second", 2));
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Equal("fix the build please", source.Get(Id)!.Title);
    }

    [Fact]
    public async Task Malformed_and_unknown_lines_are_skipped_without_losing_the_lines_around_them()
    {
        await using var s = new Session_();

        var s1 = await s.After(Copilot.User("one", 1), "{not json", "[1,2]", Copilot.Event("future.event", new { x = 1 }, 2),
            "{\"type\":\"assistant.message\",\"data\":{\"content\":42},\"timestamp\":\"2026-10-28T04:00:03.000Z\"}", Copilot.Assistant("two", 4));

        Assert.Equal("two", s1.Activity.Response);
        Assert.Equal("one", s1.Activity.UserMessage);
    }
}
