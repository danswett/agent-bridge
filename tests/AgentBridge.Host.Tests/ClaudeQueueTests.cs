using System.Text.Json;
using AgentBridge.Host.Delivery.AgentReplies;
using Xunit;

namespace AgentBridge.Host.Tests;

// Whether Claude has taken a relay prompt (docs/message-delivery.md "Busy sends to Claude", #3856). A relay prompt is found by the request id
// its framed sender line carries; no message text is kept. The shapes are the ones the repo's Claude prompt parser documents (2.1.312).
public sealed class ClaudeQueueTests : IDisposable
{
    readonly string path = Path.Combine(Path.GetTempPath(), "claude-queue-" + Guid.NewGuid().ToString("N") + ".jsonl");
    public void Dispose() { if (File.Exists(path)) { File.Delete(path); } }

    const string Req = "11111111-1111-1111-1111-111111111111";
    static string Frame(string request) => $"[Slipgates · from an agent on SENDER · session sess-9 · request {request} · not typed by your user] please look at x";
    static string Line(object o) => JsonSerializer.Serialize(o);
    void Append(params string[] lines) => File.AppendAllText(path, string.Concat(lines.Select(l => l + "\n")));

    static string QueuedTaken(string request) => Line(new { type = "user", promptSource = "queued", message = new { role = "user", content = Frame(request) } });
    static string Enqueue(string request) => Line(new { type = "queue-operation", operation = "enqueue", content = Frame(request) });
    static string IdlePrompt(string request) => Line(new { type = "user", message = new { role = "user", content = Frame(request) } });

    [Fact]
    public void A_queued_user_entry_or_a_plain_user_entry_with_the_request_id_is_a_prompt_taken()
    {
        Append(QueuedTaken(Req));
        Assert.True(ClaudeQueue.Taken(path, 0, Req));
        File.Delete(path);
        Append(IdlePrompt(Req));
        Assert.True(ClaudeQueue.Taken(path, 0, Req));
        File.Delete(path);
        Append(Line(new { type = "user", message = new { role = "user", content = new object[] { new { type = "text", text = Frame(Req) } } } }));
        Assert.True(ClaudeQueue.Taken(path, 0, Req));
    }

    [Fact]
    public void An_enqueue_row_another_requests_prompt_a_tool_result_a_sidechain_and_a_meta_entry_are_not_a_prompt_taken()
    {
        Append(Enqueue(Req),
            QueuedTaken("22222222-2222-2222-2222-222222222222"),
            Line(new { type = "user", message = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = "t1", content = Frame(Req) } } } }),
            Line(new { type = "user", isSidechain = true, promptSource = "queued", message = new { role = "user", content = Frame(Req) } }),
            Line(new { type = "user", isMeta = true, message = new { role = "user", content = Frame(Req) } }),
            Line(new { type = "assistant", message = new { role = "assistant", content = new object[] { new { type = "text", text = Frame(Req) } } } }));
        Assert.False(ClaudeQueue.Taken(path, 0, Req));
    }

    [Fact]
    public void Only_entries_after_the_offset_count_and_the_mark_is_the_length_so_far()
    {
        Append(QueuedTaken(Req));
        var mark = ClaudeQueue.Mark(path);
        Assert.Equal(new FileInfo(path).Length, mark);
        Assert.False(ClaudeQueue.Taken(path, mark, Req));      // taken before the send began: not this send's
        Append(QueuedTaken(Req));
        Assert.True(ClaudeQueue.Taken(path, mark, Req));
    }

    [Fact]
    public void A_missing_or_unreadable_transcript_is_never_a_prompt_taken_and_marks_zero()
    {
        Assert.Equal(0, ClaudeQueue.Mark(path));
        Assert.False(ClaudeQueue.Taken(path, 0, Req));
        Append("not json at all", "{\"type\":\"user\"");
        Assert.False(ClaudeQueue.Taken(path, 0, Req));
    }
}
