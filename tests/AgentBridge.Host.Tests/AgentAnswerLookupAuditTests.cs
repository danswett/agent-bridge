using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Fleet;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class AgentAnswerLookupAuditTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ab-a5-" + Guid.NewGuid().ToString("N")[..8]);
    readonly AgentCredentials credentials;
    readonly Chan channel = new();
    readonly Roster roster = new();
    const string Target = "88888888-8888-4888-8888-888888888888";
    static readonly JsonElement Body = JsonSerializer.SerializeToElement(new
    {
        id = Target, name = "private lookup name", text = "private answer", principal = "owner", chain = new[] { "owner" },
    });

    public AgentAnswerLookupAuditTests()
    {
        Directory.CreateDirectory(root);
        credentials = new AgentCredentials(root, "machine1");
        var started = DateTimeOffset.UtcNow;
        var token = credentials.Mint("sess-1", "launch-1", 100, started, AgentProtocol.ScopeRead, chain: ["agent:upstream/parent"]);
        credentials.Accept(channel, credentials.Check(token, new ProcessAncestry([new PeerProcess(100, started, "agent.exe")], true)).Accepted!);
    }

    public void Dispose() => Directory.Delete(root, true);

    Func<IProtocolChannel, string, JsonElement, Task<object?>> Handler(AgentAuditLog? audit, CallerKind kind = CallerKind.Agent) =>
        LocalApi.Handler(null!, null!, new FixedCallerGuard(new Caller(kind, "fixture caller")),
            credentials: credentials, fleet: new FleetList(credentials, roster), audit: audit);

    [Theory]
    [InlineData(Ops.SessionAnswer)]
    [InlineData(FleetList.FindOp)]
    [InlineData(FleetList.Op)]
    public async Task Each_authenticated_answer_refusal_or_lookup_writes_one_private_credential_attributed_record(string op)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        JsonElement[] Lines() => Directory.GetFiles(Path.Combine(root, AgentAuditLog.FolderName), "agents-*.jsonl")
            .SelectMany(f => File.ReadAllLines(f).Skip(1)).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        roster.BeforeRead = () => Assert.Single(Lines());
        var handle = Handler(new AgentAuditLog(root), CallerKind.Other);
        if (op == Ops.SessionAnswer)
        {
            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => handle(channel, op, Body));
            Assert.Contains("for the person", refusal.Message);
        }
        else { Assert.NotNull(await handle(channel, op, Body)); }
        var entry = Assert.Single(Lines());
        Assert.Equal("agent:machine1/sess-1", entry.GetProperty("principal").GetString());
        Assert.Equal("agent:upstream/parent", Assert.Single(entry.GetProperty("chain").EnumerateArray()).GetString());
        Assert.Equal(op, entry.GetProperty("action").GetString());
        Assert.Equal(op == Ops.SessionAnswer ? Target : "fleet", entry.GetProperty("target").GetString());
        Assert.Equal(op == Ops.SessionAnswer ? "refused" : "attempted", entry.GetProperty("outcome").GetString());
        Assert.Equal("local", entry.GetProperty("direction").GetString());
        Assert.True(Guid.TryParse(entry.GetProperty("request").GetString(), out _));
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("textLength").ValueKind);
        Assert.DoesNotContain("private", entry.GetRawText());
        Assert.DoesNotContain("owner", entry.GetRawText());
        Assert.Equal(op == Ops.SessionAnswer ? 0 : 1, roster.Reads);
    }

    [Theory]
    [InlineData(Ops.SessionAnswer, 0)]
    [InlineData(Ops.SessionAnswer, 1)]
    [InlineData(Ops.SessionAnswer, 2)]
    [InlineData(FleetList.FindOp, 0)]
    [InlineData(FleetList.FindOp, 1)]
    [InlineData(FleetList.FindOp, 2)]
    [InlineData(FleetList.Op, 0)]
    [InlineData(FleetList.Op, 1)]
    [InlineData(FleetList.Op, 2)]
    public async Task A_missing_false_or_throwing_append_refuses_before_any_answer_dispatch_or_roster_read(string op, int storeKind)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var handle = Handler(storeKind == 0 ? null : new AgentAuditLog(root, time: storeKind == 2 ? new UnavailableClock() : null, maxBytes: 0));
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => handle(channel, op, Body));
        Assert.Contains("refused: auditUnwritable:", refusal.Message);
        Assert.Equal(0, roster.Reads);
    }

    [Theory]
    [InlineData(Ops.SessionAnswer)]
    [InlineData(FleetList.FindOp)]
    [InlineData(FleetList.Op)]
    public async Task An_unauthenticated_refusal_writes_no_record_and_reads_no_roster(string op)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(new AgentAuditLog(root))(new Chan(), op, Body));
        Assert.False(Directory.Exists(Path.Combine(root, AgentAuditLog.FolderName)));
        Assert.Equal(0, roster.Reads);
    }

    [Theory]
    [InlineData("""{"id":"private answer disguised as an id"}""")]
    [InlineData("""{"id":"\uD800"}""")]
    public async Task An_answer_refusal_does_not_log_arbitrary_words_supplied_as_a_session_id(string json)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var body = JsonDocument.Parse(json).RootElement;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Handler(new AgentAuditLog(root))(channel, Ops.SessionAnswer, body));
        var entry = JsonSerializer.Deserialize<JsonElement>(Assert.Single(File.ReadAllLines(
            Assert.Single(Directory.GetFiles(Path.Combine(root, AgentAuditLog.FolderName), "agents-*.jsonl"))).Skip(1)));
        Assert.Equal("unknown", entry.GetProperty("target").GetString());
        Assert.DoesNotContain("private", entry.GetRawText());
    }

    sealed class UnavailableClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => throw new IOException("private store failure");
    }

    sealed class Roster : IFleetRoster
    {
        public int Reads;
        public Action? BeforeRead;
        public Task<IReadOnlyList<FleetMachine>> ReadAsync(CancellationToken ct)
        {
            BeforeRead?.Invoke();
            Reads++;
            return Task.FromResult<IReadOnlyList<FleetMachine>>([]);
        }
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
