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

    sealed class Roster(params FleetMachine[] machines) : IFleetRoster
    {
        public Task<IReadOnlyList<FleetMachine>> ReadAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<FleetMachine>>(machines);
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

    FleetList Op(params FleetMachine[] machines) => new(store, new Roster(machines)) { AuditFleetList = audited.Add };

    static FleetMachine M(string id, bool online, string? name, bool truncated = false, bool verified = false) =>
        new(id, online, name is null ? [] : [new FleetSession("n-" + id, name, "working", Started, "s-" + id)], truncated, verified);

    static JsonElement Body(object o) => JsonSerializer.SerializeToElement(o);

    async Task<JsonElement> Find(FleetList op, object data) =>
        JsonSerializer.SerializeToElement(await op.FindAsync(Hello(AgentProtocol.ScopeRead), Body(data)), Web);

    [Fact]
    public async Task A_name_alone_never_yields_found_or_a_node_and_an_impostor_beside_an_offline_original_is_unverified()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var op = Op(M("orig", false, "SG Quality"), M("impostor", true, "SG Quality"));
        var r = await Find(op, new { name = "SG Quality" });
        Assert.Equal("unverified", r.GetProperty("result").GetString());
        Assert.DoesNotContain("node", r.GetRawText());
        Assert.Equal("unverified", (await Find(op, new { name = "SG Quality", machine = "impostor" })).GetProperty("result").GetString());
        Assert.Equal("offline", (await Find(op, new { name = "SG Quality", machine = "orig" })).GetProperty("result").GetString());
        Assert.Equal([AgentProtocol.Principal("machine1", "sess-1")], audited.Distinct());
    }

    [Fact]
    public async Task Verified_matches_are_pinned_ambiguous_or_found_only_with_the_machine_and_the_name_is_never_echoed()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var one = Op(M("a", true, "SG Quality", verified: true), M("b", true, null, verified: true));
        var unpinned = await Find(one, new { name = "SG Quality" });
        Assert.Equal("unpinned", unpinned.GetProperty("result").GetString());
        var trimmed = await Find(one, new { name = " SG Quality " });
        Assert.Equal("unpinned", trimmed.GetProperty("result").GetString());
        var pinned = await Find(one, new { name = "SG Quality", machine = "a" });
        Assert.Equal("found", pinned.GetProperty("result").GetString());
        Assert.Equal(["id", "machine", "node", "online", "since", "state"], pinned.GetProperty("session").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var two = Op(M("a", true, "SG Quality", verified: true), M("b", true, "SG Quality", verified: true), M("c", false, null));
        var amb = await Find(two, new { name = "SG Quality" });
        Assert.Equal("ambiguous", amb.GetProperty("result").GetString());
        var none = await Find(one, new { name = "SG Nobody" });
        foreach (var r in new[] { unpinned, trimmed, pinned, amb, none })
        {
            Assert.DoesNotContain("SG Quality", r.GetRawText());
            Assert.DoesNotContain("SG Nobody", r.GetRawText());
        }
        Assert.Equal("notFound", (await Find(one, new { name = "sg quality" })).GetProperty("result").GetString());
    }

    [Fact]
    public async Task A_truncated_live_machine_makes_the_answer_incomplete_even_with_a_verified_match()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var op = Op(M("a", true, "SG Quality", verified: true), M("b", true, null, truncated: true));
        var r = await Find(op, new { name = "SG Quality" });
        Assert.Equal("incomplete", r.GetProperty("result").GetString());
        Assert.Equal("b", r.GetProperty("machines")[0].GetString());
        Assert.Equal("found", (await Find(op, new { name = "SG Quality", machine = "a" })).GetProperty("result").GetString());
    }

    [Fact]
    public async Task Find_is_refused_without_hello_or_the_read_scope_or_a_usable_name_and_routes_through_the_local_api()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var op = Op(M("a", true, "SG Quality"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => op.FindAsync(new Chan(), Body(new { name = "x" })));
        var low = await Assert.ThrowsAsync<InvalidOperationException>(() => op.FindAsync(Hello(AgentProtocol.ScopeNone), Body(new { name = "x" })));
        Assert.Contains("fleet.find needs at least 'read'", low.Message);
        foreach (var bad in new object[] { new { }, new { name = "   " }, new { name = new string('x', 65) }, new { name = "x", machine = "bad machine" } })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => op.FindAsync(Hello(AgentProtocol.ScopeRead), Body(bad)));
        }
        Assert.Empty(audited);
        var handler = LocalApi.Handler(null!, null!, null!, null, null, store, null, op);
        var json = JsonSerializer.Serialize(await handler(Hello(AgentProtocol.ScopeRead), FleetList.FindOp, Body(new { name = "SG Quality" })), Web);
        Assert.Contains("\"unverified\"", json);
    }
}
