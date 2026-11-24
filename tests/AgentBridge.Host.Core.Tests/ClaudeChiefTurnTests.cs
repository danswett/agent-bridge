using System.Text.Json.Nodes;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using Xunit;

// #3649: a Chief's turn is long (text, background Agent and SendMessage calls, more text) and messages arrive inside
// it. A client replaces the response part a reply's id names, or the last response part when the reply has none, so
// a reply with no id after one with an id overwrote the earlier reply. These pin the ids across such a turn.
public class ClaudeChiefTurnTests
{
    const string Id = "77777777-7777-4777-8777-777777777777";

    static string Notice(string task, int s) => new JsonObject
    {
        ["type"] = "attachment", ["timestamp"] = Copilot.At(s),
        ["attachment"] = new JsonObject
        {
            ["type"] = "queued_command", ["commandMode"] = "task-notification",
            ["prompt"] = $"<task-notification><task-id>{task}</task-id><status>completed</status></task-notification>",
        },
    }.ToJsonString();

    static string Human(string text, int s) => new JsonObject
    {
        ["type"] = "attachment", ["timestamp"] = Copilot.At(s), ["isSidechain"] = false,
        ["attachment"] = new JsonObject
        {
            ["type"] = "queued_command", ["commandMode"] = "prompt", ["prompt"] = text,
            ["origin"] = new JsonObject { ["kind"] = "human" },
        },
    }.ToJsonString();

    static JsonObject Bash(string id) => Claude.ToolUse(id, "Bash", new JsonObject { ["command"] = "echo " + id });
    static JsonObject Send(string id) => Claude.ToolUse(id, "SendMessage", new JsonObject { ["to"] = "agent-1", ["message"] = "continue" });

    Fixture Start(out string transcript)
    {
        var f = new Fixture();
        transcript = f.ClaudeTranscript(Id);
        f.ClaudeRegistration(Id, 900, transcript);
        f.Processes.Run(AgentKind.Claude, 900);
        return f;
    }

    // One message's text blocks arrive as the Chief's turn goes on: the message streams text, a tool, a delivered notice,
    // more text. The reply is one composite that grows, so it keeps ONE id, the first part's, however far it grows.
    [Fact]
    public async Task A_composite_reply_keeps_one_id_as_it_grows_across_tools_and_notices()
    {
        using var f = Start(out var transcript);
        await using var source = f.Source();
        var ids = new List<string?>();
        var texts = new List<string?>();
        async Task Step(params string[] lines)
        {
            Fixture.Append(transcript, lines);
            await source.RefreshAsync();
            var a = source.Get(Id)!.Activity;
            ids.Add(a.ResponseId);
            texts.Add(a.Response);
        }
        await Step(Claude.User("Brief me", 1), Claude.AssistantMessage("earlier", 2, null, Claude.Text("Earlier reply"), Bash("b0")), Claude.ToolResult("b0", 3));
        await Step(Claude.AssistantMessage("m", 4, null, Claude.Text("One")));
        await Step(Claude.AssistantMessage("m", 5, null, Send("send")), Claude.ToolResult("send", 6), Notice("agent-1", 7));
        await Step(Claude.AssistantMessage("m", 8, null, Claude.Text("Two")));
        await Step(Claude.AssistantMessage("m", 9, null, Bash("b1")), Claude.ToolResult("b1", 10));
        await Step(Claude.AssistantMessage("m", 11, "end_turn", Claude.Text("Three")));
        Assert.Equal(["Earlier reply", "One", "One", "One\n\nTwo", "One\n\nTwo", "One\n\nTwo\n\nThree"], texts);
        Assert.All(ids, id => Assert.NotNull(id));
        Assert.NotEqual(ids[0], ids[1]);
        Assert.Equal([ids[1], ids[1], ids[1], ids[1], ids[1]], ids.Skip(1));
        var parts = (await source.HistoryAsync(Id, null, HistoryLimits.DefaultTurns)).Turns!.Single().Parts.Where(p => p.Kind == HistoryPartKind.Response).ToArray();
        Assert.Equal(["Earlier reply", "One", "Two", "Three"], parts.Select(p => p.Text));
        Assert.Equal(parts[1].Id, ids[^1]);
        Assert.Equal(4, parts.Select(p => p.Id).Distinct().Count());
    }

    // A message that straddles a turn boundary (a person's message queued while it streamed) is not one reply of one turn:
    // the earlier turn's id must not follow its text into the next turn.
    [Fact]
    public async Task A_composite_that_crosses_a_turn_boundary_does_not_keep_the_earlier_turns_id()
    {
        using var f = Start(out var transcript);
        await using var source = f.Source();
        Fixture.Append(transcript, Claude.User("Brief me", 1), Claude.AssistantMessage("m", 2, null, Claude.Text("One")));
        await source.RefreshAsync();
        var first = source.Get(Id)!.Activity;
        Assert.NotNull(first.ResponseId);
        Fixture.Append(transcript, Human("and the calendar", 3), Claude.AssistantMessage("m", 4, "end_turn", Claude.Text("Two")));
        await source.RefreshAsync();
        var second = source.Get(Id)!.Activity;
        Assert.NotEqual(first.Turn, second.Turn);
        Assert.NotEqual(first.ResponseId, second.ResponseId);
    }

    // A message that arrives inside the turn (a person's, another session's text typed in, a delegate's notice) between
    // two replies leaves each reply on its own id: the Chief's briefing is never replaced.
    [Theory]
    [InlineData("notice")]
    [InlineData("human")]
    public async Task Replies_around_a_message_that_arrives_mid_turn_each_keep_their_own_id(string arrival)
    {
        using var f = Start(out var transcript);
        await using var source = f.Source();
        string?[] ids = new string?[3];
        async Task Step(int i, params string[] lines)
        {
            Fixture.Append(transcript, lines);
            await source.RefreshAsync();
            ids[i] = source.Get(Id)!.Activity.ResponseId;
        }
        await Step(0, Claude.User("Brief me", 1), Claude.AssistantMessage("m1", 2, null, Claude.Text("Looking into it."), Bash("b1")), Claude.ToolResult("b1", 3));
        await Step(1, arrival == "notice" ? Notice("agent-1", 4) : Human("and the calendar", 4),
            Claude.AssistantMessage("m2", 5, null, Claude.Text("The 08:00 briefing."), Send("send")), Claude.ToolResult("send", 6));
        await Step(2, Claude.AssistantMessage("m3", 7, "end_turn", Claude.Text("Done.")));
        Assert.All(ids, id => Assert.NotNull(id));
        Assert.Equal(3, ids.Distinct().Count());
    }
}
