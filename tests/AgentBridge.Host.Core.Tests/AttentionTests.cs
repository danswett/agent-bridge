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

        public Session_(long tailBytes = 4 * 1024 * 1024, long maxSkim = 512L * 1024 * 1024)
        {
            Events = F.CopilotSession(Id, 100);
            F.Processes.Run(AgentKind.Copilot, 100);
            Source = F.Source(tailBytes: tailBytes, maxSkim: maxSkim);
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
    public async Task Recovery_overlay_stays_pending_until_model_proof_and_rejects_stale_flow_or_error_callbacks()
    {
        await using var s = new Session_();
        await s.After(Error(SignedOut, 2));
        var source = s.Source;
        var failure = source.Get(Id)!.Attention!.Since;
        await source.SetSignInRecoveryAsync(Id, failure, "old-flow", RecoveryState.Checking, true);
        await source.SetSignInRecoveryAsync(Id, failure, "new-flow", RecoveryState.Pending, true);
        await source.SetSignInRecoveryAsync(Id, failure, "old-flow", RecoveryState.Failed);
        Assert.Equal(RecoveryState.Pending, source.Get(Id)!.Attention!.Recovery);
        await s.After(Copilot.User("try again", 3), Copilot.TurnStart(4), Copilot.TurnEnd(5),
            Copilot.Assistant("", 6), Copilot.Assistant("⚠️ Turn error: login needed", 7));
        Assert.Equal(RecoveryState.Pending, source.Get(Id)!.Attention!.Recovery);
        await s.After(Error(SignedOut, 8));
        await source.SetSignInRecoveryAsync(Id, failure, "new-flow", RecoveryState.Pending);
        Assert.Null(source.Get(Id)!.Attention!.Recovery);
        var latest = source.Get(Id)!.Attention!.Since;
        await source.SetSignInRecoveryAsync(Id, latest, "latest", RecoveryState.Pending, true);
        await s.After(Copilot.Assistant("real model content", 9));
        Assert.Null(source.Get(Id)!.Attention);
        await source.SetSignInRecoveryAsync(Id, latest, "latest", RecoveryState.Pending);
        Assert.Null(source.Get(Id)!.Attention);
    }

    [Fact]
    public async Task A_turn_that_ended_on_copilots_authorization_error_leaves_the_session_idle_and_needing_sign_in()
    {
        await using var s = new Session_();

        var session = await s.After(Copilot.User("Carry on with the release", 1), Copilot.TurnStart(2), Error(SignedOut, 3));

        Assert.Equal(SessionStatus.Idle, session.Status);
        Assert.Equal(new Attention { Kind = AttentionKind.SignInNeeded, Since = Fixture.T0.AddSeconds(3), Agent = AgentKind.Copilot, Provider = "github", Message = "Copilot needs you to sign in again (401)." }, session.Attention);
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
            Copilot.Event("assistant.message", new { messageId = "m4", content = "\u26A0\uFE0F Turn error: too many requests" }, 4));

        Assert.Equal(AttentionKind.Error, session.Attention?.Kind);
        Assert.Equal(SessionStatus.Idle, session.Status);
    }

    // Memory and a skill's context come within a turn and start none.
    [Theory]
    [InlineData("memory")]
    [InlineData("skill-release")]
    public async Task Context_the_cli_adds_after_another_error_leaves_it(string source)
    {
        await using var s = new Session_();

        var session = await s.After(Copilot.User("Go", 1), Copilot.TurnStart(2), Error(new { errorType = "session", message = "Turn error: too many requests", statusCode = 429 }, 3),
            Copilot.Event("user.message", new { content = "Context.", source }, 4));

        Assert.Equal(AttentionKind.Error, session.Attention?.Kind);
    }

    [Fact]
    public async Task A_failed_main_turn_leaves_a_live_delegates_permission_prompt_for_the_person()
    {
        await using var s = new Session_();

        var session = await s.After(Copilot.User("Go", 1), Copilot.TurnStart(2), Copilot.ToolStart("c1", "powershell", new { }, 3, agentId: "agent-1"),
            Copilot.Permission("r1", "c1", Copilot.Prompts["commands"], 4, agentId: "agent-1"), Error(SignedOut, 5));

        Assert.Equal("agent-1", session.Question?.Agent);
        Assert.Equal(AttentionKind.SignInNeeded, session.Attention?.Kind);
    }

    // Past MaxSkimBytes the host reads nothing until the tail. Bytes never read prove nothing (a
    // retry loop writes plenty with no reply), so a failure skimmed before them is kept, and only
    // what the tail itself shows clears or refreshes it (#800 ruling).
    static List<string> SkimmedFailureThenUnread()
    {
        var lines = new List<string> { Copilot.User("Carry on", 1), Copilot.TurnStart(2), Error(SignedOut, 3) };
        lines.AddRange(Enumerable.Range(0, 60).Select(i => Copilot.Assistant("Still bisecting " + new string('x', 200), 20 + i, agentId: "agent-1")));
        return lines;
    }

    [Fact]
    public async Task A_skimmed_failure_is_cleared_by_a_model_reply_in_the_tail_read_after_an_unread_stretch()
    {
        await using var s = new Session_(tailBytes: 4096, maxSkim: 2048);
        var lines = SkimmedFailureThenUnread();
        lines.Add(Copilot.Assistant("Done.", 100));

        Assert.Null((await s.After([.. lines])).Attention);
    }

    [Fact]
    public async Task A_skimmed_failure_followed_by_a_new_401_in_the_tail_holds_from_the_new_one()
    {
        await using var s = new Session_(tailBytes: 4096, maxSkim: 2048);
        var lines = SkimmedFailureThenUnread();
        lines.Add(Error(new { errorType = "session", message = "Unauthorized", statusCode = 401 }, 100));

        var session = await s.After([.. lines]);

        Assert.Equal(AttentionKind.SignInNeeded, session.Attention?.Kind);
        Assert.Equal(Fixture.T0.AddSeconds(100), session.Attention?.Since);
    }

    [Fact]
    public async Task A_skimmed_failure_holds_when_the_tail_has_only_turn_ends_hooks_delegates_and_context()
    {
        await using var s = new Session_(tailBytes: 4096, maxSkim: 2048);
        var lines = SkimmedFailureThenUnread();
        lines.AddRange([Copilot.TurnEnd(100), Copilot.Event("hook.start", new { }, 101), Copilot.Event("hook.end", new { }, 102),
            Copilot.Assistant("Delegate done.", 103, agentId: "agent-1"), Copilot.Event("user.message", new { content = "Context.", source = "memory" }, 104)]);

        var session = await s.After([.. lines]);

        Assert.Equal(AttentionKind.SignInNeeded, session.Attention?.Kind);
        Assert.Equal(Fixture.T0.AddSeconds(3), session.Attention?.Since);
    }

    // read_agent's arguments name an agentId: the main reply asking for it was skimmed past, and
    // a host restart kept the sign-in it had answered.
    [Fact]
    public async Task A_skimmed_model_tool_request_naming_an_agent_still_clears_sign_in()
    {
        await using var s = new Session_(tailBytes: 4096);
        var lines = new List<string> { Copilot.User("Carry on", 1), Copilot.TurnStart(2), Error(SignedOut, 3),
            Copilot.Event("assistant.message", new { messageId = "m10", content = "", toolRequests = new[] { new { toolCallId = "t1", name = "read_agent", arguments = new { agentId = "agent-1" } } } }, 10) };
        lines.AddRange(Enumerable.Range(0, 40).Select(i => Copilot.Assistant("Still bisecting " + new string('x', 200), 20 + i, agentId: "agent-1")));

        Assert.Null((await s.After([.. lines])).Attention);
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
        Assert.Equal(new Attention { Kind = AttentionKind.Error, Since = Fixture.T0.AddSeconds(3), Agent = AgentKind.Copilot, Message = "Copilot hit a rate limit (429).", ErrorClass = ErrorClass.Rate }, session.Attention);
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

    // #841: attention carries no agent text at all, only the host's sentence for the fields: a
    // redaction blocklist missed a new shape every round (camelCase keys the last).
    const string Secrets = "Authorization: Bearer Bearer abc123 db_password=hunter2x GITHUB_TOKEN=ghx1 {\"dbPassword\":\"camel secret\"} authToken=camel2 userPassword: p4ss mona@example.com C:\\Users\\mona\\notes.txt";

    [Theory]
    [InlineData("authorization", 401, "Copilot needs you to sign in again (401).")]
    [InlineData("session", 429, "Copilot hit a rate limit (429).")]
    [InlineData("session", 403, "Copilot was not authorised (403).")]
    [InlineData("execution", null, "Copilot stopped with an error.")]
    public async Task An_error_full_of_secrets_reaches_attention_only_as_the_hosts_sentence(string type, int? status, string message)
    {
        await using var s = new Session_();
        var data = new System.Text.Json.Nodes.JsonObject { ["errorType"] = type, ["message"] = Secrets };
        if (status is { } code) { data["statusCode"] = code; }

        var session = await s.After(Copilot.User("Go", 1), Copilot.TurnStart(2), Error(data, 3));

        Assert.Equal(message, session.Attention?.Message);
        var json = System.Text.Json.JsonSerializer.Serialize(session.Attention, ProtocolJson.Options);
        foreach (var piece in new[] { "abc123", "hunter2x", "ghx1", "camel", "p4ss", "mona", "notes" }) { Assert.DoesNotContain(piece, json); }
    }

    // #846: each shape a redaction blocklist missed, in the CLI's turn-error notice. None of the
    // secret reaches the session as any client sees it, read as written or skimmed on a cold read.
    // Values are obviously fake; CI masks any that matches a repository secret.
    public static TheoryData<string, string[]> Missed => new()
    {
        { "Request failed: {\"dbPassword\":\"fake-camel-1\"}", ["fake-camel-1"] },
        { "Request failed: authToken=fakecamel2", ["fakecamel2"] },
        { "Request failed: githubToken=fakecamel3", ["fakecamel3"] },
        { "Request failed: sessionToken: fakecamel4", ["fakecamel4"] },
        { "Request failed: userPassword=fakecamel5", ["fakecamel5"] },
        { "Request failed: aws secret Zq8vR2/xT5nWk9pL3/hY7mB1cD4fG6jK0sA2eU8i", ["Zq8vR2", "xT5nWk9pL3", "hY7mB1cD4fG6jK0sA2eU8i"] },
        { "Request failed: key AKIAFAKEFAKEFAKE0123 and ASIAFAKEFAKEFAKE4567", ["AKIAFAKEFAKEFAKE0123", "ASIAFAKEFAKEFAKE4567"] },
        { "Request failed: rejected qWeRtYuIoPaSdFgHjKlZxCvBnMqWeRt", ["qWeRtYuIoPaSdFgHjKlZxCvBnMqWeRt"] },
        { "Request failed: -----BEGIN " + "PRIVATE KEY----- MIIfakeFAKEpemBODYline1 fakePemBodyLine2 -----END " + "PRIVATE KEY-----", ["MIIfakeFAKEpemBODYline1", "fakePemBodyLine2"] },
        { "Request failed: bea" + "rer fakeabc;fakedef", ["fakeabc", "fakedef"] },
    };

    [Theory]
    [MemberData(nameof(Missed))]
    public async Task A_secret_in_the_clis_turn_error_notice_never_reaches_the_session(string message, string[] secret)
    {
        await using var s = new Session_();

        var session = await s.After(Copilot.User("Go", 1), Copilot.TurnStart(2), Error(new { errorType = "session", message }, 3),
            Copilot.Event("assistant.message", new { messageId = "m4", content = "\u26A0\uFE0F Turn error: " + message }, 4));

        Assert.StartsWith("\u26A0\uFE0F Turn error:", session.Activity.Own?.Response ?? session.Activity.Response);
        var json = System.Text.Json.JsonSerializer.Serialize(session, ProtocolJson.Options);
        foreach (var piece in secret) { Assert.DoesNotContain(piece, json); }
    }

    [Theory]
    [MemberData(nameof(Missed))]
    public async Task A_secret_in_the_clis_turn_error_notice_never_reaches_the_session_on_a_cold_read(string message, string[] secret)
    {
        await using var s = new Session_(tailBytes: 4096);
        var lines = new List<string> { Copilot.User("Go", 1), Copilot.TurnStart(2), Error(new { errorType = "session", message }, 3),
            Copilot.Event("assistant.message", new { messageId = "m4", content = "\u26A0\uFE0F Turn error: " + message }, 4) };
        lines.AddRange(Enumerable.Range(0, 40).Select(i => Copilot.Assistant("Still bisecting " + new string('x', 200), 20 + i, agentId: "agent-1")));

        var session = await s.After([.. lines]);

        Assert.StartsWith("\u26A0\uFE0F Turn error:", session.Activity.Own?.Response ?? session.Activity.Response);
        var json = System.Text.Json.JsonSerializer.Serialize(session, ProtocolJson.Options);
        foreach (var piece in secret) { Assert.DoesNotContain(piece, json); }
    }

    // #846: the notice is the host's sentence for the error that ended the turn, as attention's.
    [Theory]
    [InlineData("authorization", 401, "Copilot needs you to sign in again (401).")]
    [InlineData("session", 429, "Copilot hit a rate limit (429).")]
    [InlineData("execution", null, "Copilot stopped with an error.")]
    public async Task The_clis_turn_error_notice_is_shown_as_the_hosts_sentence_for_its_error(string type, int? status, string sentence)
    {
        await using var s = new Session_();
        var data = new JsonObject { ["errorType"] = type, ["message"] = "Turn error: boom" };
        if (status is { } code) { data["statusCode"] = code; }

        var session = await s.After(Copilot.User("Go", 1), Copilot.TurnStart(2), Error(data, 3),
            Copilot.Event("assistant.message", new { messageId = "m4", content = "\u26A0\uFE0F Turn error: token=abc123secret at C:\\Users\\mona\\x.log" }, 4));

        Assert.Equal("\u26A0\uFE0F Turn error: " + sentence, session.Activity.Response);
    }

    // Past the skim budget, a later turn may have started and failed in the bytes never read.
    [Fact]
    public async Task A_turn_error_notice_after_an_unread_stretch_never_names_an_error_the_skim_read()
    {
        await using var s = new Session_(tailBytes: 4096, maxSkim: 2048);
        var lines = new List<string> { Copilot.User("Go", 1), Copilot.TurnStart(2), Error(new { errorType = "session", message = "Turn error: boom", statusCode = 429 }, 3) };
        lines.AddRange(Enumerable.Range(0, 60).Select(i => Copilot.Assistant("Still bisecting " + new string('x', 200), 20 + i, agentId: "agent-1")));
        lines.Add(Copilot.Event("assistant.message", new { messageId = "m100", content = "\u26A0\uFE0F Turn error: something else" }, 100));

        var session = await s.After([.. lines]);

        Assert.Equal("\u26A0\uFE0F Turn error: Copilot stopped with an error.", session.Activity.Own?.Response ?? session.Activity.Response);
    }

    // An error is its own turn's: a notice in a later turn, with none read before it, names none.
    [Fact]
    public async Task A_turn_error_notice_never_names_an_earlier_turns_error()
    {
        await using var s = new Session_();

        var session = await s.After(Copilot.User("Go", 1), Copilot.TurnStart(2), Error(new { errorType = "session", message = "Turn error: boom", statusCode = 429 }, 3),
            Copilot.User("Try again", 10), Copilot.TurnStart(11),
            Copilot.Event("assistant.message", new { messageId = "m12", content = "\u26A0\uFE0F Turn error: something else" }, 12));

        Assert.Equal("\u26A0\uFE0F Turn error: Copilot stopped with an error.", session.Activity.Response);
    }

    // C (#800, 6646501884 §1): the Not responding watchdog. The fixture's clock reads an hour after
    // T0, so these transcripts are written at that hour.
    const int Now = 3600;

    static async Task<(Fixture F, string Events, AgentBridge.Host.Core.SessionSource Source)> Watched()
    {
        var f = new Fixture();
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        var source = f.Source(stalledAfter: TimeSpan.FromMinutes(10));
        await source.RefreshAsync();
        return (f, events, source);
    }

    static async Task<Session> Read((Fixture F, string Events, AgentBridge.Host.Core.SessionSource Source) w, TimeSpan after, params string[] lines)
    {
        w.F.Time.Advance(after);
        Fixture.Append(w.Events, lines);
        await w.Source.RefreshAsync();
        return w.Source.Get(Id)!;
    }

    [Fact]
    public async Task A_working_session_silent_for_ten_minutes_reads_not_responding_until_it_writes_anything_again()
    {
        var w = await Watched();
        using var __ = w.F;
        await using var _ = w.Source;
        Assert.Null((await Read(w, TimeSpan.Zero, Copilot.User("Carry on", Now), Copilot.TurnStart(Now))).Attention);
        Assert.Null((await Read(w, TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1))).Attention);

        var stalled = await Read(w, TimeSpan.FromSeconds(1));

        Assert.Equal(SessionStatus.Working, stalled.Status);
        var at = Fixture.T0.AddSeconds(Now);
        Assert.Equal(new Attention { Kind = AttentionKind.NotResponding, Since = at, Agent = AgentKind.Copilot, Message = "Copilot isn't responding.", LastEventAt = at }, stalled.Attention);
        // A model call changes nothing a client shows, and is still the CLI at work.
        Assert.Null((await Read(w, TimeSpan.Zero, Copilot.Event("model.model_call_started", new { kind = "model_call_started" }, Now + 601))).Attention);
    }

    [Fact]
    public async Task A_session_running_a_tool_reads_not_responding_only_after_thirty_minutes_and_an_idle_one_never()
    {
        var w = await Watched();
        using var __ = w.F;
        await using var _ = w.Source;
        await Read(w, TimeSpan.Zero, Copilot.User("Run the build", Now), Copilot.TurnStart(Now), Copilot.ToolStart("c1", "powershell", new { command = "dotnet build" }, Now));
        Assert.Null((await Read(w, TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(1))).Attention);
        Assert.Equal(AttentionKind.NotResponding, (await Read(w, TimeSpan.FromSeconds(1))).Attention?.Kind);

        var idle = await Read(w, TimeSpan.Zero, Copilot.ToolDone("c1", Now + 1800), Copilot.Assistant("Built.", Now + 1800), Copilot.TurnEnd(Now + 1800));
        Assert.Equal((SessionStatus.Idle, (AttentionKind?)null), (idle.Status, idle.Attention?.Kind));
        Assert.Null((await Read(w, TimeSpan.FromHours(2))).Attention);
    }

    // Claude's delegate writes only its own transcript beside the session's: the session's own goes
    // quiet for as long as the delegate works, and that is no stall.
    [Fact]
    public async Task A_Claude_session_whose_delegate_is_still_writing_is_not_called_not_responding()
    {
        using var f = new Fixture();
        await using var source = f.Source(stalledAfter: TimeSpan.FromMinutes(10));
        const string claude = "c1a0de00-0000-4000-8000-000000000001";
        var transcript = f.ClaudeTranscript(claude);
        Fixture.Append(transcript, Claude.User("Review the PR", Now),
            Claude.Assistant(Now, "tool_use", Claude.ToolUse("toolu_1", "Task", new System.Text.Json.Nodes.JsonObject { ["description"] = "review" })));
        f.ClaudeSessionFile(700, claude);
        f.Processes.Run(AgentKind.Claude, 700);
        var delegates = Path.Combine(Path.GetDirectoryName(transcript)!, claude, "subagents");
        Directory.CreateDirectory(delegates);
        var own = Path.Combine(delegates, "agent-a1.jsonl");
        File.WriteAllText(own, "{}\n");

        f.Time.Advance(TimeSpan.FromMinutes(31));
        File.SetLastWriteTimeUtc(own, f.Time.GetUtcNow().AddMinutes(-1).UtcDateTime);
        await source.RefreshAsync();
        Assert.Equal((SessionStatus.Working, (AttentionKind?)null), (source.Get(claude)!.Status, source.Get(claude)!.Attention?.Kind));

        f.Time.Advance(TimeSpan.FromMinutes(30));
        await source.RefreshAsync();
        Assert.Equal(AttentionKind.NotResponding, source.Get(claude)!.Attention?.Kind);
    }

    // A Codex sub-agent is a thread with a rollout of its own: the session's waits quietly on it.
    [Fact]
    public async Task A_Codex_session_whose_sub_agent_is_still_writing_its_own_rollout_is_not_called_not_responding()
    {
        await using var r = new CodexSubagentTests.Replay(stalledAfter: TimeSpan.FromMinutes(10));
        var s = await r.Through(15);
        Assert.Equal((SessionStatus.Working, SubagentStatus.Working), (s.Status, Assert.Single(s.Subagents!).Status));
        if (!File.Exists(r.ThreadRollout)) { File.WriteAllText(r.ThreadRollout, ""); }

        // The fixture's clock reads an hour after T0: the session's rollout is that old.
        File.SetLastWriteTimeUtc(r.ThreadRollout, Fixture.T0.AddMinutes(59).UtcDateTime);
        Assert.Null((await r.Append()).Attention);

        File.SetLastWriteTimeUtc(r.ThreadRollout, Fixture.T0.AddSeconds(15).UtcDateTime);
        Assert.Equal(AttentionKind.NotResponding, (await r.Append()).Attention?.Kind);
    }
}
