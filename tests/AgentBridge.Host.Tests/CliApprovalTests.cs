using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

namespace AgentBridge.Host.Tests;

[Trait("Category", "Network")]
public sealed class CliApprovalTests : IAsyncLifetime
{
    const string Id = "88888888-8888-4888-8888-888888888888";
    const string Request = "99999999-9999-4999-8999-999999999999";
    const string QuestionId = "permission#shown";
    readonly string root = Path.Combine(Path.GetTempPath(), "ab-approve-" + Guid.NewGuid().ToString("N")[..8]);
    readonly List<string> operations = [];
    readonly StringWriter output = new(), error = new();
    Question question = new()
    {
        Id = QuestionId, Kind = QuestionKind.Approval, Message = "Allow the displayed operation?", AskedAt = DateTimeOffset.UnixEpoch,
        Fields = [new Field { Name = "decision", Type = FieldType.Choice, Required = true, Options = [new("approve", "Approve once"), new("deny", "Deny")] }],
    };
    AnswerRequest? sent;
    AnswerOutcome outcome = AnswerOutcome.Delivered;
    string scenario = "";
    int reads;
    LocalServer server = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(root, "default"));
        server = LocalServer.Start(LocalEndpoint.For(Path.Combine(root, "default")), (_, op, data) =>
        {
            operations.Add(op);
            object result = op switch
            {
                Ops.Hello => new HelloResult(ProtocolJson.Version, new("fixture", "Fixture", "windows", "test"),
                    scenario == "no-capability" ? [Capabilities.SessionsRead] : [Capabilities.SessionsRead, Capabilities.SessionsApprove]),
                Ops.SessionGet => Read(data),
                Ops.SessionAnswer => Answer(data),
                _ => throw new InvalidOperationException("unexpected operation"),
            };
            return Task.FromResult<object?>(result);
        });
        return Task.CompletedTask;
    }

    Session Read(JsonElement data)
    {
        Assert.Equal(Id, data.GetProperty("id").GetString());
        reads++;
        return new Session
        {
            Id = scenario == "wrong-session" ? "another-session" : Id,
            Agent = scenario == "other-adapter" ? AgentKind.Codex : AgentKind.Copilot,
            Status = SessionStatus.Waiting, StatusSince = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch, Activity = new(),
            Question = scenario == "changed" && reads > 1 ? question with { Message = "A different operation" } : question,
        };
    }

    AnswerResult Answer(JsonElement data)
    {
        sent = data.Deserialize<AnswerRequest>(ProtocolJson.Options)!;
        return new(outcome, "private-sentinel: do not print raw host reasons");
    }

    Task<int> Run(string input = "1", Func<bool>? refused = null) =>
        ApproveCommands.RunAsync(["approve", Id, QuestionId, "--request", Request], root, refused ?? (() => false), new StringReader(input), output, error);

    [Theory]
    [InlineData("1", "approve", AnswerOutcome.Delivered, 0)]
    [InlineData("2", "deny", AnswerOutcome.Unconfirmed, 3)]
    [InlineData("1", "approve", AnswerOutcome.Refused, 4)]
    public async Task Cli_approve_sends_the_current_single_choice_bound_to_session_question_and_request(string input, string choice, AnswerOutcome result, int exit)
    {
        outcome = result;
        var guards = 0;
        Assert.Equal(exit, await Run(input, () => { guards++; return false; }));
        Assert.Equal(2, guards);
        Assert.Equal([Ops.Hello, Ops.SessionGet, Ops.SessionGet, Ops.SessionAnswer], operations);
        Assert.NotNull(sent);
        Assert.Equal((Id, QuestionId, Request), (sent.Id, sent.Question, sent.Request));
        Assert.Equal(("decision", choice), (Assert.Single(sent.Fields!).Key, sent.Fields!["decision"].GetString()));
        Assert.Null(sent.Text);
        Assert.Null(sent.Confirm);
        Assert.Contains(question.Message, output.ToString());
        Assert.Contains("1. Approve once", output.ToString());
        Assert.Contains("2. Deny", output.ToString());
        Assert.DoesNotContain("private-sentinel", output.ToString() + error);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("at-machine")]
    [InlineData("truncated")]
    [InlineData("escaped")]
    [InlineData("form")]
    [InlineData("multiple")]
    [InlineData("wrong-session")]
    [InlineData("other-adapter")]
    [InlineData("changed")]
    [InlineData("no-capability")]
    [InlineData("invalid-choice")]
    [InlineData("person-lost")]
    public async Task Cli_approve_refuses_stale_at_machine_truncated_and_escaped_prompts(string reason)
    {
        scenario = reason;
        question = reason switch
        {
            "stale" => question with { Id = "replaced" },
            "at-machine" => question with { AnswerAtMachine = true },
            "truncated" => question with { Truncated = true },
            "escaped" => question with { Escaped = new() { Shown = "shown", Marks = [[0, 6]], Approve = EscapedApproval.Confirm } },
            "form" => question with { Kind = QuestionKind.Form },
            "multiple" => question with { Fields = [.. question.Fields, question.Fields[0] with { Name = "another" }] },
            _ => question,
        };
        var guards = 0;
        Assert.Equal(4, await Run(reason == "invalid-choice" ? "3" : "1", () => ++guards > 1 && reason == "person-lost"));
        Assert.Null(sent);
        Assert.DoesNotContain(Ops.SessionAnswer, operations);
        Assert.NotEqual("", error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cli_approve_from_an_agent_child_sends_nothing(bool partial)
    {
        var ancestry = new ProcessAncestry([new PeerProcess(1, null, partial ? "fixture" : "copilot")], !partial);
        Assert.Equal(4, await Run(refused: () => AgentAncestry.Inside(null, ancestry, () => []) is not null));
        Assert.Empty(operations);
        Assert.Null(sent);
        Assert.NotEqual("", error.ToString());
    }

    public async Task DisposeAsync()
    {
        await server.DisposeAsync();
        Directory.Delete(root, true);
    }
}
