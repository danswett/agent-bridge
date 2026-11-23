using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Lite;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

// #3875 slice 2: the agent.receipt pipe op. Same caller rules as agent.send; it asks about one earlier unconfirmed send and nothing else.
public sealed class LiteAgentReceiptTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ablite-" + Guid.NewGuid().ToString("N")[..8]);
    static readonly DateTimeOffset Started = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    const string Request = "3f2b8c1e-5a4d-4e6f-9b7a-1c2d3e4f5a6b";
    readonly AgentCredentials store;
    readonly AgentAuditLog audit;
    readonly Receipts receipts = new();
    readonly Chan hello = new();

    public LiteAgentReceiptTests()
    {
        Directory.CreateDirectory(root);
        store = new AgentCredentials(root, "machine1");
        audit = new AgentAuditLog(root);
        var token = store.Mint("sess-1", "launch-1", 100, Started, AgentProtocol.ScopeRead, chain: ["agent:upstream/parent"]);
        store.Accept(hello, store.Check(token, new ProcessAncestry([new PeerProcess(100, Started, "agent.exe")], true)).Accepted!);
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    Task<object?> Ask(IProtocolChannel channel, string json, bool windows = true, ILiteReceipts? with = null, Func<AgentAuditEntry, string?, bool>? append = null) =>
        LiteAgentReceipt.HandleAsync(store, with ?? receipts, channel, JsonDocument.Parse(json).RootElement, windows: windows, appendAttempt: append ?? audit.Append);

    static string Body(string request = Request) => JsonSerializer.Serialize(new { request });

    [Fact]
    public async Task A_connection_that_has_not_passed_hello_or_a_host_that_is_not_windows_is_refused_and_asks_nothing()
    {
        var e1 = await Assert.ThrowsAsync<InvalidOperationException>(() => Ask(new Chan(), Body()));
        Assert.Contains("agent.hello", e1.Message);
        var e2 = await Assert.ThrowsAsync<InvalidOperationException>(() => Ask(hello, Body(), windows: false));
        Assert.Contains("isn't available", e2.Message);
        Assert.Empty(receipts.Calls);
    }

    [Theory, InlineData("{}"), InlineData("""{"request":5}"""), InlineData("""{"request":"not-a-guid"}"""), InlineData("""{"request":"3F2B8C1E-5A4D-4E6F-9B7A-1C2D3E4F5A6B"}""")]
    public async Task A_request_that_is_not_the_lower_case_id_agent_send_returned_is_refused_before_audit_or_lookup(string json)
    {
        var e = await Assert.ThrowsAsync<ArgumentException>(() => Ask(hello, json));
        Assert.Contains("badRequest", e.Message);
        Assert.Empty(receipts.Calls);
        Assert.False(Directory.Exists(Path.Combine(root, AgentAuditLog.FolderName)));
    }

    [Fact]
    public async Task The_answer_is_the_senders_reply_unchanged_and_the_caller_is_the_connections_credential()
    {
        receipts.Next = new LiteReceiptReply("refused", "busy", Request);
        var reply = Assert.IsType<LiteReceiptReply>(await Ask(hello, Body()));
        Assert.Equal(new LiteReceiptReply("refused", "busy", Request), reply);
        var call = Assert.Single(receipts.Calls);
        Assert.Equal(Request, call.Request);
        Assert.Equal("sess-1", call.Caller.Session);
    }

    [Fact]
    public async Task A_request_this_credential_has_no_unconfirmed_send_for_is_refused_as_unknown_after_an_audit_entry()
    {
        receipts.Next = null;
        var e = await Assert.ThrowsAsync<ArgumentException>(() => Ask(hello, Body()));
        Assert.Contains("unknownRequest", e.Message);
        Assert.True(Directory.Exists(Path.Combine(root, AgentAuditLog.FolderName)));
    }

    [Fact]
    public async Task An_unwritable_audit_log_refuses_before_anything_is_asked()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Ask(hello, Body(), append: (_, _) => false));
        Assert.Contains("auditUnwritable", e.Message);
        Assert.Empty(receipts.Calls);
    }

    sealed class Receipts : ILiteReceipts
    {
        public List<(AgentCredential Caller, string Request)> Calls { get; } = [];
        public LiteReceiptReply? Next { get; set; } = new("delivered", null, Request);
        public Task<LiteReceiptReply?> ReceiptAsync(AgentCredential caller, string request, CancellationToken ct) { Calls.Add((caller, request)); return Task.FromResult(Next); }
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
