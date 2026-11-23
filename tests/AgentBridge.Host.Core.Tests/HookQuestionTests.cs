using System.Text.Json.Nodes;
using AgentBridge.Protocol;
using Xunit;

// #3838: a Claude AskUserQuestion the transcript has not written yet (Claude Code writes it only once it
// is answered when parallel tool calls follow it in the same message) is known from the claude/ask hook.
// It is shown answer-at-machine and never typed; the transcript's own copy replaces it by tool_use_id.
public class HookQuestionTests
{
    const string Id = "77777777-7777-4777-8777-777777777777";
    const string ToolUse = "toolu_hook_1";

    static JsonObject Input() => Claude.Ask(("Ship it?", false, ["Yes", "No"]));

    sealed class Session_ : IAsyncDisposable
    {
        public readonly Fixture F = new();
        public readonly string Transcript;
        public readonly AgentBridge.Host.Core.SessionSource Source;

        public Session_(string? hookStatus = null, DateTimeOffset? hookAt = null)
        {
            Transcript = F.ClaudeTranscript(Id);
            F.ClaudeRegistration(Id, 900, Transcript, hookStatus, hookAt);
            F.Processes.Run(AgentKind.Claude, 900);
            Source = F.Source();
        }

        public async Task<Session> After(params string[] lines)
        {
            Fixture.Append(Transcript, lines);
            await Source.RefreshAsync();
            return Source.Get(Id)!;
        }

        public async Task<Session> Refresh()
        {
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
    public async Task A_question_only_the_hook_knows_is_shown_waiting_and_answered_at_the_machine()
    {
        await using var s = new Session_();
        await s.After(Claude.User("plan it", 1));
        s.F.ClaudeAskMarker(Id, ToolUse, Input());

        var session = await s.Refresh();

        Assert.Equal(SessionStatus.Waiting, session.Status);
        var question = Assert.IsType<Question>(session.Question);
        Assert.Equal(ToolUse, question.Id);
        Assert.True(question.AnswerAtMachine);
        Assert.Equal("hook", question.AnswerAtMachineReason);
        Assert.Contains("Ship it?", question.Message + string.Join(' ', question.Fields?.Select(f => f.Title) ?? []));
        Assert.Null(session.Attention);
    }

    [Fact]
    public async Task A_session_with_a_hook_question_waiting_is_never_not_responding()
    {
        await using var s = new Session_();
        await s.After(Claude.User("plan it", 1));
        s.F.ClaudeAskMarker(Id, ToolUse, Input());
        await s.Refresh();

        s.F.Time.Advance(TimeSpan.FromMinutes(35));
        var session = await s.Refresh();

        Assert.Equal(SessionStatus.Waiting, session.Status);
        Assert.Null(session.Attention);
        Assert.Equal(ToolUse, session.Question?.Id);
    }

    [Fact]
    public async Task The_transcripts_copy_replaces_the_hook_question_by_id_with_no_duplicate()
    {
        await using var s = new Session_();
        await s.After(Claude.User("plan it", 1));
        s.F.ClaudeAskMarker(Id, ToolUse, Input());
        Assert.True((await s.Refresh()).Question?.AnswerAtMachine);

        var session = await s.After(Claude.Assistant(2, "tool_use", Claude.ToolUse(ToolUse, "AskUserQuestion", Input())));

        var question = Assert.IsType<Question>(session.Question);
        Assert.Equal(ToolUse, question.Id);
        Assert.NotEqual(true, question.AnswerAtMachine);
        Assert.Null(question.AnswerAtMachineReason);
        Assert.Equal(SessionStatus.Waiting, session.Status);
    }

    [Fact]
    public async Task A_question_the_transcript_wrote_and_answered_is_not_shown_again_from_the_hook()
    {
        await using var s = new Session_();
        await s.After(Claude.User("plan it", 1));
        s.F.ClaudeAskMarker(Id, ToolUse, Input());
        Assert.NotNull((await s.Refresh()).Question);

        // Claude Code writes the question and its answer together, late: the marker is then spent.
        var session = await s.After(Claude.Assistant(2, "tool_use", Claude.ToolUse(ToolUse, "AskUserQuestion", Input())),
            Claude.Answered(ToolUse, 3, new Dictionary<string, string> { ["Ship it?"] = "Yes" }));

        Assert.Null(session.Question);
        Assert.NotEqual(SessionStatus.Waiting, session.Status);
    }

    [Fact]
    public async Task A_hook_question_for_a_session_with_a_pending_transcript_question_does_not_replace_it()
    {
        await using var s = new Session_();
        await s.After(Claude.User("plan it", 1), Claude.Assistant(2, "tool_use", Claude.ToolUse("toolu_other", "AskUserQuestion", Input())));
        s.F.ClaudeAskMarker(Id, ToolUse, Input());

        var session = await s.Refresh();

        Assert.Equal("toolu_other", session.Question?.Id);
        Assert.NotEqual(true, session.Question?.AnswerAtMachine);
    }

    [Fact]
    public async Task A_hook_marker_that_is_not_an_AskUserQuestion_input_shows_nothing()
    {
        await using var s = new Session_();
        await s.After(Claude.User("plan it", 1));
        s.F.ClaudeAskMarker(Id, ToolUse, new JsonObject { ["not"] = "a question" });

        var session = await s.Refresh();

        Assert.Null(session.Question);
    }

    // S3: a Notification Claude is blocked on (permission_prompt, elicitation_dialog, agent_needs_input) with no tool running
    // leaves the transcript silent, so the session read Working and, after ten minutes, "Not responding".
    [Fact]
    public async Task A_session_blocked_on_a_notification_with_no_tool_running_is_never_not_responding()
    {
        await using var s = new Session_("waiting", Fixture.T0.AddSeconds(5));
        await s.After(Claude.User("go", 1));

        s.F.Time.Advance(TimeSpan.FromMinutes(35));
        var session = await s.Refresh();

        Assert.Null(session.Attention);
        Assert.Equal(SessionStatus.Working, session.Status);
    }

    [Fact]
    public async Task A_blocking_notification_older_than_the_transcript_does_not_hold_back_not_responding()
    {
        await using var s = new Session_("waiting", Fixture.T0.AddSeconds(5));
        await s.After(Claude.User("go", 1), Claude.Assistant(20, "end_turn", Claude.Text("working on it")), Claude.User("carry on", 21));

        s.F.Time.Advance(TimeSpan.FromMinutes(11));
        var session = await s.Refresh();

        Assert.Equal(AttentionKind.NotResponding, session.Attention?.Kind);
    }

    [Fact]
    public async Task A_session_with_no_blocking_notification_still_reads_not_responding_when_silent()
    {
        await using var s = new Session_();
        await s.After(Claude.User("go", 1));

        s.F.Time.Advance(TimeSpan.FromMinutes(11));
        var session = await s.Refresh();

        Assert.Equal(AttentionKind.NotResponding, session.Attention?.Kind);
    }
}