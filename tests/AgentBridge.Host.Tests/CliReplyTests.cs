using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

namespace AgentBridge.Host.Tests;

[Trait("Category", "Network")]
public sealed class CliReplyTests : IAsyncLifetime
{
    const string Id = "88888888-8888-4888-8888-888888888888";
    const string Request = "99999999-9999-4999-8999-999999999999";
    readonly string root = Path.Combine(Path.GetTempPath(), "ab-reply-" + Guid.NewGuid().ToString("N")[..8]);
    readonly List<string> operations = [];
    readonly StringWriter output = new(), error = new();
    ReplyRequest? sent;
    Question? question;
    string receipt = "delivered";
    bool disconnect, capability = true;
    LocalServer server = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(root, "selected"));
        server = LocalServer.Start(LocalEndpoint.For(Path.Combine(root, "selected")), async (channel, op, data) =>
        {
            operations.Add(op);
            if (op == Ops.Hello)
            {
                return new HelloResult(ProtocolJson.Version, new("fixture", "Fixture", "windows", "test"),
                    capability ? [Capabilities.SessionsRead, Capabilities.SessionsReply] : [Capabilities.SessionsRead]);
            }
            if (op == Ops.SessionGet)
            {
                Assert.Equal(Id, data.GetProperty("id").GetString());
                return new Session
                {
                    Id = Id, Agent = AgentKind.Copilot, Status = SessionStatus.Idle, Question = question,
                    StatusSince = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch, Activity = new(),
                };
            }
            Assert.Equal(Ops.SessionReply, op);
            sent = data.Deserialize<ReplyRequest>(ProtocolJson.Options);
            if (disconnect) { await ((LocalChannel)channel).DisposeAsync(); }
            return new { outcome = receipt, reason = "private-sentinel: do not print raw host content" };
        });
        return Task.CompletedTask;
    }

    async Task<int> Run(Func<bool>? refused = null, string text = "Continue with the explanation", string profile = "selected")
    {
        // Before the verb exists, exercise the executable's unknown-command behavior.
        var entry = typeof(HostSettings).Assembly.GetType("AgentBridge.Host.ReplyCommands")?.GetMethod("RunAsync");
        if (entry is null)
        {
            var previous = Console.Error;
            Console.SetError(error);
            try
            {
                var result = typeof(HostSettings).Assembly.EntryPoint!.Invoke(null,
                    [new[] { "reply", Id, Request, text, "--profile", Path.GetFileName(root) }]);
                return result is Task<int> pending ? await pending : Assert.IsType<int>(result);
            }
            finally { Console.SetError(previous); }
        }
        return await Assert.IsAssignableFrom<Task<int>>(entry.Invoke(null,
            [new[] { "reply", Id, Request, text, "--profile", profile }, root, refused ?? (() => false), output, error]));
    }

    [Theory]
    [InlineData("delivered", 0)]
    [InlineData("refused", 4)]
    [InlineData("unconfirmed", 3)]
    [InlineData("busy", 4)]
    [InlineData("stale", 4)]
    [InlineData("future", 3)]
    public async Task Cli_reply_prints_delivered_refused_or_unconfirmed_from_the_service_receipt(string result, int exit)
    {
        receipt = result;
        Assert.Equal(exit, await Run());
        Assert.Equal([Ops.Hello, Ops.SessionGet, Ops.SessionReply], operations);
        Assert.NotNull(sent);
        Assert.Equal((Id, Request, "Continue with the explanation"), (sent.Id, sent.Request, sent.Text));
        Assert.Null(sent.Attachments);
        Assert.Null(sent.Mode);
        Assert.Equal(exit == 0 ? "delivered" : "", output.ToString().Trim());
        Assert.Equal(exit == 0 ? "" : result == "future" ? "unconfirmed" : result, error.ToString().Trim());
        Assert.DoesNotContain("private-sentinel", output.ToString() + error);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Cli_reply_from_an_agent_child_sends_nothing(bool partial, bool discoveredLater)
    {
        var ancestry = new ProcessAncestry([new PeerProcess(1, null, partial ? "fixture" : "copilot")], !partial);
        var checks = 0;
        Assert.Equal(4, await Run(() => ++checks > (discoveredLater ? 1 : 0)
            && AgentAncestry.Inside(null, ancestry, () => []) is not null));
        if (!discoveredLater) { Assert.Empty(operations); }
        Assert.Null(sent);
        Assert.DoesNotContain(Ops.SessionReply, operations);
        Assert.Contains("refused", error.ToString());
    }

    [Fact]
    public async Task Cli_reply_with_an_open_question_sends_nothing()
    {
        question = new() { Id = "question", Kind = QuestionKind.Form, Message = "Answer first", Fields = [], AskedAt = DateTimeOffset.UnixEpoch };
        Assert.Equal(4, await Run());
        Assert.Equal([Ops.Hello, Ops.SessionGet], operations);
        Assert.Null(sent);
        Assert.Contains("refused", error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cli_reply_never_retries_after_ambiguous_typing(bool lost)
    {
        receipt = "unconfirmed";
        disconnect = lost;
        Assert.Equal(3, await Run());
        Assert.Equal([Ops.Hello, Ops.SessionGet, Ops.SessionReply], operations);
        Assert.NotNull(sent);
        Assert.Equal("unconfirmed", error.ToString().Trim());
        Assert.Equal("", output.ToString());
    }

    [Theory]
    [InlineData("/command", "selected", true, 2)]
    [InlineData("!shell", "selected", true, 2)]
    [InlineData(" ", "selected", true, 2)]
    [InlineData("hello", "..", true, 2)]
    [InlineData("hello", "selected", false, 4)]
    public async Task Cli_reply_rejects_invalid_input_and_missing_capability(string text, string profile, bool available, int exit)
    {
        capability = available;
        Assert.Equal(exit, await Run(text: text, profile: profile));
        Assert.Null(sent);
        if (available) { Assert.Empty(operations); }
        Assert.NotEqual("", error.ToString());
    }

    public async Task DisposeAsync()
    {
        await server.DisposeAsync();
        Directory.Delete(root, true);
    }
}
