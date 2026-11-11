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

    Func<IProtocolChannel, string, JsonElement, Task<object?>> Handler(AgentAuditLog? audit) =>
        LocalApi.Handler(null!, null!, new FixedCallerGuard(new Caller(CallerKind.Agent, "fixture agent")),
            credentials: credentials, fleet: new FleetList(credentials, roster), audit: audit);

    [Theory]
    [InlineData(Ops.SessionAnswer)]
    [InlineData(FleetList.FindOp)]
    [InlineData(FleetList.Op)]
    public async Task Each_authenticated_answer_refusal_or_lookup_writes_one_private_credential_attributed_record(string op)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        JsonElement[] Lines() => Directory.GetFiles(Path.Combine(root, AgentAuditLog.FolderName), "agents-*.jsonl")
            .SelectMany(File.ReadAllLines).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
        roster.BeforeRead = () => Assert.Single(Lines());
        var handle = Handler(new AgentAuditLog(root));
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
    [InlineData(Ops.SessionAnswer, false)]
    [InlineData(Ops.SessionAnswer, true)]
    [InlineData(FleetList.FindOp, false)]
    [InlineData(FleetList.FindOp, true)]
    [InlineData(FleetList.Op, false)]
    [InlineData(FleetList.Op, true)]
    public async Task A_missing_or_failing_audit_store_refuses_before_any_answer_dispatch_or_roster_read(string op, bool wired)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var handle = Handler(wired ? new AgentAuditLog(root, maxBytes: 0) : null);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => handle(channel, op, Body));
        Assert.Contains("refused: auditUnwritable:", refusal.Message);
        Assert.Equal(0, roster.Reads);
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
