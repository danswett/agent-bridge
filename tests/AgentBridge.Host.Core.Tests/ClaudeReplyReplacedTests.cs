using System.Text.Json.Nodes;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using Xunit;

// #4245: the owner still lost replies after #4074/#4082. A text-only reply was replaced by the NEXT reply, one that
// makes its tool calls first and says its text after them. The host keeps one id per reply in every shape of that turn;
// the client's rewrite of an id-less live part is pinned in SessionHistoryRepliesTests.
public class ClaudeReplyReplacedTests
{
    const string Id = "88888888-8888-4888-8888-888888888888";

    static JsonObject Bash(string id) => Claude.ToolUse(id, "Bash", new JsonObject { ["command"] = "echo " + id });

    [Theory]
    [InlineData(false, "plain")]
    [InlineData(true, "plain")]
    [InlineData(true, "thinking")]
    [InlineData(true, "parallel")]
    [InlineData(true, "noids")]
    [InlineData(false, "noids")]
    [InlineData(true, "thinkingtext")]
    public async Task A_text_only_reply_is_not_replaced_by_the_next_reply_that_calls_tools_before_its_text(bool sameTurn, string shape)
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(Id);
        f.ClaudeRegistration(Id, 900, transcript);
        f.Processes.Run(AgentKind.Claude, 900);
        await using var source = f.Source();
        var seen = new List<(string? Id, string? Text, string? Turn)>();
        async Task Step(params string[] lines)
        {
            Fixture.Append(transcript, lines);
            await source.RefreshAsync();
            var a = source.Get(Id)!.Activity;
            seen.Add((a.ResponseId, a.Response, a.ResponseTurn));
        }
        var ids = shape == "noids";
        await Step(Claude.User("Hello", 1), Claude.AssistantMessage(ids ? null : "m1", 2, "end_turn", Claude.Text("First, text only.")));
        if (!sameTurn) { await Step(Claude.User("Now check", 3)); }
        var m2 = ids ? null : "m2";
        await Step(Claude.AssistantMessage(m2, 4, null, shape == "thinking" || shape == "thinkingtext" ? [Claude.Thinking("hm"), Bash("b1")] : [Bash("b1")]));
        if (shape == "parallel") { await Step(Claude.AssistantMessage(m2, 4, null, Bash("b2"))); await Step(Claude.ToolResult("b2", 5)); }
        await Step(Claude.ToolResult("b1", 5));
        await Step(Claude.AssistantMessage(m2, 6, "end_turn", shape == "thinkingtext" ? [Claude.Thinking("ok"), Claude.Text("Second, after tools.")] : [Claude.Text("Second, after tools.")]));
        var first = seen[0];
        var last = seen[^1];
        Assert.Equal("First, text only.", first.Text);
        Assert.Equal("Second, after tools.", last.Text);
        Assert.NotNull(first.Id);
        Assert.NotNull(last.Id);
        Assert.NotEqual(first.Id, last.Id);
        var turns = (await source.HistoryAsync(Id, null, HistoryLimits.DefaultTurns)).Turns!;
        var texts = turns.SelectMany(t => t.Parts).Where(p => p.Kind == HistoryPartKind.Response).Select(p => p.Text).ToArray();
        Assert.Equal(new string?[] { "First, text only.", "Second, after tools." }, texts);
    }
}
