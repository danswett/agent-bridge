using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Fleet;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Relay;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Microsoft.Extensions.Time.Testing;
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

    sealed class Roster(params FleetMachine[] machines) : IScopedFleetRoster
    {
        public string Scope { get; init; } = "account-a";
        public Task<IReadOnlyList<FleetMachine>> ReadAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<FleetMachine>>(machines);
        public Task<FleetRosterSnapshot> ReadScopedAsync(CancellationToken ct) => Task.FromResult(new FleetRosterSnapshot(Scope, machines));
    }

    sealed class RegistryRoster(SessionRegistry registry, string account) : IScopedFleetRoster
    {
        public async Task<IReadOnlyList<FleetMachine>> ReadAsync(CancellationToken ct) => (await ReadScopedAsync(ct)).Machines;
        public Task<FleetRosterSnapshot> ReadScopedAsync(CancellationToken ct) => Task.FromResult(new FleetRosterSnapshot(account,
            RelayFleetRoster.Parse(registry.Read(account).Select(r => Body(new
            {
                machine = r.Machine, online = r.Fresh, record = JsonSerializer.Deserialize<JsonElement>(r.Json),
            }))).Select(m => m with { Verified = true }).ToList()));
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

    FleetList Op(params FleetMachine[] machines) => new(store, new Roster(machines), audit: new AgentAuditLog(root)) { AuditFleetList = audited.Add };

    static FleetMachine M(string id, bool online, string? name, bool truncated = false, bool verified = false) =>
        new(id, online, name is null ? [] : [new FleetSession("n-" + id, name, "working", Started, "s-" + id)], truncated, verified);

    static JsonElement Body(object o) => JsonSerializer.SerializeToElement(o);

    async Task<JsonElement> Find(FleetList op, object data) =>
        JsonSerializer.SerializeToElement(await op.FindAsync(Hello(AgentProtocol.ScopeRead), Body(data)), Web);

    [Fact]
    public async Task A_unique_verified_live_name_is_found_without_a_machine_pin_and_returns_its_model()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var session = JsonSerializer.Deserialize<FleetSession>("""
            {"id":"s-a","node":"n-a","name":"SG Quality","state":"working",
             "since":"2026-11-07T12:00:00Z","model":"gpt-6.1-sol"}
            """, Web)!;
        var op = Op(M("a", true, null, verified: true) with { Sessions = [session] });
        var result = await Find(op, new { name = "SG Quality", model = "caller-spoof" });
        Assert.Equal("found", result.GetProperty("result").GetString());
        var found = result.GetProperty("session");
        Assert.Equal("n-a", found.GetProperty("node").GetString());
        Assert.Equal("gpt-6.1-sol", found.GetProperty("model").GetString());
        Assert.DoesNotContain("SG Quality", result.GetRawText());
        Assert.DoesNotContain("caller-spoof", result.GetRawText());
    }

    [Fact]
    public void The_roster_preserves_a_supplied_model_instead_of_guessing_one()
    {
        var frame = Body(new
        {
            machine = "a", online = true,
            record = new { sessions = new[] { new { id = "s-a", node = "n-a", state = "working", since = Started, model = "gpt-6.1-sol" } } }
        });
        var session = Assert.Single(Assert.Single(RelayFleetRoster.Parse([frame])).Sessions);
        Assert.Equal("gpt-6.1-sol", JsonSerializer.SerializeToElement(session, Web).GetProperty("model").GetString());
    }

    [Theory]
    [InlineData(false, "notFound")]
    [InlineData(true, "found")]
    public async Task A_name_in_another_account_cannot_be_selected_or_create_ambiguity_by_spoofing_the_scope(bool ownMatch, string expected)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var registry = new SessionRegistry(TimeProvider.System);
        foreach (var account in ownMatch ? new[] { "account-a", "account-b" } : ["account-b"])
        {
            var hex = new string('a', 16);
            Assert.Null(registry.Put(account, "fixture", Body(new
            {
                v = 1, machine = "fixture", epoch = hex, seq = 1, at = DateTimeOffset.UtcNow, truncated = false,
                sessions = new[] { new { id = hex, node = "agent_bridge_" + hex, name = "SG Quality", state = "idle", since = Started } },
            })));
        }
        var op = new FleetList(store, new RegistryRoster(registry, "account-a"), audit: new AgentAuditLog(root));
        var result = await Find(op, new { name = "SG Quality", account = "account-b", fleetScope = "account-b" });
        Assert.Equal(expected, result.GetProperty("result").GetString());
        Assert.Equal("account-a", result.GetProperty("fleetScope").GetString());
        Assert.DoesNotContain("account-b", result.GetRawText());
    }

    [Fact, Trait("Category", "Network")]
    public async Task Connected_machines_without_a_registry_record_are_online_including_the_callers_own_host()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await using var relay = await TestRelay.StartAsync();
        await using var own = await relay.ConnectAsync(relay.TokenFor(1, "own"), "own-host", "host");
        await using var peer = await relay.ConnectAsync(relay.TokenFor(1, "own"), "peer-host", "host");
        await using var app = await relay.ConnectAsync(relay.TokenFor(1, "own"), "own-app", "app");
        await using var foreign = await relay.ConnectAsync(relay.TokenFor(2, "other"), "foreign-host", "host");
        var roster = new RelayFleetRoster();
        roster.Attach(own.Relay);
        var op = new FleetList(store, roster, audit: new AgentAuditLog(root));
        var reply = JsonSerializer.SerializeToElement(await op.HandleAsync(Hello(AgentProtocol.ScopeRead), Body(new { })), Web);
        Assert.Empty(reply.GetProperty("sessions").EnumerateArray());
        var machines = reply.GetProperty("machines").EnumerateArray().ToList();
        Assert.Equal(new[] { own.Id, peer.Id }.Order(StringComparer.Ordinal),
            machines.Select(m => m.GetProperty("machine").GetString()).Order(StringComparer.Ordinal));
        Assert.All(machines, m => Assert.Equal("online", m.GetProperty("state").GetString()));
    }

    [Theory, Trait("Category", "Network")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Listing_with_a_session_or_registered_script_credential_never_exposes_another_accounts_machine(bool script, bool foreignOffline)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var time = new FakeTimeProvider(Started);
        await using var relay = await TestRelay.StartAsync(o => o.Roster.Time = time);
        await using var own = await relay.ConnectAsync(relay.TokenFor(1, "own"), "own-host", "host");
        await using var other = await relay.ConnectAsync(relay.TokenFor(2, "other"), "other-host", "host");
        await using var otherViewer = await relay.ConnectAsync(relay.TokenFor(2, "viewer"), "other-viewer", "app");
        foreach (var (connection, hex) in new[]
        {
            (own, new string('a', 16)),
            (other, new string('b', 16)),
        })
        {
            if (connection == other) { time.Advance(TimeSpan.FromSeconds(10)); }
            await connection.Relay.RegistryPutAsync(Body(new
            {
                v = 1, machine = connection.Id, epoch = hex, seq = 1, at = DateTimeOffset.UtcNow, truncated = false,
                sessions = new[] { new { id = hex, node = "agent_bridge_" + hex, state = "idle", since = Started } },
            }));
        }
        if (foreignOffline)
        {
            await otherViewer.Relay.WaitForPeerAsync(p => p.Id == other.Id, TimeSpan.FromSeconds(10));
            time.Advance(TimeSpan.FromSeconds(10));
            await other.DisposeAsync();
            await Wait.UntilAsync(() => otherViewer.Relay.Known?.Any(k => k.Id == other.Id) == true, "the foreign machine is offline");
            var metadata = Assert.Single(otherViewer.Relay.Known!, k => k.Id == other.Id);
            Assert.Equal("other-host", metadata.Name);
            Assert.Equal(Started.AddSeconds(20), metadata.LastSeen);
        }
        var roster = new RelayFleetRoster();
        roster.Attach(own.Relay);
        var chan = script ? new Chan() : Hello(AgentProtocol.ScopeRead);
        if (script) { store.Accept(chan, store.MintScript("roster-reader").Credential); }
        var op = new FleetList(store, roster, audit: new AgentAuditLog(root));
        var handler = LocalApi.Handler(null!, null!, null!, credentials: store, fleet: op);
        var reply = JsonSerializer.SerializeToElement(await handler(chan, FleetList.Op,
            Body(new { account = other.Relay.AccountId, fleetScope = other.Relay.AccountId })), Web);
        var machine = Assert.Single(reply.GetProperty("machines").EnumerateArray());
        Assert.Equal(own.Id, machine.GetProperty("machine").GetString());
        Assert.Equal("own-host", machine.GetProperty("machineName").GetString());
        Assert.Equal(Started, machine.GetProperty("lastSeen").GetDateTimeOffset());
        Assert.Equal(own.Id, Assert.Single(reply.GetProperty("sessions").EnumerateArray()).GetProperty("machine").GetString());
        Assert.DoesNotContain(other.Id, reply.GetRawText());
        Assert.DoesNotContain("other-host", reply.GetRawText());
        Assert.NotEqual(own.Relay.AccountId, other.Relay.AccountId);
        Assert.Equal(["machines", "sessions"], reply.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.All(reply.GetProperty("machines").EnumerateArray(), m =>
            Assert.Equal(["complete", "lastSeen", "machine", "machineName", "state"], m.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));
        Assert.All(reply.GetProperty("sessions").EnumerateArray(), s =>
            Assert.Equal(["machine", "machineName", "machineOnline", "node", "secondsInState", "since", "state"],
                s.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)));
    }

    [Fact, Trait("Category", "Network")]
    public async Task A_real_roster_read_keeps_a_disconnected_machine_after_its_session_record_is_gone()
    {
        await using var relay = await TestRelay.StartAsync();
        await using var own = await relay.ConnectAsync(relay.TokenFor(1, "own"), "own-host", "host");
        await using var leaving = await relay.ConnectAsync(relay.TokenFor(1, "leaving"), "offline-box", "host");
        await own.Relay.WaitForPeerAsync(p => p.Id == leaving.Id, TimeSpan.FromSeconds(10));
        var hex = new string('a', 16);
        await leaving.Relay.RegistryPutAsync(Body(new
        {
            v = 1, machine = leaving.Id, epoch = hex, seq = 1, at = DateTimeOffset.UtcNow, truncated = false,
            sessions = new[] { new { id = hex, node = "agent_bridge_" + hex, state = "idle", since = Started } },
        }));
        await leaving.DisposeAsync();
        await Wait.UntilAsync(() => own.Relay.Known?.Any(k => k.Id == leaving.Id) == true, "the disconnected machine is known");
        var metadata = Assert.Single(own.Relay.Known!, k => k.Id == leaving.Id);
        var roster = new RelayFleetRoster();
        roster.Attach(own.Relay);
        var snapshot = await roster.ReadScopedAsync(default);
        Assert.Equal(own.Relay.AccountId, snapshot.FleetScope);
        var gone = Assert.Single(snapshot.Machines, m => m.Id == leaving.Id);
        Assert.False(gone.Online);
        Assert.Empty(gone.Sessions);
        Assert.Equal(metadata.Name, gone.Name);
        Assert.Equal(metadata.LastSeen, gone.LastSeen);
    }

    [Fact, Trait("Category", "Network")]
    public async Task A_real_relay_connection_supplies_its_account_scope_and_never_resolves_another_accounts_name()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await using var relay = await TestRelay.StartAsync();
        await using var own = await relay.ConnectAsync(relay.TokenFor(1, "own"), "own-host", "host");
        await using var other = await relay.ConnectAsync(relay.TokenFor(2, "other"), "other-host", "host");
        var hex = new string('b', 16);
        await other.Relay.RegistryPutAsync(Body(new
        {
            v = 1, machine = other.Id, epoch = hex, seq = 1, at = DateTimeOffset.UtcNow, truncated = false,
            sessions = new[] { new { id = hex, node = "agent_bridge_" + hex, name = "SG Other", state = "idle", since = Started } },
        }));
        var roster = new RelayFleetRoster();
        roster.Attach(own.Relay);
        var result = await Find(new FleetList(store, roster, audit: new AgentAuditLog(root)),
            new { name = "SG Other", fleetScope = other.Relay.AccountId, account = other.Relay.AccountId });
        Assert.Equal("notFound", result.GetProperty("result").GetString());
        Assert.Equal(own.Relay.AccountId, result.GetProperty("fleetScope").GetString());
        Assert.NotEqual(own.Relay.AccountId, other.Relay.AccountId);
        Assert.DoesNotContain("node", result.GetRawText());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task A_roster_without_authenticated_scope_refuses_instead_of_returning_a_success_shaped_answer(string scope)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var op = new FleetList(store, new Roster { Scope = scope }, audit: new AgentAuditLog(root));
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => Find(op, new { name = "SG Quality" }));
        Assert.Contains("no authenticated account scope", refusal.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(42)]
    public async Task Missing_or_non_string_roster_models_are_never_inferred_from_the_name_or_the_request(object? model)
    {
        var frame = Body(new
        {
            machine = "a", online = true,
            record = new { sessions = new[] { new { id = "s-a", node = "n-a", name = "SG Quality", state = "idle", since = Started, model } } }
        });
        var machine = Assert.Single(RelayFleetRoster.Parse([frame])) with { Verified = true };
        if (!OperatingSystem.IsWindows()) { return; }
        var result = await Find(Op(machine), new { name = "SG Quality", model = "caller-spoof" });
        Assert.Equal("found", result.GetProperty("result").GetString());
        Assert.False(result.GetProperty("session").TryGetProperty("model", out _));
    }

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
    public async Task Verified_names_are_found_ambiguous_or_unknown_without_echoing_the_name()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var one = Op(M("a", true, "SG Quality", verified: true), M("b", true, null, verified: true));
        var named = await Find(one, new { name = "SG Quality" });
        Assert.Equal("found", named.GetProperty("result").GetString());
        Assert.Equal("a", named.GetProperty("session").GetProperty("machine").GetString());
        var trimmed = await Find(one, new { name = " SG Quality " });
        Assert.Equal("found", trimmed.GetProperty("result").GetString());
        var pinned = await Find(one, new { name = "SG Quality", machine = "a" });
        Assert.Equal("found", pinned.GetProperty("result").GetString());
        Assert.Equal(["id", "machine", "node", "online", "since", "state"], pinned.GetProperty("session").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var two = Op(M("a", true, "SG Quality", verified: true), M("b", true, "SG Quality", verified: true), M("c", false, null));
        var amb = await Find(two, new { name = "SG Quality" });
        Assert.Equal("ambiguous", amb.GetProperty("result").GetString());
        var withOffline = await Find(Op(M("a", true, "SG Quality", verified: true), M("c", false, "SG Quality")), new { name = "SG Quality" });
        Assert.Equal("found", withOffline.GetProperty("result").GetString());
        var sameMachine = await Find(Op(M("a", true, "SG Quality", verified: true) with
        {
            Sessions = [new("n-1", "SG Quality", "idle", Started), new("n-2", "SG Quality", "working", Started)],
        }), new { name = "SG Quality" });
        Assert.Equal("ambiguous", sameMachine.GetProperty("result").GetString());
        var none = await Find(one, new { name = "SG Nobody" });
        Assert.Equal("notFound", none.GetProperty("result").GetString());
        foreach (var r in new[] { named, trimmed, pinned, amb, none, sameMachine, withOffline })
        {
            Assert.Equal("account-a", r.GetProperty("fleetScope").GetString());
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
