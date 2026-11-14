using System.Text.Json;
using System.Text.Json.Serialization;
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
        var json = new JsonSerializerOptions(ProtocolJson.Options);
        json.Converters.Add(new ReplyWriter(() => scenario));
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
        }, json: json);
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
            Question = scenario == "missing" ? null : scenario == "changed" && reads > 1 ? question with { Message = "A different operation" } : question,
        };
    }

    AnswerResult Answer(JsonElement data)
    {
        sent = data.Deserialize<AnswerRequest>(ProtocolJson.Options)!;
        return new(outcome, "private-sentinel: do not print raw host reasons");
    }

    Task<int> Run(string choice = "approve", Func<bool>? refused = null)
    {
        string[] args = ["approve", Id, QuestionId, scenario == "bad-request" ? "invalid" : Request, choice];
        if (scenario == "bad-profile") { args = [.. args, "--profile", ".."]; }
        return ApproveCommands.RunAsync(args, root, refused ?? (() => false), output, error);
    }

    [Theory]
    [InlineData("approve", AnswerOutcome.Delivered, 0)]
    [InlineData("deny", AnswerOutcome.Unconfirmed, 3)]
    [InlineData("approve", AnswerOutcome.Refused, 4)]
    [InlineData("approve", (AnswerOutcome)int.MaxValue, 3)]
    public async Task Cli_approve_sends_the_current_single_choice_bound_to_session_question_and_request(string choice, AnswerOutcome result, int exit)
    {
        outcome = result;
        var guards = 0;
        Assert.Equal(exit, await Run(choice, () => { guards++; return false; }));
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
    [InlineData("missing")]
    [InlineData("delegated")]
    [InlineData("bad-request")]
    [InlineData("bad-profile")]
    [InlineData("at-machine")]
    [InlineData("truncated")]
    [InlineData("escaped")]
    [InlineData("form")]
    [InlineData("multiple")]
    [InlineData("hidden")]
    [InlineData("field-text")]
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
            "delegated" => question with { Agent = "delegate" },
            "at-machine" => question with { AnswerAtMachine = true },
            "truncated" => question with { Truncated = true },
            "escaped" => question with { Escaped = new() { Shown = "shown", Marks = [[0, 6]], Approve = EscapedApproval.Confirm } },
            "form" => question with { Kind = QuestionKind.Form },
            "multiple" => question with { Fields = [.. question.Fields, question.Fields[0] with { Name = "another" }] },
            "hidden" => question with { Message = "\u001b[2JHidden operation" },
            "field-text" => question with { Fields = [question.Fields[0] with { Description = "Unrendered instruction" }] },
            _ => question,
        };
        var guards = 0;
        Assert.Equal(reason is "bad-request" or "bad-profile" ? 2 : 4, await Run(reason == "invalid-choice" ? "3" : "approve", () => ++guards > 1 && reason == "person-lost"));
        Assert.Null(sent);
        Assert.DoesNotContain(Ops.SessionAnswer, operations);
        Assert.NotEqual("", error.ToString());
        Assert.DoesNotContain("\u001b", output.ToString());
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

    [Theory]
    [InlineData("missing-ok", 3)]
    [InlineData("string-ok", 3)]
    [InlineData("surrogate-outcome", 3)]
    [InlineData("false-no-error", 3)]
    [InlineData("host-rejected", 4)]
    public async Task Cli_approve_distinguishes_malformed_replies_from_explicit_rejection_without_retry(string reply, int exit)
    {
        scenario = reply;
        Assert.Equal(exit, await Run());
        Assert.NotNull(sent);
        Assert.Equal([Ops.Hello, Ops.SessionGet, Ops.SessionGet, Ops.SessionAnswer], operations);
        Assert.Equal(exit == 3 ? "unconfirmed" : "refused: host declined approval", error.ToString().Trim());
        Assert.DoesNotContain("delivered", output.ToString());
    }

    sealed class ReplyWriter(Func<string> scenario) : JsonConverter<object>
    {
        public override object Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            var envelope = JsonSerializer.SerializeToElement(value, ProtocolJson.Options);
            var raw = envelope.GetRawText();
            if (envelope.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("outcome", out _))
            {
                raw = scenario() switch
                {
                    "missing-ok" => raw.Replace("\"ok\":true,", ""),
                    "string-ok" => raw.Replace("\"ok\":true", "\"ok\":\"true\""),
                    "surrogate-outcome" => raw.Replace("\"outcome\":\"delivered\"", "\"outcome\":\"\\uD800\""),
                    "false-no-error" => JsonSerializer.Serialize(new { re = envelope.GetProperty("re").GetInt64(), ok = false }, ProtocolJson.Options),
                    "host-rejected" => JsonSerializer.Serialize(new { re = envelope.GetProperty("re").GetInt64(), ok = false, error = "refused" }, ProtocolJson.Options),
                    _ => raw,
                };
            }
            writer.WriteRawValue(raw);
        }
    }

    public async Task DisposeAsync()
    {
        await server.DisposeAsync();
        Directory.Delete(root, true);
    }
}
