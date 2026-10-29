using System.Text.Json;
using Xunit;

namespace AgentBridge.Rehearsal.Tests;

public class TranscriptTests
{
    static string Line(object o) => JsonSerializer.Serialize(o) + "\n";

    static (Scratch S, string Path) Transcript(params object[] lines)
    {
        var s = new Scratch();
        var path = s.File("events.jsonl", string.Concat(lines.Select(Line)));
        return (s, path);
    }

    static void Append(string path, params object[] lines) => File.AppendAllText(path, string.Concat(lines.Select(Line)));

    static object Recorded(object content, string? agentId = null) => agentId is null
        ? new { type = "human_response.recorded", data = new { toolCallId = "t1", response = new { content } } }
        : new { type = "human_response.recorded", agentId, data = new { toolCallId = "t9", response = new { content } } };

    static readonly IReadOnlyDictionary<string, string> NoFields = new Dictionary<string, string>();

    [Fact]
    public void Only_what_a_session_wrote_after_its_mark_counts()
    {
        var (s, path) = Transcript(Recorded("before"));
        using var _ = s;
        var mark = new FileInfo(path).Length;
        Assert.False(Transcripts.Answer(Transcripts.Copilot, path, mark, "before", NoFields).Found);
        Append(path, Recorded("after"));
        Assert.True(Transcripts.Answer(Transcripts.Copilot, path, mark, "after", NoFields).Found);
    }

    [Fact]
    public void A_subagents_records_never_count_as_the_sessions_answer()
    {
        var (s, path) = Transcript(Recorded("word", agentId: "sub-1"));
        using var _ = s;
        Assert.Equal(Match.Waiting, Transcripts.Answer(Transcripts.Copilot, path, 0, "word", NoFields));
    }

    [Fact]
    public void A_different_recorded_answer_fails_at_once_rather_than_waiting()
    {
        var (s, path) = Transcript(Recorded("beta"));
        using var _ = s;
        var m = Transcripts.Answer(Transcripts.Copilot, path, 0, "alpha", NoFields);
        Assert.False(m.Found);
        Assert.True(m.Final);
    }

    [Fact]
    public void Typed_words_match_whatever_whitespace_they_were_recorded_with()
    {
        var (s, path) = Transcript(Recorded("Grüße — “quoted”  你好 🚀\n"));
        using var _ = s;
        Assert.True(Transcripts.Answer(Transcripts.Copilot, path, 0, "Grüße — “quoted” 你好 🚀", NoFields).Found);
    }

