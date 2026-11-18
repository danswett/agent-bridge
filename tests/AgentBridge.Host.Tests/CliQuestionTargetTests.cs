using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class CliQuestionTargetTests
{
    const string Id = "88888888-8888-4888-8888-888888888888";
    const string QuestionId = "question#selected";
    static readonly Question Question = new()
    {
        Id = QuestionId, Kind = QuestionKind.Form, Message = "Fixture question", AskedAt = DateTimeOffset.UnixEpoch,
    };
    static Session Pending => new()
    {
        Id = Id, Agent = AgentKind.Copilot, Status = SessionStatus.Waiting,
        StatusSince = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch,
        Question = Question, Activity = new() { Turn = "turn-selected", ResponseTurn = "turn-before" },
    };
    readonly Source source = new();
    readonly StringWriter output = new(), error = new();

    Task<int> Run(params string[] args)
    {
        var type = typeof(HostSettings).Assembly.GetType("AgentBridge.Host.QuestionTargetCommands");
        Assert.NotNull(type);
        var method = type.GetMethod("RunAsync");
        Assert.NotNull(method);
        return method.CreateDelegate<Func<IReadOnlyList<string>, Func<ISessionSource>, TextWriter, TextWriter, Task<int>>>()
            (args.Length == 0 ? ["question-target", Id, QuestionId] : args, () => source, output, error);
    }

    string Outcome()
    {
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(Id, json.RootElement.GetProperty("id").GetString());
        Assert.Equal(QuestionId, json.RootElement.GetProperty("question").GetString());
        return json.RootElement.GetProperty("outcome").GetString()!;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cli_question_target_accepts_the_exact_session_and_question_identity(bool knownTurn)
    {
        if (!knownTurn) { source.Current = Pending with { Activity = new() }; }
        Assert.Equal(0, await Run());
        Assert.Equal("pending", Outcome());
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(knownTurn, json.RootElement.TryGetProperty("turn", out var turn));
        if (knownTurn) { Assert.Equal("turn-selected", turn.GetString()); }
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData("replaced", "stale")]
    [InlineData("wrong-session", "stale")]
    [InlineData("prefix", "stale")]
    [InlineData("absent", "no-longer-pending")]
    [InlineData("answered", "answered")]
    [InlineData("approved", "answered")]
    [InlineData("cancelled", "no-longer-pending")]
    [InlineData("missing", "missing")]
    [InlineData("held-missing", "unknown")]
    [InlineData("held", "unknown")]
    [InlineData("not-waiting", "unknown")]
    [InlineData("history-replaced", "stale")]
    [InlineData("history-missing", "missing")]
    public async Task Cli_question_target_refuses_a_replaced_answered_or_missing_question(string scenario, string expected)
    {
        source.Current = scenario switch
        {
            "replaced" => Pending with { Question = Question with { Id = "replacement" } },
            "wrong-session" => Pending with { Id = "other-session" },
            "prefix" => Pending with { Question = Question with { Id = QuestionId + "-suffix" } },
            "missing" or "held-missing" => null,
            "held" => Pending,
            "not-waiting" => Pending with { Status = SessionStatus.Working },
            _ => Pending with { Question = null },
        };
        if (scenario.StartsWith("held", StringComparison.Ordinal)) { source.Held = new("fixture discovery incomplete", [AgentKind.Copilot]); }
        source.History = new()
        {
            Turns = [new() { Id = "turn-selected", At = DateTimeOffset.UnixEpoch, Parts =
                [new() { Id = "part", Kind = HistoryPartKind.Question, At = DateTimeOffset.UnixEpoch, Question = Question,
                    Outcome = scenario == "approved" ? QuestionOutcome.Approved : scenario == "cancelled" ? QuestionOutcome.Cancelled
                        : scenario == "answered" ? QuestionOutcome.Answered : QuestionOutcome.Unknown }] }],
        };
        if (scenario.StartsWith("history-", StringComparison.Ordinal))
        {
            source.AfterHistory = () => source.Current = scenario == "history-missing" ? null : Pending with { Question = Question with { Id = "replacement" } };
        }
        Assert.Equal(1, await Run());
        Assert.Equal(expected, Outcome());
        Assert.Contains(expected, error.ToString());
    }

    [Fact]
    public async Task Cli_question_target_never_answers_or_changes_focus()
    {
        var before = source.Current;
        Assert.Equal(0, await Run());
        Assert.Same(before, source.Current);
        Assert.Equal(["refresh", "get", "dispose"], source.Calls);
        Assert.DoesNotContain(Question.Message, output.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Cli_question_target_rejects_empty_identity_before_discovery(string id)
    {
        Assert.Equal(2, await Run("question-target", id, QuestionId));
        Assert.Empty(source.Calls);
        Assert.Empty(output.ToString());
        Assert.Contains("usage:", error.ToString());
    }

    sealed class Source : ISessionSource
    {
        public Session? Current { get; set; } = Pending;
        public HistoryResult History { get; set; } = HistoryResult.NotAvailable("fixture");
        public Action? AfterHistory { get; set; }
        public List<string> Calls { get; } = [];
        public DiscoveryHeld? Held { get; set; }
        public long Revision => 0;
        public event EventHandler<SessionChange>? Changed { add => throw new NotSupportedException(); remove { } }
        public Session? Get(string id) { Calls.Add("get"); Assert.Equal(Id, id); return Current; }
        public Task RefreshAsync(CancellationToken cancellationToken = default) { Calls.Add("refresh"); return Task.CompletedTask; }
        public Task<HistoryResult> HistoryAsync(string id, string? before, int limit, CancellationToken cancellationToken = default)
        {
            Calls.Add("history"); Assert.Equal(Id, id); Assert.Null(before); Assert.InRange(limit, 1, HistoryLimits.MaxTurns);
            AfterHistory?.Invoke();
            return Task.FromResult(History);
        }
        public IReadOnlyList<Session> Snapshot() => throw new NotSupportedException();
        public SessionList List(string? cursor = null) => throw new NotSupportedException();
        public SessionSubscription Subscribe() => throw new NotSupportedException();
        public Task StartAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { Calls.Add("dispose"); return ValueTask.CompletedTask; }
    }
}
