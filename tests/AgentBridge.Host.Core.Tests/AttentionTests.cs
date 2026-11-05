using System.Text;
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

        public Session_()
        {
            Events = F.CopilotSession(Id, 100);
            F.Processes.Run(AgentKind.Copilot, 100);
            Source = F.Source();
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
    public void An_error_message_is_cut_to_its_limit_and_a_blank_one_is_none()
    {
        var shown = AgentErrors.Shown("Authorization error. " + new string('x', 1000));

        Assert.StartsWith("Authorization error. xxx", shown);
        Assert.True(Encoding.UTF8.GetByteCount(shown!) <= Limits.AttentionMessage);
        Assert.Null(AgentErrors.Shown(" \n "));
    }
}
