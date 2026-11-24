using System.Text.Json.Nodes;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using Xunit;

// #4245 (family of #3649): the host cannot always say which history part a reply is (after an unreadable line, a skipped head, a long turn), and sends no
// responseId then. A client with no id used to treat two such replies as one live slot, so the second rewrote the first. responseRef tells it, from the host
// and not from the text (#2667), that a reply is a new one.
public class ClaudeReplyRefTests
{
    const string Id = "66666666-6666-4666-8666-666666666666";

    static string Short(string? s) => s is null ? "null" : s[..Math.Min(8, s.Length)];

    [Fact]
    public async Task Every_reply_has_its_own_ref_even_when_the_host_has_lost_which_part_each_is_and_a_growing_reply_keeps_its_ref()
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(Id);
        f.ClaudeRegistration(Id, 900, transcript);
        f.Processes.Run(AgentKind.Claude, 900);
        await using var source = f.Source();
        var seen = new List<(string? Id, string? Ref, string? Text)>();
        async Task Step(params string[] lines)
        {
            Fixture.Append(transcript, lines);
            await source.RefreshAsync();
            var a = source.Get(Id)!.Activity;
            seen.Add((a.ResponseId, a.ResponseRef, a.Response));
        }
        await Step(Claude.User("go", 1), Claude.AssistantMessage("m1", 2, null, Claude.Text("First reply")));
        await Step("{ this line is not json");                                                                             // the host loses which part a reply is
        await Step(Claude.AssistantMessage("m2", 3, null, Claude.ToolUse("b1", "Bash", new JsonObject { ["command"] = "echo" })), Claude.ToolResult("b1", 4));
        await Step(Claude.AssistantMessage("m3", 5, null, Claude.Text("Second reply")));
        await Step(Claude.AssistantMessage("m3", 6, null, Claude.ToolUse("b2", "Bash", new JsonObject { ["command"] = "echo" })), Claude.ToolResult("b2", 7));
        await Step(Claude.AssistantMessage("m4", 8, "end_turn", Claude.Text("Third reply")));
        await Step(Claude.AssistantMessage("m4", 9, "end_turn", Claude.Text("Third reply, more")));                        // the same message growing

        var replies = seen.Where(s => !string.IsNullOrEmpty(s.Text)).ToList();
        var first = replies.First(r => r.Text == "First reply");
        var second = replies.First(r => r.Text == "Second reply");
        var third = replies.First(r => r.Text == "Third reply");
        Assert.NotNull(first.Id);
        Assert.Null(second.Id);                                                                                            // the loss this test is about
        Assert.Null(third.Id);
        Assert.All([first, second, third], r => Assert.False(string.IsNullOrEmpty(r.Ref), $"[{r.Text}] has no ref"));
        Assert.Equal(3, new[] { first.Ref, second.Ref, third.Ref }.Distinct().Count());
        Assert.Equal(third.Ref, replies.Last().Ref);                                                                       // "Third reply, more" is the same reply grown
        Assert.Equal("Third reply\n\nThird reply, more", replies.Last().Text);
    }

    [Fact]
    public async Task A_session_with_no_reply_has_no_ref_and_an_identified_reply_has_one_too()
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(Id);
        f.ClaudeRegistration(Id, 900, transcript);
        f.Processes.Run(AgentKind.Claude, 900);
        await using var source = f.Source();
        Fixture.Append(transcript, Claude.User("go", 1));
        await source.RefreshAsync();
        Assert.Null(source.Get(Id)!.Activity.ResponseRef);
        Fixture.Append(transcript, Claude.AssistantMessage("m1", 2, "end_turn", Claude.Text("Only reply")));
        await source.RefreshAsync();
        var a = source.Get(Id)!.Activity;
        Assert.False(string.IsNullOrEmpty(a.ResponseId));
        Assert.False(string.IsNullOrEmpty(a.ResponseRef));
    }

    // #4333 review P2: the ref of a reply that GROWS across a tool or a notice (the same message id, not contiguous) is its first part's, and a re-read of the
    // same transcript after a host restart gives every reply the same ref.
    [Fact]
    public async Task A_reply_that_grows_across_a_tool_keeps_its_first_ref_and_the_next_reply_has_another()
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(Id);
        f.ClaudeRegistration(Id, 900, transcript);
        f.Processes.Run(AgentKind.Claude, 900);
        await using var source = f.Source();
        var seen = new List<(string? Ref, string? Text)>();
        async Task Step(params string[] lines)
        {
            Fixture.Append(transcript, lines);
            await source.RefreshAsync();
            var a = source.Get(Id)!.Activity;
            seen.Add((a.ResponseRef, a.Response));
        }
        await Step(Claude.User("go", 1), Claude.AssistantMessage("m1", 2, null, Claude.Text("One")));
        await Step(Claude.AssistantMessage("m1", 3, null, Claude.ToolUse("b1", "Bash", new JsonObject { ["command"] = "echo" })), Claude.ToolResult("b1", 4));
        await Step(Claude.AssistantMessage("m1", 5, null, Claude.Text("Two")));                                            // the same message grows across the tool
        await Step(Claude.AssistantMessage("m2", 6, "end_turn", Claude.Text("Next")));
        var one = seen.First(s => s.Text == "One");
        var grown = seen.First(s => s.Text == "One\n\nTwo");
        var next = seen.First(s => s.Text == "Next");
        Assert.False(string.IsNullOrEmpty(one.Ref));
        Assert.Equal(one.Ref, grown.Ref);                                                                                   // one reply, one ref, however far it grows
        Assert.NotEqual(grown.Ref, next.Ref);                                                                               // the next reply is another
    }

    [Fact]
    public async Task A_host_restart_and_a_full_re_read_give_the_same_reply_the_same_ref()
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(Id);
        f.ClaudeRegistration(Id, 900, transcript);
        f.Processes.Run(AgentKind.Claude, 900);
        string? before;
        await using (var first = f.Source())
        {
            Fixture.Append(transcript, Claude.User("go", 1), Claude.AssistantMessage("m1", 2, null, Claude.Text("One")),
                Claude.AssistantMessage("m2", 3, "end_turn", Claude.Text("Two")));
            await first.RefreshAsync();
            before = first.Get(Id)!.Activity.ResponseRef;
        }
        Assert.False(string.IsNullOrEmpty(before));
        await using var second = f.Source();                                                                                // a new host run reads the whole transcript again
        await second.RefreshAsync();
        var after = second.Get(Id)!.Activity;
        Assert.Equal("Two", after.Response);
        Assert.Equal(before, after.ResponseRef);
        // And a host that starts reading in the middle (only the last record is inside the window) still gives that reply the same ref: it comes from the record, not from a count.
        var lastLine = File.ReadLines(transcript).Last();
        await using var third = f.Source(tailBytes: System.Text.Encoding.UTF8.GetByteCount(lastLine) + 4);
        await third.RefreshAsync();
        Assert.Equal("Two", third.Get(Id)!.Activity.Response);
        Assert.Equal(before, third.Get(Id)!.Activity.ResponseRef);
    }
}
