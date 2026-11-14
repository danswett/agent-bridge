using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Lite;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class LiteAgentSendTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ablite-" + Guid.NewGuid().ToString("N")[..8]);
    static readonly DateTimeOffset Started = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    readonly AgentCredentials store;
    readonly AgentAuditLog audit;
    readonly Sender sender = new();
    readonly Chan hello = new();

    public LiteAgentSendTests()
    {
        Directory.CreateDirectory(root);
        store = new AgentCredentials(root, "machine1");
        audit = new AgentAuditLog(root);
        var token = store.Mint("sess-1", "launch-1", 100, Started, AgentProtocol.ScopeRead, chain: ["agent:upstream/parent"]);
        store.Accept(hello, store.Check(token, new ProcessAncestry([new PeerProcess(100, Started, "agent.exe")], true)).Accepted!);
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    Task<object?> Send(IProtocolChannel channel, string json) =>
        LiteAgentSend.HandleAsync(store, sender, channel, JsonDocument.Parse(json).RootElement, windows: true, appendAttempt: audit.Append);

    const string Sess = "agent_bridge_0123456789abcdef";
    const string Body = """{"machine":"m2","session":"agent_bridge_0123456789abcdef","text":"hi"}""";

    JsonElement[] AuditLines() => Directory.GetFiles(Path.Combine(root, AgentAuditLog.FolderName), "agents-*.jsonl")
        .SelectMany(File.ReadAllLines).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).Where(line => line.TryGetProperty("action", out _)).ToArray();

    [Fact]
    public async Task A_send_writes_one_credential_attributed_attempt_before_delivery_without_the_message_text()
    {
        const string text = "private message that must not be logged \u00e9";
        sender.BeforeSend = () =>
        {
            Assert.True(Directory.Exists(Path.Combine(root, AgentAuditLog.FolderName)));
            Assert.Single(AuditLines());
        };
        var reply = Assert.IsType<LiteSendReply>(await Send(hello, JsonSerializer.Serialize(new
        {
            machine = "m2", session = Sess, text, principal = "owner", chain = new[] { "owner" },
        })));

        Assert.Equal("delivered", reply.Outcome);
        Assert.Single(sender.Calls);
        var entry = Assert.Single(AuditLines());
        Assert.Equal("agent:machine1/sess-1", entry.GetProperty("principal").GetString());
        Assert.Equal("agent:upstream/parent", Assert.Single(entry.GetProperty("chain").EnumerateArray()).GetString());
        Assert.Equal("out", entry.GetProperty("direction").GetString());
        Assert.Equal("agent.send", entry.GetProperty("action").GetString());
        Assert.Equal($"m2/{Sess}", entry.GetProperty("target").GetString());
        Assert.Equal("attempted", entry.GetProperty("outcome").GetString());
        Assert.True(Guid.TryParse(entry.GetProperty("request").GetString(), out _));
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(text), entry.GetProperty("textLength").GetInt32());
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant(),
            entry.GetProperty("dataSha256").GetString());
        Assert.DoesNotContain(text, entry.GetRawText());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task A_missing_false_or_throwing_append_refuses_the_send_and_delivers_nothing(bool wired, bool throws)
    {
        var calls = 0;
        bool Append(AgentAuditEntry attempt, string? text)
        {
            calls++;
            if (throws) { throw new IOException("private failure detail"); }
            return false;
        }
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LiteAgentSend.HandleAsync(store, sender, hello, Json(), windows: true, appendAttempt: wired ? Append : null));

        Assert.Equal("refused: auditUnwritable: the agent audit log could not be written; nothing was sent", refusal.Message);
        Assert.Equal(wired ? 1 : 0, calls);
        Assert.Empty(sender.Calls);
    }

    [Theory]
    [InlineData(LiteKind.Refused)]
    [InlineData(LiteKind.Undeliverable)]
    [InlineData(LiteKind.Unconfirmed)]
    [InlineData(LiteKind.Off)]
    public async Task A_send_the_sender_fails_still_has_exactly_one_attempt_record(LiteKind kind)
    {
        sender.Next = new LiteOutcome(kind, "sender refusal", "relay-request");
        var reply = Assert.IsType<LiteSendReply>(await Send(hello, Body));
        Assert.Equal(kind.ToString().ToLowerInvariant(), reply.Outcome);
        Assert.Equal("attempted", Assert.Single(AuditLines()).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task A_sender_that_throws_leaves_one_attempt_record_and_its_error_is_not_hidden()
    {
        sender.BeforeSend = () => throw new IOException("sender failed");
        var error = await Assert.ThrowsAsync<IOException>(() => Send(hello, Body));
        Assert.Equal("sender failed", error.Message);
        Assert.Equal("attempted", Assert.Single(AuditLines()).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Send_before_hello_is_refused_and_nothing_is_sent()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Send(new Chan(), Body));
        Assert.Empty(sender.Calls);
        Assert.False(Directory.Exists(Path.Combine(root, AgentAuditLog.FolderName)));
    }

    [Fact]
    public async Task A_principal_in_the_body_is_ignored_and_the_credential_is_the_sender()
    {
        await Send(hello, """{"machine":"m2","session":"agent_bridge_0123456789abcdef","text":"hi","principal":"agent:other:sess-9","caller":{"session":"sess-9"}}""");
        var call = Assert.Single(sender.Calls);
        Assert.Equal("sess-1", call.Caller.Session);
        Assert.Equal(new LiteSendInput("m2", Sess, "hi"), call.Input);
    }

    [Theory]
    [InlineData(LiteKind.Delivered, null, "delivered")]
    [InlineData(LiteKind.Refused, "busy", "refused")]
    [InlineData(LiteKind.Undeliverable, null, "undeliverable")]
    [InlineData(LiteKind.Unconfirmed, null, "unconfirmed")]
    [InlineData(LiteKind.Off, null, "off")]
    public async Task Each_outcome_comes_back_with_its_code_and_request(LiteKind kind, string? code, string name)
    {
        sender.Next = new LiteOutcome(kind, code, "req1");
        var reply = Assert.IsType<LiteSendReply>(await Send(hello, Body));
        Assert.Equal(new LiteSendReply(name, code, "req1"), reply);
    }

    [Fact]
    public async Task Text_over_4096_characters_is_refused_before_the_sender_is_called()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Send(hello, JsonSerializer.Serialize(new { machine = "m2", session = Sess, text = new string('x', 4097) })));
        Assert.Empty(sender.Calls);
        await Send(hello, JsonSerializer.Serialize(new { machine = "m2", session = Sess, text = new string('x', 4096) }));
        Assert.Single(sender.Calls);
    }

    [Theory]
    [InlineData("""{"session":"agent_bridge_0123456789abcdef","text":"hi"}""")]
    [InlineData("""{"machine":"","session":"agent_bridge_0123456789abcdef","text":"hi"}""")]
    [InlineData("""{"machine":5,"session":"agent_bridge_0123456789abcdef","text":"hi"}""")]
    [InlineData("""{"machine":"bad name","session":"agent_bridge_0123456789abcdef","text":"hi"}""")]
    [InlineData("""{"machine":"m2","text":"hi"}""")]
    [InlineData("""{"machine":"m2","session":"","text":"hi"}""")]
    [InlineData("""{"machine":"m2","session":7,"text":"hi"}""")]
    [InlineData("""{"machine":"m2","session":"s2","text":"hi"}""")]
    [InlineData("""{"machine":"m2","session":"agent_bridge_0123456789ABCDEF","text":"hi"}""")]
    [InlineData("""{"machine":"m2","session":"agent_bridge_0123456789abcdef\n","text":"hi"}""")]
    [InlineData("""{"machine":"m2","session":"agent_bridge_0123456789abcde","text":"hi"}""")]
    [InlineData("""{"machine":"m2","session":"agent_bridge_0123456789abcdef"}""")]
    [InlineData("""{"machine":"m2","session":"agent_bridge_0123456789abcdef","text":""}""")]
    [InlineData("""{"machine":"m2","session":"agent_bridge_0123456789abcdef","text":null}""")]
    [InlineData("""{"machine":"m2","session":"agent_bridge_0123456789abcdef","text":4}""")]
    [InlineData("""{"machine":"m2","session":"agent_bridge_0123456789abcdef","text":"  \n "}""")]
    [InlineData("""{"machine":"m2","session":"agent_bridge_0123456789abcdef","text":"a\u001b[31m"}""")]
    [InlineData("""{"machine":"m2","session":"agent_bridge_0123456789abcdef","text":"a\u0007b"}""")]
    [InlineData("""{"machine":"m2","session":"agent_bridge_0123456789abcdef","text":"a\u0000b"}""")]
    [InlineData("[1]")]
    public async Task A_bad_request_is_refused_before_the_sender_runs(string json)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Send(hello, json));
        Assert.Empty(sender.Calls);
        Assert.False(Directory.Exists(Path.Combine(root, AgentAuditLog.FolderName)));
    }

    [Fact]
    public async Task Tab_CR_and_LF_are_allowed_in_text()
    {
        await Send(hello, """{"machine":"m2","session":"agent_bridge_0123456789abcdef","text":"a\tb\r\nc"}""");
        Assert.Single(sender.Calls);
    }

    [Fact]
    public async Task An_unwired_sender_answers_off()
    {
        var reply = Assert.IsType<LiteSendReply>(await LiteAgentSend.HandleAsync(store, null, hello, JsonDocument.Parse(Body).RootElement, windows: true, appendAttempt: audit.Append));
        Assert.Equal("off", reply.Outcome);
    }

    static JsonElement Json() => JsonDocument.Parse(Body).RootElement;

    [Fact]
    public async Task Through_the_local_api_a_non_agent_channel_and_a_host_without_credentials_are_refused()
    {
        var withCreds = LocalApi.Handler(null!, null!, null!, null, null, store, sender);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => withCreds(new Chan(), LiteAgentSend.Op, Json()));
        Assert.StartsWith("refused:", refused.Message);
        var bare = LocalApi.Handler(null!, null!, null!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => bare(hello, LiteAgentSend.Op, Json()));
        Assert.Empty(sender.Calls);
    }

    [Fact]
    public async Task Through_the_local_api_an_agent_connection_reaches_the_sender_or_the_off_stub()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var wired = LocalApi.Handler(null!, null!, null!, null, null, store, sender, audit: audit);
        Assert.Equal("delivered", Assert.IsType<LiteSendReply>(await wired(hello, LiteAgentSend.Op, Json())).Outcome);
        var unwired = LocalApi.Handler(null!, null!, null!, null, null, store, null, audit: audit);
        Assert.Equal("off", Assert.IsType<LiteSendReply>(await unwired(hello, LiteAgentSend.Op, Json())).Outcome);
        Assert.Equal(2, AuditLines().Length);
    }

    [Fact]
    public async Task Through_the_local_api_a_failed_append_refuses_before_the_sender_runs()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var handler = LocalApi.Handler(null!, null!, null!, credentials: store, lite: sender, audit: new AgentAuditLog(root, maxBytes: 0));
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => handler(hello, LiteAgentSend.Op, Json()));
        Assert.Equal("refused: auditUnwritable: the agent audit log could not be written; nothing was sent", refusal.Message);
        Assert.Empty(sender.Calls);
        Assert.Empty(AuditLines());
    }
    sealed class Sender : ILiteSender
    {
        public List<(AgentCredential Caller, LiteSendInput Input)> Calls { get; } = [];
        public LiteOutcome Next { get; set; } = new(LiteKind.Delivered, null, "r");
        public Action? BeforeSend { get; set; }
        public Task<LiteOutcome> SendAsync(AgentCredential caller, LiteSendInput input, CancellationToken ct) { BeforeSend?.Invoke(); Calls.Add((caller, input)); return Task.FromResult(Next); }
    }

    sealed class Chan : IProtocolChannel
    {
        public string PeerId => "fixture";
        public string PeerName => "fixture";
        public string PeerKind => "fixture";
        public bool IsOpen => true;
        public Task Closed => Task.Delay(Timeout.Infinite);
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
