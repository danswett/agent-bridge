using System.Text.Json.Nodes;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using Xunit;

// #3649: one record the host cannot read (a complete line that is not valid JSON, or one over the line cap) used to leave
// every later reply of the turn without an id, and a client rewrites the last reply's part for a reply with no id. The
// unread record may have held text of a message that continues after it, so the first message after it stays without an
// id; the replies after that keep theirs.
public class ClaudeUnreadableRecordTests
{
    const string Id = "99999999-9999-4999-8999-999999999999";
    const string Malformed = "{\"type\":\"assistant\",\"message\":";

    [Fact]
    public async Task An_unreadable_record_costs_one_reply_its_identity_not_the_rest_of_the_turn()
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(Id);
        f.ClaudeRegistration(Id, 900, transcript);
        f.Processes.Run(AgentKind.Claude, 900);
        var bash = Claude.ToolUse("tool", "Bash", new JsonObject { ["command"] = "echo test" });
        await using var source = f.Source();
        var seen = new List<(string? Text, string? Id)>();
        async Task Step(params string[] lines)
        {
            Fixture.Append(transcript, lines);
            await source.RefreshAsync();
            var a = source.Get(Id)!.Activity;
            seen.Add((a.Response, a.ResponseId));
        }
        await Step(Claude.User("Brief me", 1), Claude.AssistantMessage("m1", 2, null, Claude.Text("Before")));
        await Step(Malformed, Claude.AssistantMessage("m2", 3, null, Claude.Text("Edge"), bash), Claude.ToolResult("tool", 4));
        await Step(Claude.AssistantMessage("m3", 5, null, Claude.Text("After")));
        await Step(Claude.AssistantMessage("m4", 6, "end_turn", Claude.Text("Last")));
        var parts = (await source.HistoryAsync(Id, null, HistoryLimits.DefaultTurns)).Turns!.Single().Parts.Where(p => p.Kind == HistoryPartKind.Response).ToArray();
        Assert.Equal(["Before", "Edge", "After", "Last"], parts.Select(p => p.Text));
        Assert.Equal(parts[0].Id, seen[0].Id);
        // The message right after the unreadable record may continue the unread one: no id (as for a window that opens mid-turn).
        Assert.Equal("Edge", seen[1].Text);
        Assert.Null(seen[1].Id);
        // Every reply after that keeps the id history gives it.
        Assert.Equal(parts[2].Id, seen[2].Id);
        Assert.Equal(parts[3].Id, seen[3].Id);
    }

    // An unreadable record that may start a turn leaves the turn unknown until the next readable prompt: unchanged.
    [Fact]
    public async Task An_unreadable_prompt_still_leaves_the_turn_without_ids_until_the_next_readable_prompt()
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(Id);
        f.ClaudeRegistration(Id, 900, transcript);
        f.Processes.Run(AgentKind.Claude, 900);
        await using var source = f.Source();
        Fixture.Append(transcript, "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":", Claude.AssistantMessage("m1", 2, "end_turn", Claude.Text("Answer")));
        await source.RefreshAsync();
        Assert.Null(source.Get(Id)!.Activity.ResponseId);
        Fixture.Append(transcript, Claude.User("Next", 3), Claude.AssistantMessage("m2", 4, "end_turn", Claude.Text("Fresh")));
        await source.RefreshAsync();
        var part = (await source.HistoryAsync(Id, null, HistoryLimits.DefaultTurns)).Turns!.Last().Parts.Single(p => p.Kind == HistoryPartKind.Response);
        Assert.Equal(part.Id, source.Get(Id)!.Activity.ResponseId);
    }
}
