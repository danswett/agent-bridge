using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Fleet;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class FleetFindTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "abfind-" + Guid.NewGuid().ToString("N")[..8]);
    static readonly DateTimeOffset Started = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    readonly AgentCredentials store;
    readonly List<string> audited = [];

    public FleetFindTests()
    {
        Directory.CreateDirectory(root);
        store = new AgentCredentials(root, "machine1");
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    sealed class Roster(params FleetSession[] sessions) : IFleetRoster
    {
        public Task<IReadOnlyList<FleetMachine>> ReadAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FleetMachine>>([new FleetMachine("m2", true, sessions), new FleetMachine("m3", true, [new FleetSession("n9", "SG Quality", "idle", Started)])]);
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

    Chan Hello(string scope)
    {
        var chan = new Chan();
        var token = store.Mint("sess-1", "launch-1", 100, Started, scope);
        store.Accept(chan, store.Check(token, new ProcessAncestry([new PeerProcess(100, Started, "agent.exe")], true)).Accepted!);
        return chan;
    }

    FleetList Op(params FleetSession[] sessions) => new(store, new Roster(sessions)) { AuditFleetList = audited.Add };

    static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    async Task<JsonElement> Find(FleetList op, string name, string scope = AgentProtocol.ScopeRead) =>
        JsonSerializer.SerializeToElement(await op.FindAsync(Hello(scope), Body(JsonSerializer.Serialize(new { name }))), Web);

    [Fact]
    public async Task An_exact_name_finds_one_session_with_typed_fields_only_and_is_audited()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var r = await Find(Op(new FleetSession("n1", "SG Engineering", "working", Started)), "SG Engineering");
        Assert.Equal("found", r.GetProperty("result").GetString());
        var match = Assert.Single(r.GetProperty("matches").EnumerateArray());
        Assert.Equal(["machine", "machineOnline", "node", "secondsInState", "since", "state"], match.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("m2", match.GetProperty("machine").GetString());
        Assert.Equal([AgentProtocol.Principal("machine1", "sess-1")], audited);
    }

    [Fact]
    public async Task An_unknown_or_inexact_name_is_not_found()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var op = Op(new FleetSession("n1", "SG Engineering", "working", Started));
        Assert.Equal("not-found", (await Find(op, "SG Product")).GetProperty("result").GetString());
        Assert.Equal("not-found", (await Find(op, "sg engineering")).GetProperty("result").GetString());
    }

    [Fact]
    public async Task Two_sessions_with_one_name_are_an_ambiguity()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var r = await Find(Op(new FleetSession("n1", "SG Quality", "working", Started)), "SG Quality");
        Assert.Equal("ambiguous", r.GetProperty("result").GetString());
        Assert.Equal(2, r.GetProperty("matches").GetArrayLength());
    }

    [Fact]
    public async Task Find_is_refused_without_hello_or_the_read_scope_or_a_name_and_routes_through_the_local_api()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var op = Op();
        await Assert.ThrowsAsync<InvalidOperationException>(() => op.FindAsync(new Chan(), Body("""{"name":"x"}""")));
        var low = await Assert.ThrowsAsync<InvalidOperationException>(() => op.FindAsync(Hello(AgentProtocol.ScopeNone), Body("""{"name":"x"}""")));
        Assert.Contains("fleet.find needs at least 'read'", low.Message);
        var bad = await Assert.ThrowsAsync<InvalidOperationException>(() => op.FindAsync(Hello(AgentProtocol.ScopeRead), Body("{}")));
        Assert.Contains("needs a non-empty", bad.Message);
        Assert.Empty(audited);
        var handler = LocalApi.Handler(null!, null!, null!, null, null, store, null, op);
        var json = JsonSerializer.Serialize(await handler(Hello(AgentProtocol.ScopeRead), FleetList.FindOp, Body("""{"name":"SG Quality"}""")), Web);
        Assert.Contains("\"found\"", json);
    }
}