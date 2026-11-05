using System.Text;
using System.Text.Json.Nodes;
using AgentBridge.Host.Core.Parsing;
using AgentBridge.Protocol;
using Xunit;

// session.attention from Copilot's own session.error records (#800). The sign-in failure is VM1's,
// captured read-only and redacted (#800, 2026-11-04): its data fields exactly as written there.
public class AttentionTests
{
    const string Id = "55555555-5555-4555-8555-555555555555";
    const string Vm1Message = "Authorization error. Your credentials may be expired or invalid. [request-id redacted]";

    static readonly object SignedOut = new { errorType = "authorization", message = Vm1Message, statusCode = 401, remediation = "sign_in" };

    sealed class Session_ : IAsyncDisposable
    {
        public readonly Fixture F = new();
        public readonly string Events;
        public readonly AgentBridge.Host.Core.SessionSource Source;

        public Session_(long tailBytes = 4 * 1024 * 1024)
        {
            Events = F.CopilotSession(Id, 100);
            F.Processes.Run(AgentKind.Copilot, 100);
            Source = F.Source(tailBytes: tailBytes);
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

    static string Error(object data, int s, string? agentId = null) => Copilot.Event("session.error", data, s, agentId);

    [Fact]
    public async Task A_turn_that_ended_on_copilots_authorization_error_leaves_the_session_idle_and_needing_sign_in()
    {
        await using var s = new Session_();

        var session = await s.After(Copilot.User("Carry on with the release", 1), Copilot.TurnStart(2), Error(SignedOut, 3));

        Assert.Equal(SessionStatus.Idle, session.Status);
        Assert.Equal(new Attention { Kind = AttentionKind.SignInNeeded, Since = Fixture.T0.AddSeconds(3), Agent = AgentKind.Copilot, Provider = "github", Message = Vm1Message }, session.Attention);
    }

    [Fact]
    public async Task A_signed_out_session_that_answers_again_no_longer_needs_sign_in()
    {
        await using var s = new Session_();
        Assert.NotNull((await s.After(Copilot.User("Carry on", 1), Copilot.TurnStart(2), Error(SignedOut, 3))).Attention);

        var session = await s.After(Copilot.User("Try again", 10), Copilot.TurnStart(11), Copilot.Assistant("Done.", 12), Copilot.TurnEnd(13));

        Assert.Null(session.Attention);
        Assert.Equal(SessionStatus.Idle, session.Status);
    }

    // VM1's Quality session (#800): signed out at 16:45:05.967, a turn_end at 16:50:55.495, and a
    // failed retry 51 ms later. A turn ending is no proof the agent can reach its model again.
    [Fact]
    public async Task A_turn_ending_between_two_sign_in_failures_leaves_the_session_needing_sign_in_since_the_second()
    {
        await using var s = new Session_();
        await s.After(Copilot.User("Carry on", 1), Copilot.TurnStart(2), Error(SignedOut, 3));

        var session = await s.After(Copilot.TurnEnd(10), Error(SignedOut, 11));

        Assert.Equal(AttentionKind.SignInNeeded, session.Attention?.Kind);
        Assert.Equal(Fixture.T0.AddSeconds(11), session.Attention?.Since);
    }

    // VM1's Migration session (#800): its 16:47:25 was a turn_end only. Nor do a prompt, a turn
    // starting, an empty message, the CLI's own turn-error notice or a reply dated no later than
    // the failure prove anything.
    public static TheoryData<string, string[]> NoProof => new()
    {
        { "a turn ending", [Copilot.TurnEnd(10)] },
        { "a prompt and its turn starting", [Copilot.User("Try again", 10), Copilot.TurnStart(11)] },
        { "an injected notice", [Copilot.Event("user.message", new { content = "Shell 3 completed", source = "system" }, 10)] },
        { "an empty message", [Copilot.TurnStart(10), Copilot.Assistant("", 11)] },
        { "the CLI's turn-error notice", [Copilot.Event("assistant.message", new { messageId = "m10", content = "\u26A0\uFE0F Turn error: Authorization error." }, 10)] },
        { "a reply dated at the failure", [Copilot.Assistant("Done.", 3)] },
    };

    [Theory]
    [MemberData(nameof(NoProof))]
    public async Task Nothing_but_the_model_answering_after_the_failure_clears_sign_in(string what, string[] lines)
    {
        await using var s = new Session_();
        await s.After(Copilot.User("Carry on", 1), Copilot.TurnStart(2), Error(SignedOut, 3));

        var session = await s.After(lines);

        Assert.True(session.Attention?.Kind == AttentionKind.SignInNeeded, what);
    }

    // VM1's Quality session (#800, 6646932234), as event types and times only: signed out at
    // 16:45:05.967, a turn_end at 16:50:55.495, signed out again at 16:50:55.546; restored by the
    // owner, its prompt at 16:57:44.130 ran a model call, a system message and usage before the
    // model's first message of its own, with a tool request, at 16:58:01.264. Only that clears it.
    [Fact]
    public async Task Vm1s_quality_session_needs_sign_in_until_the_models_first_message_after_it_was_restored()
    {
        await using var s = new Session_();
        await s.After(Copilot.User("Carry on", 1), Copilot.TurnStart(2), At(Error(SignedOut, 0), -349_579), At(Copilot.TurnEnd(0), -51), At(Error(SignedOut, 0), 0));

        var before = await s.After(
            At(Copilot.User("Carry on", 0), 408_584),
            At(Copilot.Event("model.turn_started", new { }, 0), 408_609),
            At(Copilot.Event("system.message", new { content = "Context." }, 0), 408_617),
            At(Copilot.Event("model.model_call_started", new { }, 0), 408_614),
            At(Copilot.TurnStart(0), 408_667),
            At(Copilot.Event("model.captured_assignment_context", new { }, 0), 410_002),
            At(Copilot.Event("model.model_call_success", new { }, 0), 410_002),
            At(Copilot.Event("model.message", new { }, 0), 410_003),
            At(Copilot.Event("model.response", new { }, 0), 410_003),
            At(Copilot.Event("model.turn_ended", new { }, 0), 410_003),
            At(Copilot.Event("model.messages_snapshot", new { }, 0), 410_004),
            At(Copilot.Event("session.usage_record", new { }, 0), 425_668));

        Assert.Equal(AttentionKind.SignInNeeded, before.Attention?.Kind);
        Assert.Equal(Failed, before.Attention?.Since);

        var after = await s.After(
            At(Copilot.Assistant("Checking CI.", 0, tools: ["call-1"]), 425_718),
            At(Copilot.ToolStart("call-1", "powershell", new { }, 0), 425_785),
            At(Copilot.Event("hook.start", new { }, 0), 425_842),
            At(Copilot.Event("hook.end", new { }, 0), 425_843),
            At(Copilot.Event("tool.execution_complete", new { toolCallId = "call-1", success = true }, 0), 430_906),
            At(Copilot.TurnEnd(0), 431_209));

        Assert.Null(after.Attention);
    }

    // The later of VM1's two failures, as the fixture's times are taken from it.
    static readonly DateTimeOffset Failed = Fixture.T0.AddSeconds(400);

    static string At(string line, int ms)
    {
        var o = JsonNode.Parse(line)!.AsObject();
        o["timestamp"] = Failed.AddMilliseconds(ms).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        return o.ToJsonString();
    }

    [Fact]
    public async Task A_tool_the_model_asks_for_after_the_failure_clears_sign_in()
    {
        await using var s = new Session_();
        await s.After(Copilot.User("Carry on", 1), Copilot.TurnStart(2), Error(SignedOut, 3));

        var session = await s.After(Copilot.User("Try again", 10), Copilot.TurnStart(11), Copilot.Assistant("", 12, tools: ["call-1"]));

        Assert.Null(session.Attention);
    }

    [Fact]
    public async Task Another_error_while_it_needs_sign_in_leaves_it_needing_sign_in_past_the_next_prompt()
    {
        await using var s = new Session_();
        await s.After(Copilot.User("Carry on", 1), Copilot.TurnStart(2), Error(SignedOut, 3));

        var session = await s.After(Error(new { errorType = "execution", message = "Failed to run /x" }, 5), Copilot.User("Try again", 10));

        Assert.Equal(AttentionKind.SignInNeeded, session.Attention?.Kind);
    }

    // A host restarting reads only the transcript's tail; a delegate writing on past it hid the
    // failure, and so would hide a reply that cleared it.
    [Theory]
    [InlineData(false, AttentionKind.SignInNeeded)]
    [InlineData(true, null)]
    public async Task A_sign_in_failure_before_the_parsed_tail_is_still_read_with_whatever_cleared_it(bool answered, AttentionKind? expected)
    {
        await using var s = new Session_(tailBytes: 4096);
        var lines = new List<string> { Copilot.User("Carry on", 1), Copilot.TurnStart(2), Error(SignedOut, 3) };
        if (answered) { lines.Add(Copilot.Assistant("Done.", 10)); }
        lines.AddRange(Enumerable.Range(0, 40).Select(i => Copilot.Assistant("Still bisecting " + new string('x', 200), 20 + i, agentId: "agent-1")));

        var session = await s.After([.. lines]);

        Assert.Equal(expected, session.Attention?.Kind);
    }

    [Fact]
    public async Task Another_error_before_the_parsed_tail_is_cleared_by_a_turn_starting_there_too()
    {
        await using var s = new Session_(tailBytes: 4096);
        var lines = new List<string> { Copilot.User("Go", 1), Copilot.TurnStart(2), Error(new { errorType = "session", message = "Turn error: boom" }, 3),
            Copilot.TurnStart(10), Copilot.Assistant("Done.", 11), Copilot.TurnEnd(12) };
        lines.AddRange(Enumerable.Range(0, 40).Select(i => Copilot.Assistant("Still bisecting " + new string('x', 200), 20 + i, agentId: "agent-1")));

        var session = await s.After([.. lines]);

        Assert.Null(session.Attention);
    }

    // The CLI follows a turn's error with a notice of its own, written as an assistant.message.
    [Fact]
    public async Task Another_error_stays_past_the_clis_turn_error_notice_until_the_next_turn()
    {
        await using var s = new Session_();

        var session = await s.After(Copilot.User("Go", 1), Copilot.TurnStart(2), Error(new { errorType = "session", message = "Turn error: too many requests", statusCode = 429 }, 3),
            Copilot.Event("assistant.message", new { messageId = "m4", content = "\u26A0\uFE0F Turn error: too many requests" }, 4), Copilot.TurnEnd(5));

        Assert.Equal(AttentionKind.Error, session.Attention?.Kind);
    }

    // Another error is about the turn it ended: the next one starting leaves it behind.
    [Fact]
    public async Task A_new_prompt_after_another_error_clears_it()
    {
        await using var s = new Session_();
        await s.After(Copilot.User("Go", 1), Copilot.TurnStart(2), Error(new { errorType = "session", message = "Turn error: boom" }, 3));

        var session = await s.After(Copilot.User("Try again", 10));

        Assert.Null(session.Attention);
        Assert.Equal(SessionStatus.Working, session.Status);
    }

    // Any one of VM1's three fields says sign-in, so a CLI that drops one still reads as it.
    [Theory]
    [InlineData("{\"remediation\":\"sign_in\"}")]
    [InlineData("{\"errorType\":\"authorization\"}")]
    [InlineData("{\"errorType\":\"session\",\"statusCode\":401}")]
    public async Task Any_one_of_the_sign_in_fields_alone_reads_as_sign_in_needed(string data)
    {
        await using var s = new Session_();

        var session = await s.After(Copilot.User("Go", 1), Copilot.TurnStart(2), Error(System.Text.Json.JsonDocument.Parse(data).RootElement, 3));

        Assert.Equal(AttentionKind.SignInNeeded, session.Attention?.Kind);
    }

    [Fact]
    public async Task Another_error_that_ends_the_turn_reads_as_an_error_with_its_class()
    {
        await using var s = new Session_();

        var session = await s.After(Copilot.User("Go", 1), Copilot.TurnStart(2),
            Error(new { errorType = "session", message = "Turn error: too many requests", statusCode = 429 }, 3));

        Assert.Equal(SessionStatus.Idle, session.Status);
        Assert.Equal(new Attention { Kind = AttentionKind.Error, Since = Fixture.T0.AddSeconds(3), Agent = AgentKind.Copilot, Message = "Turn error: too many requests", ErrorClass = ErrorClass.Rate }, session.Attention);
    }

    // The CLI retries a failed model call within the turn, and a hook's failure ends nothing.
    [Theory]
    [InlineData("model_call")]
    [InlineData("hook")]
    public async Task An_error_the_cli_does_not_end_the_turn_on_changes_nothing(string type)
    {
        await using var s = new Session_();

        var session = await s.After(Copilot.User("Go", 1), Copilot.TurnStart(2), Error(new { errorType = type, message = "Retrying", statusCode = 401 }, 3));

        Assert.Null(session.Attention);
        Assert.Equal(SessionStatus.Working, session.Status);
    }

    [Fact]
    public async Task A_delegates_error_is_not_the_sessions()
    {
        await using var s = new Session_();

        var session = await s.After(Copilot.User("Go", 1), Copilot.TurnStart(2), Error(SignedOut, 3, agentId: "agent-1"));

        Assert.Null(session.Attention);
        Assert.Equal(SessionStatus.Working, session.Status);
    }

    [Fact]
    public void An_error_message_is_shown_without_tokens_codes_emails_paths_links_or_ids()
    {
        var raw = "Authorization error for mona@example.com (gho_abcdefghijklmnopqrstuvwxyz0123). Request ID C3F4:2B8A1:9F3E0C:A1B2C3:672A1B3C, " +
            "session 0f3c2a1e-9d3b-4e6f-8a7c-5b1d2e3f4a5b. Enter ABCD-1234 at https://github.com/login/device?x=1 or see " +
            @"C:\Users\mona\.copilot\logs\a.log and /home/mona/.copilot/x now." + "\n  Bearer abc.def";

        Assert.Equal("Authorization error for [email] ([token]). Request ID [id], session [id]. Enter [code] at [link] or see [path] and [path] now. [token]",
            AgentErrors.Shown(raw));
    }

    [Fact]
    public void A_quoted_credential_is_taken_out_whole()
    {
        Assert.Equal("Request failed: {\"[token]} and [token] end",
            AgentErrors.Shown("Request failed: {\"password\":\"example-value-7\"} and password = \"a secret \\\"phrase\\\" here\" end"));
    }

    [Fact]
    public void An_authorization_header_is_taken_out_whatever_its_scheme_and_the_word_basic_is_not()
    {
        Assert.Equal("Sent [token] and [token]; basic auth failed",
            AgentErrors.Shown("Sent Authorization: Basic dXNlcjpwYXNz and Basic dTpwMQ==; basic auth failed"));
    }

    [Fact]
    public void A_quoted_path_with_spaces_in_it_is_taken_out_whole()
    {
        Assert.Equal("Cannot read [path] or [path] now",
            AgentErrors.Shown("Cannot read \"C:\\Users\\Example Person\\.copilot\\private.json\" or '/home/Example Person/.copilot/x.json' now"));
    }

    [Fact]
    public void An_error_message_is_cut_to_its_limit_and_a_blank_one_is_none()
    {
        var shown = AgentErrors.Shown("Authorization error. " + new string('x', 1000));

        Assert.StartsWith("Authorization error. xxx", shown);
        Assert.True(Encoding.UTF8.GetByteCount(shown!) <= Limits.AttentionMessage);
        Assert.Null(AgentErrors.Shown(" \n "));
    }
}
