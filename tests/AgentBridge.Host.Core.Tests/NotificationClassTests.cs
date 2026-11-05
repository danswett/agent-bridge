using System.Text.Json;
using AgentBridge.Protocol;
using Xunit;
using NotificationClasses = AgentBridge.Host.Core.NotificationClasses;

// session.class (#913): the host's one notification policy, so every client follows the same tiers.
// Needs you interrupts, Done waits, FYI never notifies; delegates roll up into their parent session.
public class NotificationClassTests
{
    const string Id = "91391391-3913-4913-8913-913913913913";
    static readonly DateTimeOffset T = Fixture.T0;

    static readonly object SignedOut = new { errorType = "authorization", message = "Authorization error.", statusCode = 401, remediation = "sign_in" };

    static Session S(SessionStatus status, string? response = null, Question? question = null, Attention? attention = null,
        ActivitySource? source = null, OwnActivity? own = null, Background? background = null) => new()
    {
        Id = Id,
        Agent = AgentKind.Copilot,
        Status = status,
        StatusSince = T,
        UpdatedAt = T,
        Activity = new Activity { Response = response, Source = source, Own = own, Background = background, TurnCount = 1 },
        Question = question,
        WaitingOn = question?.Agent,
        Attention = attention,
    };

    static Question Q(QuestionKind kind = QuestionKind.Form, string? agent = null) =>
        new() { Id = "q1", Kind = kind, Message = "Which database?", AskedAt = T, Agent = agent };

    [Theory]
    [InlineData(QuestionKind.Form)]
    [InlineData(QuestionKind.Approval)]
    public void A_question_or_an_approval_needs_the_person(QuestionKind kind) =>
        Assert.Equal(NotificationClass.NeedsYou, NotificationClasses.Of(S(SessionStatus.Waiting, response: "Earlier answer.", question: Q(kind))));

    [Fact]
    public void A_delegates_question_makes_its_parent_session_need_the_person()
    {
        var session = S(SessionStatus.Waiting, question: Q(QuestionKind.Approval, agent: "agent-1")) with
        {
            Subagents = [new Subagent { Id = "agent-1", Kind = SubagentKind.Task, Name = "Run the tests", Status = SubagentStatus.Waiting }],
        };

        Assert.Equal(NotificationClass.NeedsYou, NotificationClasses.Of(session));
    }

    [Theory]
    [InlineData(AttentionKind.SignInNeeded, SessionStatus.Idle)]
    [InlineData(AttentionKind.Error, SessionStatus.Idle)]
    [InlineData(AttentionKind.NotResponding, SessionStatus.Working)]
    public void Attention_needs_the_person_even_beside_the_previous_turns_answer(AttentionKind kind, SessionStatus status) =>
        Assert.Equal(NotificationClass.NeedsYou, NotificationClasses.Of(S(status, response: "The previous turn's answer.", attention: new Attention { Kind = kind, Since = T })));

    // Delegates still running do not hold it back, as they do not hold back a finished push.
    [Fact]
    public void A_turn_that_ended_with_the_main_agents_own_answer_is_done_delegating_or_not()
    {
        Assert.Equal(NotificationClass.Done, NotificationClasses.Of(S(SessionStatus.Idle, response: "Postgres it is.")));
        Assert.Equal(NotificationClass.Done, NotificationClasses.Of(S(SessionStatus.Idle, response: "Started both delegates.", background: new Background { Agents = 2, Shells = 0 })));
    }

    [Fact]
    public void A_delegates_words_alone_never_make_the_session_done()
    {
        var delegates = new ActivitySource { Response = "agent-1" };

        Assert.Equal(NotificationClass.Fyi, NotificationClasses.Of(S(SessionStatus.Idle, response: "Delegate: 3 tests fixed.", source: delegates)));
        Assert.Equal(NotificationClass.Done, NotificationClasses.Of(S(SessionStatus.Idle, response: "Delegate: 3 tests fixed.", source: delegates, own: new OwnActivity { Response = "All green." })));
    }

    [Fact]
    public void Working_and_silent_sessions_are_fyi()
    {
        Assert.Equal(NotificationClass.Fyi, NotificationClasses.Of(S(SessionStatus.Working, response: "Halfway there.")));
        Assert.Equal(NotificationClass.Fyi, NotificationClasses.Of(S(SessionStatus.Idle, background: new Background { Agents = 2, Shells = 0 })));
        Assert.Equal(NotificationClass.Fyi, NotificationClasses.Of(S(SessionStatus.Idle)));
    }

    [Fact]
    public void A_session_being_ended_is_fyi_whatever_it_was_asking() =>
        Assert.Equal(NotificationClass.Fyi, NotificationClasses.Of(S(SessionStatus.Ending, question: Q())));

    [Fact]
    public async Task The_host_sends_each_session_classed_as_its_turn_runs_finishes_and_fails()
    {
        using var f = new Fixture();
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source();

        Fixture.Append(events, Copilot.User("Pick the session store", 1), Copilot.TurnStart(2));
        await source.RefreshAsync();
        Assert.Equal(NotificationClass.Fyi, source.Get(Id)!.Class);

        Fixture.Append(events, Copilot.Assistant("Postgres it is.", 3), Copilot.TurnEnd(4));
        await source.RefreshAsync();
        Assert.Equal(NotificationClass.Done, source.Get(Id)!.Class);
        Assert.Equal(source.Get(Id)!.StatusSince, source.Get(Id)!.ClassSince);

        Fixture.Append(events, Copilot.User("Now migrate it", 5), Copilot.TurnStart(6), Copilot.Event("session.error", SignedOut, 7));
        await source.RefreshAsync();
        Assert.Equal(NotificationClass.NeedsYou, source.Get(Id)!.Class);
        Assert.Equal(source.Get(Id)!.Attention!.Since, source.Get(Id)!.ClassSince);
    }

    // The 15-minute reminder (#913 q9) counts from when the person was first needed.
    [Fact]
    public void Needs_you_counts_from_its_oldest_pending_reason_and_every_other_tier_from_its_status()
    {
        var both = S(SessionStatus.Waiting, question: Q() with { AskedAt = T.AddMinutes(1) },
            attention: new Attention { Kind = AttentionKind.Error, Since = T.AddMinutes(3) }) with { StatusSince = T.AddMinutes(2) };
        var done = S(SessionStatus.Idle, response: "Postgres it is.") with { StatusSince = T.AddMinutes(5) };

        Assert.Equal(T.AddMinutes(1), NotificationClasses.Since(both, NotificationClasses.Of(both)));
        Assert.Equal(T.AddMinutes(5), NotificationClasses.Since(done, NotificationClasses.Of(done)));
    }

    // #913 q10: a tier from a newer host is never guessed at, so the client keeps today's rules.
    [Fact]
    public void A_class_a_client_does_not_know_reads_as_absent()
    {
        const string json = """{"id":"x","agent":"copilot","status":"idle","statusSince":"2026-10-28T04:22:43.182Z","updatedAt":"2026-10-28T04:22:43.182Z","activity":{},"class":"urgent"}""";

        Assert.Null(JsonSerializer.Deserialize<Session>(json, ProtocolJson.Options)!.Class);
        Assert.Null(Received.Read<Session>(JsonDocument.Parse(json).RootElement).Class);
        Assert.Null(Received.Read<Session>(JsonDocument.Parse(json.Replace("\"urgent\"", "{\"tier\":1}")).RootElement).Class);
        Assert.Equal(NotificationClass.Done, JsonSerializer.Deserialize<Session>(json.Replace("urgent", "done"), ProtocolJson.Options)!.Class);
    }
}