    [Fact]
    public void A_form_answer_is_matched_field_by_field_with_a_list_compared_as_a_set()
    {
        var (s, path) = Transcript(Recorded(new Dictionary<string, object>
        {
            ["colour"] = "green", ["confirm"] = true, ["name"] = "Zoë 🚀", ["count"] = 3, ["extras"] = new[] { "z", "x" },
        }));
        using var _ = s;
        var fields = new Dictionary<string, string> { ["colour"] = "green", ["confirm"] = "true", ["name"] = "Zoë 🚀", ["count"] = "3", ["extras"] = "x,z" };
        Assert.True(Transcripts.Answer(Transcripts.Copilot, path, 0, null, fields).Found);
        fields["extras"] = "x";
        var wrong = Transcripts.Answer(Transcripts.Copilot, path, 0, null, fields);
        Assert.False(wrong.Found);
        Assert.Contains("extras", wrong.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Words_through_Other_are_read_from_the_tool_result_when_no_response_was_recorded()
    {
        var (s, path) = Transcript(new { type = "tool.execution_complete", data = new { toolCallId = "t1", result = new { content = "User responded: “smart” words" } } });
        using var _ = s;
        Assert.True(Transcripts.Answer(Transcripts.Copilot, path, 0, "“smart” words", NoFields).Found);
    }

    [Fact]
    public void A_Claude_answer_is_read_from_the_answers_it_recorded_and_a_sidechain_is_ignored()
    {
        var (s, path) = Transcript(
            new { type = "user", isSidechain = true, toolUseResult = new { answers = new Dictionary<string, string> { ["Pick one"] = "alpha" } } },
            new { type = "user", toolUseResult = new { answers = new Dictionary<string, string> { ["Pick one"] = "beta" } } });
        using var _ = s;
        Assert.True(Transcripts.Answer(Transcripts.Claude, path, 0, null, new Dictionary<string, string> { ["Pick one"] = "beta" }).Found);
        Assert.False(Transcripts.Answer(Transcripts.Claude, path, 0, null, new Dictionary<string, string> { ["Pick one"] = "alpha" }).Found);
    }

    [Fact]
    public async Task Codex_answers_are_left_to_a_person_since_Codex_records_none()
    {
        var (s, path) = Transcript(new { type = "event_msg", payload = new { type = "task_started" } });
        using var _ = s;
        var outcome = await Program.PollAsync(() => Transcripts.Answer(Transcripts.Codex, path, 0, null, NoFields), TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.Equal(Verdict.Manual, outcome.Verdict);
    }

    [Fact]
    public void A_reply_with_files_matches_its_words_after_the_mentions_and_counts_its_attachments()
    {
        var (s, path) = Transcript(new
        {
            type = "user.message",
            data = new { content = "@C:\\a\\1-x.png @C:\\a\\2-y.txt Describe the attached files in one line each.", attachments = new object[] { new { }, new { } }, delivery = "idle" },
        });
        using var _ = s;
        Assert.True(Transcripts.Reply(Transcripts.Copilot, path, 0, "Describe the attached files in one line each.", 2, steering: false).Found);
        var fewer = Transcripts.Reply(Transcripts.Copilot, path, 0, "Describe the attached files in one line each.", 3, steering: false);
        Assert.False(fewer.Found);
        Assert.True(fewer.Final);
    }

    [Fact]
    public void A_reply_taken_at_a_turn_boundary_is_not_steering_and_one_from_another_source_is_not_the_persons()
    {
        var (s, path) = Transcript(
            new { type = "user.message", data = new { content = "rehearsal steer", source = "system" } },
            new { type = "user.message", data = new { content = "rehearsal steer", delivery = "idle" } });
        using var _ = s;
        Assert.True(Transcripts.Reply(Transcripts.Copilot, path, 0, "rehearsal steer", 0, steering: false).Found);
        Assert.False(Transcripts.Reply(Transcripts.Copilot, path, 0, "rehearsal steer", 0, steering: true).Found);
        Append(path, new { type = "user.message", data = new { content = "rehearsal steer two", delivery = "steering" } });
        Assert.True(Transcripts.Reply(Transcripts.Copilot, path, 0, "rehearsal steer two", 0, steering: true).Found);
    }

    [Fact]
    public void Claude_and_Codex_replies_are_found_in_their_own_records()
    {
        var (s, path) = Transcript(new { type = "user", message = new { content = new object[] { new { type = "text", text = "rehearsal claude reply" } } } });
        using var _ = s;
        Assert.True(Transcripts.Reply(Transcripts.Claude, path, 0, "rehearsal claude reply", 0, steering: false).Found);
        Append(path, new { type = "event_msg", payload = new { type = "user_message", message = "rehearsal codex reply" } });
        Assert.True(Transcripts.Reply(Transcripts.Codex, path, 0, "rehearsal codex reply", 0, steering: false).Found);
        Append(path, new { type = "event_msg", payload = new { type = "item_completed", item = new { type = "UserMessage", id = "u1", content = new[] { new { type = "text", text = "rehearsal newer codex reply" } } } } });
        Assert.True(Transcripts.Reply(Transcripts.Codex, path, 0, "rehearsal newer codex reply", 0, steering: false).Found);
    }

    [Fact]
    public void A_deny_is_a_denial_without_a_message_or_a_cancel_followed_by_the_turn_stopping()
    {
        var (s, path) = Transcript(new { type = "permission.completed", data = new { requestId = "r1", result = new { kind = "denied-interactively-by-user" } } });
        using var _ = s;
        Assert.True(Transcripts.Permission(path, 0, "denied").Found);
        Assert.False(Transcripts.Permission(path, 0, "approved").Found);

        var mark = new FileInfo(path).Length;
        Append(path, new { type = "permission.completed", data = new { requestId = "r2", result = new { kind = "cancelled" } } });
        Assert.Equal(Match.Waiting, Transcripts.Permission(path, mark, "denied"));
        Append(path, new { type = "abort", data = new { reason = "user" } });
        Assert.True(Transcripts.Permission(path, mark, "denied").Found);

        var withWords = new FileInfo(path).Length;
        Append(path, new { type = "permission.completed", data = new { requestId = "r3", result = new { kind = "denied-interactively-by-user", feedback = "do it differently" } } });
        Assert.False(Transcripts.Permission(path, withWords, "denied").Found);
    }

    [Fact]
    public void A_line_still_being_written_is_not_read_until_it_ends()
    {
        var (s, path) = Transcript();
        using var _ = s;
        File.AppendAllText(path, "{\"type\":\"session.shutdown\"");
        Assert.Equal(Match.Waiting, Transcripts.Shutdown(Transcripts.Copilot, path, 0));
        File.AppendAllText(path, "}\n");
        Assert.True(Transcripts.Shutdown(Transcripts.Copilot, path, 0).Found);
    }

    [Fact]
    public void The_newest_session_of_each_agent_is_found_from_its_transcripts_without_a_host()
    {
        using var s = new Scratch();
        var homes = Homes.For(s.Root);
        var old = s.File(".copilot/session-state/11111111-old/events.jsonl", "");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));
        s.File(".copilot/session-state/22222222-new/events.jsonl", "");
        var sub = s.File(".claude/projects/C--repo/aaaaaaaa-main/subagents/agent-1.jsonl", "");
        var main = s.File(".claude/projects/C--repo/aaaaaaaa-main.jsonl", "");
        File.SetLastWriteTimeUtc(main, DateTime.UtcNow.AddMinutes(-5));
        s.File(".codex/sessions/2026/10/28/rollout-2026-10-28T10-00-00-0199a3b2-1c2d-7e8f-9a0b-1c2d3e4f5a6b.jsonl", "");
        Assert.Equal("22222222-new", Transcripts.Newest(homes, Transcripts.Copilot)?.Id);
        Assert.Equal("aaaaaaaa-main", Transcripts.Newest(homes, Transcripts.Claude)?.Id);
        Assert.True(File.Exists(sub));
        Assert.Equal("0199a3b2-1c2d-7e8f-9a0b-1c2d3e4f5a6b", Transcripts.Newest(homes, Transcripts.Codex)?.Id);
    }

    [Fact]
    public void Each_agents_transcript_is_found_by_the_session_id_alone()
    {
        using var s = new Scratch();
        var homes = Homes.For(s.Root);
        s.File(".copilot/session-state/11111111-aaaa/events.jsonl", "");
        s.File(".claude/projects/C--repo/22222222-bbbb.jsonl", "");
        s.File(".codex/sessions/2026/10/28/rollout-2026-10-28T10-00-00-33333333-cccc.jsonl", "");
        Assert.Equal(Transcripts.Copilot, Transcripts.Find(homes, "11111111-aaaa")?.Kind);
        Assert.Equal(Transcripts.Claude, Transcripts.Find(homes, "22222222-bbbb")?.Kind);
        Assert.Equal(Transcripts.Codex, Transcripts.Find(homes, "33333333-cccc")?.Kind);
        Assert.Null(Transcripts.Find(homes, "../../etc"));
    }
}
