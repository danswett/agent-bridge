using System.Text.Json.Nodes;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using Xunit;

// #1628: every reply the person sent shows in scrollback once, in the order it was sent,
// whether it came between turns, while the agent was working, or seconds after another.
public class ReplyScrollbackTests
{
    const string Id = "66666666-6666-4666-8666-666666666666";

    static async Task<List<string>> AllPrompts(SessionSource source)
    {
        var seen = new List<string>();
        string? before = null;
        do
        {
            var page = await source.HistoryAsync(Id, before, 7);
            seen.InsertRange(0, page.Turns!.Select(t => t.Prompt!.Text));
            before = page.Start is null ? page.Before : null;
        } while (before is not null);
        return seen;
    }

    // Long enough to need several pages, with replies at the start of a turn, in the middle of
    // one and after it, then five identical short ones within ten seconds.
    [Fact]
    public async Task A_copilot_session_keeps_every_reply_in_order_including_one_sent_mid_turn_and_rapid_ones()
    {
        using var f = new Fixture();
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        var lines = new List<string>();
        var expected = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            expected.Add("filler " + i);
            lines.AddRange([Copilot.User("filler " + i, i * 10), Copilot.TurnStart(i * 10 + 1), Copilot.Assistant("ok " + i, i * 10 + 2)]);
        }
        expected.AddRange(["reply one", "reply two (mid-turn)", "reply three"]);
        lines.AddRange([
            Copilot.User("reply one", 300), Copilot.TurnStart(301),
            Copilot.User("reply two (mid-turn)", 302), Copilot.Assistant("working", 303),
            Copilot.User("reply three", 310), Copilot.TurnStart(311), Copilot.Assistant("done", 312)]);
        var same = Copilot.User("ok", 320);
        for (var i = 0; i < 5; i++) { expected.Add("ok"); lines.Add(same); }
        Fixture.Append(events, [.. lines]);
        await using var source = f.Source();
        await source.RefreshAsync();

        Assert.Equal(expected, await AllPrompts(source));
    }

    [Fact]
    public async Task A_claude_session_keeps_every_reply_in_order_including_one_sent_mid_turn_and_rapid_ones()
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(Id);
        f.ClaudeRegistration(Id, 900, transcript);
        f.Processes.Run(AgentKind.Claude, 900);
        var lines = new List<string>();
        var expected = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            expected.Add("filler " + i);
            lines.AddRange([Claude.User("filler " + i, i * 10), Claude.Assistant(i * 10 + 1, "end_turn", Claude.Text("ok " + i))]);
        }
        expected.AddRange(["reply one", "reply two (mid-turn)", "reply three"]);
        lines.AddRange([
            Claude.User("reply one", 300), Claude.Assistant(301, "tool_use", Claude.ToolUse("t1", "Bash", new JsonObject { ["command"] = "make" })),
            Claude.User("reply two (mid-turn)", 302), Claude.ToolResult("t1", 303), Claude.Assistant(304, "end_turn", Claude.Text("working")),
            Claude.User("reply three", 310), Claude.Assistant(311, "end_turn", Claude.Text("done"))]);
        var same = Claude.User("ok", 320);
        for (var i = 0; i < 5; i++) { expected.Add("ok"); lines.Add(same); }
        Fixture.Append(transcript, [.. lines]);
        await using var source = f.Source();
        await source.RefreshAsync();

        Assert.Equal(expected, await AllPrompts(source));
    }
}
