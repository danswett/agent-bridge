using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Fleet;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class FleetListTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "abfleet-" + Guid.NewGuid().ToString("N")[..8]);
    static readonly DateTimeOffset Started = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    readonly AgentCredentials store;
    readonly Roster roster = new();
    readonly List<string> audited = [];

    public FleetListTests()
    {
        Directory.CreateDirectory(root);
        store = new AgentCredentials(root, "machine1");
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    Chan Hello(string scope)
    {
        var chan = new Chan();
        var token = store.Mint("sess-1", "launch-1", 100, Started, scope);
        store.Accept(chan, store.Check(token, new ProcessAncestry([new PeerProcess(100, Started, "agent.exe")], true)).Accepted!);
        return chan;
    }

    FleetList Op() => new(store, roster, new FixedTime(Started.AddSeconds(90))) { AuditFleetList = audited.Add };

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public async Task Listing_before_agent_hello_is_refused_and_not_audited()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Op().HandleAsync(new Chan(), Body("{}")));
        Assert.Empty(audited);
    }

    [Fact]
    public async Task A_credential_without_the_read_scope_is_refused_with_a_readable_reason()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => Op().HandleAsync(Hello(AgentProtocol.ScopeNone), Body("{}")));
        Assert.Contains("needs at least 'read'", e.Message);
        Assert.Empty(audited);
    }

    [Fact]
    public async Task The_reply_carries_only_the_allow_listed_fields_and_no_name_unless_asked()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var chan = Hello(AgentProtocol.ScopeRead);
        var op = Op();
        var plain = JsonSerializer.Serialize(await op.HandleAsync(chan, Body("""{"prompt":"secret prompt","account":"other"}""")), Web);
        Assert.Equal(["machine", "machineOnline", "node", "secondsInState", "since", "state"], Names(plain));
        Assert.DoesNotContain("my project", plain);
        Assert.DoesNotContain("secret", plain);
        Assert.Contains("\"secondsInState\":90", plain);
        var named = JsonSerializer.Serialize(await op.HandleAsync(chan, Body("""{"include":"content"}""")), Web);
        Assert.Equal(["machine", "machineOnline", "name", "node", "secondsInState", "since", "state"], Names(named));
        Assert.Contains("my project", named);
    }

    [Fact]
    public async Task Each_accepted_call_is_audited_with_the_agents_principal()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await Op().HandleAsync(Hello(AgentProtocol.ScopeRead), Body("{}"));
        Assert.Equal([AgentProtocol.Principal("machine1", "sess-1")], audited);
    }

    [Fact]
    public async Task A_read_reply_credential_is_accepted_and_the_local_api_routes_fleet_list()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var handler = LocalApi.Handler(null!, null!, null!, null, null, store, null, Op());
        var json = JsonSerializer.Serialize(await handler(Hello(AgentProtocol.ScopeReadReply), FleetList.Op, Body("{}")), Web);
        Assert.Contains("\"node\":\"n1\"", json);
        var bare = LocalApi.Handler(null!, null!, null!, null, null, store, null);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => bare(Hello(AgentProtocol.ScopeRead), FleetList.Op, Body("{}")));
        Assert.StartsWith("refused:", e.Message);
    }

    [Fact]
    public void Parsing_skips_a_frame_without_a_machine_and_lists_no_sessions_for_an_offline_machine()
    {
        var frames = new[]
        {
            """{"t":"registry","id":2,"name":"x","online":true,"record":null}""",
            """{"t":"registry","id":2,"machine":"off","online":false,"record":null}""",
            """{"t":"registry","id":2,"machine":"on","online":true,"record":{"sessions":[{"id":"a","node":"n","name":"p","state":"idle","since":"2026-11-07T12:00:00Z"}]}}""",
        }.Select(Body).ToList();
        var parsed = RelayFleetRoster.Parse(frames);
        Assert.Equal(["off", "on"], parsed.Select(m => m.Id));
        Assert.Empty(parsed[0].Sessions);
        Assert.Equal("idle", Assert.Single(parsed[1].Sessions).State);
    }

    [Fact]
    public async Task A_roster_with_no_relay_refuses_at_once()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => new RelayFleetRoster().ReadAsync(default));
        Assert.Contains("isn't connected to the relay", e.Message);
    }
    static string[] Names(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("sessions")[0].EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

    sealed class Roster : IFleetRoster
    {
        public Task<IReadOnlyList<FleetMachine>> ReadAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<FleetMachine>>([new FleetMachine("m2", true, [new FleetSession("n1", "my project", "working", Started)])]);
    }

    sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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
