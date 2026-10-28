using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Copilot;
using Xunit;

// A reply's keys, and reading a transcript for one: before typing (is anything asking?) and
// after (which prompts did the person give?).
public sealed class CopilotReplyTests : IDisposable
{
    readonly Fixture f = new();
    readonly string events;

    public CopilotReplyTests() => events = f.CopilotSession("22222222-2222-4222-8222-222222222222", 100);

    public void Dispose() => f.Dispose();

    static string Ask(string id, int s, string? agentId = null) => Copilot.ToolStart(id, "ask_user", new { message = "Which?" }, s, agentId);

    static string Prompt(string text, int s, string? source = null, string delivery = "idle") =>
        Copilot.Event("user.message", source is null ? new { content = text, delivery } : (object)new { content = text, delivery, source }, s);

    [Fact]
    public void A_reply_is_its_words_as_one_burst_then_Enter_after_300_ms_with_every_line_break_a_space()
    {
        var (keys, words) = CopilotReply.Plan("first line\r\nsecond\tline\u2028third 🚀");

        Assert.Equal("first line  second line third 🚀", words);
        var send = Assert.Single(keys.Sends);
        Assert.Equal((words, true, 300, 0), (send.Text, send.Enter, send.EnterDelayMs, send.PauseAfterMs));
    }

    [Fact]
    public void A_recorded_prompt_matches_the_typed_words_whatever_its_whitespace_and_nothing_else_does()
    {
        Assert.True(CopilotReply.Matches("first line  second", "first line second"));
        Assert.True(CopilotReply.Matches(" carry on ", "carry on"));
        Assert.False(CopilotReply.Matches("carry on", "carry on."));
        Assert.False(CopilotReply.Matches("carry on", "Carry on"));
    }

    [Fact]
    public void Nothing_asks_in_an_idle_transcript_and_the_scan_ends_on_its_last_line()
    {
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.Assistant("done", 3), Copilot.TurnEnd(4));

        var before = CopilotPrompts.Before(events);

        Assert.False(before.Asking);
        Assert.Equal(new FileInfo(events).Length, before.ScannedTo);
    }

    [Fact]
    public void An_open_question_from_the_session_or_a_subagent_asks_until_it_is_answered()
    {
        Fixture.Append(events, Ask("q1", 1));
        Assert.True(CopilotPrompts.Before(events).Asking);
        Fixture.Append(events, Copilot.Answered("q1", 2), Copilot.ToolDone("q1", 3));
        Assert.False(CopilotPrompts.Before(events).Asking);

        // A subagent's question takes the same input.
        Fixture.Append(events, Ask("q2", 4, agentId: "helper"));
        Assert.True(CopilotPrompts.Before(events).Asking);
        Fixture.Append(events, Copilot.ToolDone("q2", 5, agentId: "helper"));
        Assert.False(CopilotPrompts.Before(events).Asking);
    }

    [Fact]
    public void A_permission_prompt_asks_until_it_completes_and_an_abort_ends_everything_open()
    {
        Fixture.Append(events, Copilot.Event("permission.requested", new { requestId = "r1", permissionRequest = new { kind = "shell", toolCallId = "c9" } }, 1));
        Assert.True(CopilotPrompts.Before(events).Asking);
        Fixture.Append(events, Copilot.Event("permission.completed", new { requestId = "r1", result = new { kind = "approved" } }, 2));
        Assert.False(CopilotPrompts.Before(events).Asking);

        Fixture.Append(events, Ask("q3", 3), Copilot.Event("permission.requested", new { requestId = "r2", permissionRequest = new { kind = "shell" } }, 4));
        // A subagent's abort ends only its own questions.
        Fixture.Append(events, Copilot.Event("abort", new { reason = "x" }, 5, agentId: "helper"));
        Assert.True(CopilotPrompts.Before(events).Asking);
        Fixture.Append(events, Copilot.Event("abort", new { reason = "user" }, 6));
        Assert.False(CopilotPrompts.Before(events).Asking);
    }

    [Fact]
    public void After_typing_only_the_persons_own_prompts_count_each_identified_by_its_line()
    {
        Fixture.Append(events, Prompt("before", 1));
        var from = CopilotPrompts.Before(events).ScannedTo;
        Fixture.Append(events,
            Prompt("scheduled", 2, source: "schedule-1"),
            Copilot.User("to the helper", 3, agentId: "helper"),
            Prompt("carry on", 4, delivery: "steering"));
        var offset = new FileInfo(events).Length;
        Fixture.Append(events, Prompt("carry on", 5, delivery: "queued"));

        var since = CopilotPrompts.Since(events, from);

        Assert.Equal(["carry on", "carry on"], since.Prompts.Select(p => p.Content));
        Assert.Equal(offset, since.Prompts[1].Offset);
        Assert.NotEqual(since.Prompts[0].Offset, since.Prompts[1].Offset);
        Assert.False(since.SessionEnded);
    }

    [Fact]
    public void A_line_still_being_written_waits_for_the_next_read_and_a_shutdown_ends_the_session()
    {
        var from = CopilotPrompts.Before(events).ScannedTo;
        File.AppendAllText(events, Prompt("half", 1));
        Assert.Empty(CopilotPrompts.Since(events, from).Prompts);

        File.AppendAllText(events, "\n" + Copilot.Event("session.shutdown", new { }, 2) + "\n");
        var since = CopilotPrompts.Since(events, from);
        Assert.Equal(("half", true), (Assert.Single(since.Prompts).Content, since.SessionEnded));
    }
}
