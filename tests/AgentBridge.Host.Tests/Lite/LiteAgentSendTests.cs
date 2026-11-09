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
    readonly Sender sender = new();
    readonly Chan hello = new();

    public LiteAgentSendTests()
    {
        Directory.CreateDirectory(root);
        store = new AgentCredentials(root, "machine1");
        var token = store.Mint("sess-1", "launch-1", 100, Started, AgentProtocol.ScopeRead);
        store.Accept(hello, store.Check(token, new ProcessAncestry([new PeerProcess(100, Started, "agent.exe")], true)).Accepted!);
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    Task<object?> Send(IProtocolChannel channel, string json) =>
        LiteAgentSend.HandleAsync(store, sender, channel, JsonDocument.Parse(json).RootElement);

    const string Body = """{"machine":"m2","session":"s2","text":"hi"}""";

    [Fact]
    public async Task Send_before_hello_is_refused_and_nothing_is_sent()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Send(new Chan(), Body));
        Assert.Empty(sender.Calls);
    }

    [Fact]
    public async Task A_principal_in_the_body_is_ignored_and_the_credential_is_the_sender()
    {
        await Send(hello, """{"machine":"m2","session":"s2","text":"hi","principal":"agent:other:sess-9","caller":{"session":"sess-9"}}""");
        var call = Assert.Single(sender.Calls);
        Assert.Equal("sess-1", call.Caller.Session);
        Assert.Equal(new LiteSendInput("m2", "s2", "hi"), call.Input);
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
        await Assert.ThrowsAsync<ArgumentException>(() => Send(hello, JsonSerializer.Serialize(new { machine = "m2", session = "s2", text = new string('x', 4097) })));
        Assert.Empty(sender.Calls);
        await Send(hello, JsonSerializer.Serialize(new { machine = "m2", session = "s2", text = new string('x', 4096) }));
        Assert.Single(sender.Calls);
    }

    [Fact]
    public async Task A_request_missing_a_field_is_refused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Send(hello, """{"machine":"m2","text":"hi"}"""));
        Assert.Empty(sender.Calls);
    }

    sealed class Sender : ILiteSender
    {
        public List<(AgentCredential Caller, LiteSendInput Input)> Calls { get; } = [];
        public LiteOutcome Next { get; set; } = new(LiteKind.Delivered, null, "r");
        public Task<LiteOutcome> SendAsync(AgentCredential caller, LiteSendInput input, CancellationToken ct) { Calls.Add((caller, input)); return Task.FromResult(Next); }
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
