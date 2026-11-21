using System.Diagnostics;
using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Fleet;
using AgentBridge.Host.Lite;
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

    sealed class FrameRoster(RelayFleetRoster reader, JsonElement frame, string account = "account-a") : IScopedFleetRoster
    {
        public Action? Reading;
        public Task<IReadOnlyList<FleetMachine>> ReadAsync(CancellationToken ct) => Task.FromResult(reader.Read([frame]));
        public Task<FleetRosterSnapshot> ReadScopedAsync(CancellationToken ct)
        {
            Reading?.Invoke();
            return Task.FromResult(new FleetRosterSnapshot(account, reader.Read([frame])));
        }
    }

    sealed class LiteLink : ILiteLink
    {
        public string Self => "self";
        public string Account { get; set; } = "account-a";
        public IReadOnlyList<PresenceEntry> Presence { get; set; } = [new("a", "host-a", "host", Started)];
        public Task<LiteAck> SendAsync(string peerId, string account, object request, CancellationToken ct) => throw new NotSupportedException();
    }

    static JsonElement LiteFrame() => Body(new
    {
        machine = "a", online = true,
        record = new { sessions = new[] { new { node = "n-a", name = "SG Quality", state = "idle", since = Started } } },
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Own_account_find_and_list_bypass_missing_or_unreadable_roster_pins_without_claiming_verification(bool unreadable, bool find)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var pin = Path.Combine(root, "roster-pin.json");
        if (unreadable) { File.WriteAllText(pin, "{broken"); }
        var trust = new FleetTrust(new HostRosterReader(pin, HostRoster.Now), root, () => "account-a");
        var signed = new FrameRoster(new RelayFleetRoster(trust.Membership), LiteFrame());
        if (unreadable) { Assert.Throws<InvalidOperationException>(() => trust.Membership()); }
        var own = new LiteFleetRoster(new FrameRoster(new RelayFleetRoster(), LiteFrame()), new LiteKill(root), new LiteLink());
        var op = new FleetList(store, signed, audit: new AgentAuditLog(root), messagingRoster: own);
        if (find)
        {
            var found = await Find(op, new { name = "SG Quality" });
            Assert.Equal("found", found.GetProperty("result").GetString());
            Assert.Equal("own-account", found.GetProperty("session").GetProperty("trust").GetString());
        }
        else
        {
            var list = JsonSerializer.SerializeToElement(await op.HandleAsync(Hello(AgentProtocol.ScopeRead), Body(new { include = "content" })), Web);
            Assert.Equal("own-account", Assert.Single(list.GetProperty("sessions").EnumerateArray()).GetProperty("trust").GetString());
        }
        Assert.False(Assert.Single((await own.ReadScopedAsync(default)).Machines).Verified);
        Assert.Null(trust.VerifiedRoster());
        Assert.Equal(unreadable, File.Exists(pin));
    }

    [Fact]
    public async Task Own_account_lookup_does_not_use_signed_roster_membership_to_hide_a_present_host()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var world = new World();
        world.Genesis("P");
        var verified = new AgentBridge.Roster.RosterVerifier(world.AccountId, world.GenerationId, () => world.Now);
        world.Feed(verified, "G");
        var signed = new FrameRoster(new RelayFleetRoster(() => verified), LiteFrame());
        Assert.Empty(await signed.ReadAsync(default));
        var own = new LiteFleetRoster(new FrameRoster(new RelayFleetRoster(), LiteFrame()), new LiteKill(root), new LiteLink());
        var result = await Find(new FleetList(store, signed, audit: new AgentAuditLog(root), messagingRoster: own), new { name = "SG Quality" });
        Assert.Equal("found", result.GetProperty("result").GetString());
        Assert.Equal("own-account", result.GetProperty("session").GetProperty("trust").GetString());
        Assert.Empty(await signed.ReadAsync(default));
    }

    [Theory]
    [InlineData("off")]
    [InlineData("kill")]
    [InlineData("during-read")]
    public async Task Off_or_killed_own_account_find_and_list_refuse_with_off(string state)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var mode = new LiteKill(root);
        var frames = new FrameRoster(new RelayFleetRoster(), LiteFrame());
        if (state == "kill") { mode.Kill(); }
        else if (state == "off") { Assert.Equal(0, ConfigCommands.Run(["config", "set", "agentMessaging.remote", "off"], root, TextWriter.Null, TextWriter.Null)); }
        else { frames.Reading = () => mode.Kill(); }
        var own = new LiteFleetRoster(frames, mode, new LiteLink());
        var op = new FleetList(store, new Roster(M("a", true, "SG Quality", verified: true)), audit: new AgentAuditLog(root), messagingRoster: own);
        Assert.Contains("refused: off:", (await Assert.ThrowsAsync<InvalidOperationException>(() => Find(op, new { name = "SG Quality" }))).Message);
        Assert.Contains("refused: off:", (await Assert.ThrowsAsync<InvalidOperationException>(() => op.HandleAsync(Hello(AgentProtocol.ScopeRead), Body(new { })))).Message);
    }

    [Theory]
    [InlineData("other-account")]
    [InlineData("signed-out")]
    [InlineData("changed-account")]
    [InlineData("absent-host")]
    [InlineData("non-host")]
    public async Task Own_account_routing_rejects_other_accounts_and_never_finds_an_absent_or_non_host_peer(string state)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var link = new LiteLink();
        var frames = new FrameRoster(new RelayFleetRoster(), LiteFrame(), state == "other-account" ? "account-b" : "account-a");
        if (state == "signed-out") { link.Account = ""; }
        if (state == "changed-account") { frames.Reading = () => link.Account = "account-b"; }
        if (state == "absent-host") { link.Presence = []; }
        if (state == "non-host") { link.Presence = [new("a", "host-a", "phone", Started)]; }
        var own = new LiteFleetRoster(frames, new LiteKill(root), link);
        var op = new FleetList(store, new Roster(), audit: new AgentAuditLog(root), messagingRoster: own);
        if (state is "absent-host" or "non-host")
        {
            var found = await Find(op, new { name = "SG Quality" });
            Assert.Equal("offline", found.GetProperty("result").GetString());
            Assert.False(found.TryGetProperty("session", out _));
            Assert.Null(Assert.Single((await own.ReadScopedAsync(default)).Machines).MessagingTrust);
        }
        else
        {
            Assert.Contains("refused: notSameAccount:", (await Assert.ThrowsAsync<InvalidOperationException>(() => Find(op, new { name = "SG Quality" }))).Message);
            Assert.Contains("refused: notSameAccount:", (await Assert.ThrowsAsync<InvalidOperationException>(() => op.HandleAsync(Hello(AgentProtocol.ScopeRead), Body(new { })))).Message);
        }
    }

    [Fact]
    public async Task Host_wiring_without_a_pin_or_switch_never_returns_a_routing_node()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var pin = Path.Combine(root, "roster-pin.json");
        var trust = new FleetTrust(new HostRosterReader(pin, HostRoster.Now), root, () => "account-a");
        var reader = new RelayFleetRoster(trust.VerifiedRoster);
        var machines = reader.Read([Body(new
        {
            machine = "a", online = true,
            record = new { sessions = new[] { new { node = "n-a", name = "SG Quality", state = "idle", since = Started } } },
        })]);
        var result = await Find(Op(machines.ToArray()), new { name = "SG Quality" });
        Assert.Equal("unverified", result.GetProperty("result").GetString());
        Assert.False(result.TryGetProperty("session", out _));
        Assert.DoesNotContain("\"node\"", result.GetRawText());
        Assert.Null(trust.VerifiedRoster());
        Assert.False(File.Exists(pin));
        Assert.False(Directory.Exists(Path.Combine(root, "host-sign")));
    }

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
    public async Task A_protocol_shaped_session_returns_its_full_uuid_without_changing_id_or_node()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        const string uuid = "12345678-1234-5678-9abc-def012345678";
        const string hex = "1234567812345678";
        var source = new FleetPublisherTests.Source { Sessions = [FleetPublisherTests.NewSession(1) with { Id = uuid }] };
        var registry = new SessionRegistry(TimeProvider.System);
        Assert.True(await new FleetPublisher(source, "fixture", _ => "SG Quality").PublishAsync((record, _) =>
        {
            var row = Assert.Single(record.GetProperty("sessions").EnumerateArray());
            Assert.Equal(hex, row.GetProperty("id").GetString());
            Assert.Equal("agent_bridge_" + hex, row.GetProperty("node").GetString());
            Assert.Equal(uuid, row.GetProperty("sessionId").GetString());
            Assert.Null(registry.Put("account-a", "fixture", record));
            return Task.CompletedTask;
        }, relaySupportsSessionId: true));
        var result = await Find(new FleetList(store, new RegistryRoster(registry, "account-a"), audit: new AgentAuditLog(root)),
            new { name = "SG Quality", sessionId = "caller-spoof" });
        Assert.Equal("found", result.GetProperty("result").GetString());
        var found = result.GetProperty("session");
        Assert.Equal(hex, found.GetProperty("id").GetString());
        Assert.Equal("agent_bridge_" + hex, found.GetProperty("node").GetString());
        Assert.Equal(uuid, found.GetProperty("sessionId").GetString());
        Assert.DoesNotContain("caller-spoof", result.GetRawText());
    }

    // An older relay refuses the whole record for any member off its allow-list, so the host sends
    // sessionId only to a connection whose relay advertised it, and never carries that over.
    [Fact]
    public async Task Session_ids_go_only_to_a_connection_whose_relay_advertised_them_and_a_reconnect_without_it_drops_them()
    {
        const string uuid = "12345678-1234-5678-9abc-def012345678";
        var source = new FleetPublisherTests.Source { Sessions = [FleetPublisherTests.NewSession(1) with { Id = uuid }] };
        var publication = new FleetPublication(source, "fixture", share: true);
        var registry = new SessionRegistry(TimeProvider.System);
        var sent = new List<JsonElement>();
        foreach (var advertised in new[] { false, true, false })
        {
            var disconnected = new TaskCompletionSource();
            await publication.RunAsync((record, _) =>
            {
                sent.Add(record);
                Assert.Null(registry.Put("account-a", "fixture", record));
                disconnected.TrySetResult();
                return Task.CompletedTask;
            }, disconnected.Task, relaySupportsSessionId: advertised).WaitAsync(TimeSpan.FromSeconds(10));
        }
        var rows = sent.Select(r => Assert.Single(r.GetProperty("sessions").EnumerateArray())).ToList();
        Assert.Equal(new[] { false, true, false }, rows.Select(r => r.TryGetProperty("sessionId", out _)));
        Assert.Equal(uuid, rows[1].GetProperty("sessionId").GetString());
        // Without the capability the record is exactly what a publisher without sessionId sends,
        // and the relay still takes it.
        Assert.DoesNotContain("sessionId", sent[2].GetRawText());
        Assert.Null(RosterFields.Check(sent[2], "fixture", out _, out _, out _));
    }

    [Theory]
    [InlineData("""{"t":"ready","device":"d","account":"1","login":"x"}""", false)]
    [InlineData("""{"t":"ready","capabilities":["rosterSessionId"]}""", true)]
    [InlineData("""{"t":"ready","capabilities":["futureThing","rosterSessionId",7,null]}""", true)]
    [InlineData("""{"t":"ready","capabilities":["futureThing","rostersessionid","rosterSessionId2"]}""", false)]
    [InlineData("""{"t":"ready","capabilities":"rosterSessionId"}""", false)]
    [InlineData("""{"t":"ready","capabilities":{"rosterSessionId":true}}""", false)]
    public void Only_an_exact_advertised_capability_enables_session_ids_and_unknown_or_malformed_ones_are_ignored(string ready, bool expected) =>
        Assert.Equal(expected, RelayClient.ReadCapabilities(JsonDocument.Parse(ready).RootElement).Contains(FleetSessionId.RelayCapability));

    [Fact]
    [Trait("Category", "Network")]
    public async Task The_relay_advertises_session_id_support_in_ready()
    {
        await using var relay = await TestRelay.StartAsync();
        var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "host", "host");
        Assert.Contains(FleetSessionId.RelayCapability, host.Relay.Capabilities);
    }

    [Theory]
    [InlineData("87654321-1234-5678-9abc-def012345678")]
    [InlineData("12345678-1234-5678-9ABC-DEF012345678")]
    [InlineData("12345678123456789abcdef012345678")]
    [InlineData(" 12345678-1234-5678-9abc-def012345678 ")]
    [InlineData(null)]
    public async Task An_unbound_or_noncanonical_session_uuid_is_omitted_from_found(string? uuid)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var frame = Body(new
        {
            machine = "fixture", online = true,
            record = new { sessions = new[] { new
            {
                id = "1234567812345678", node = "agent_bridge_1234567812345678", sessionId = uuid,
                name = "SG Quality", state = "idle", since = Started,
            } } },
        });
        var machine = Assert.Single(RelayFleetRoster.Parse([frame])) with { Verified = true };
        var result = await Find(Op(machine), new { name = "SG Quality" });
        Assert.Equal("found", result.GetProperty("result").GetString());
        Assert.False(result.GetProperty("session").TryGetProperty("sessionId", out _));
    }

    [Fact]
    public void The_relay_refuses_noncanonical_session_ids_instead_of_carrying_arbitrary_identity_text()
    {
        foreach (var uuid in new[] { "SG Quality", "12345678123456789abcdef012345678", "12345678-1234-5678-9ABC-DEF012345678" })
        {
            var record = Body(new
            {
                v = 1, machine = "fixture", epoch = "1234567812345678", seq = 1, at = Started, truncated = false,
                sessions = new[] { new
                {
                    id = "1234567812345678", node = "agent_bridge_1234567812345678", sessionId = uuid,
                    state = "idle", since = Started,
                } },
            });
            Assert.Equal("bad-record", RosterFields.Check(record, "fixture", out _, out _, out _));
        }
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
        var timings = new List<string>();
        async Task<Device> ConnectObservedAsync(string token, string name)
        {
            var started = Stopwatch.GetTimestamp();
            Task<Device>? connecting = null;
            try
            {
                connecting = relay.ConnectAsync(token, name, "host");
                return await connecting;
            }
            finally
            {
                var connected = connecting?.IsCompletedSuccessfully == true;
                timings.Add($"{name}: connect {(connected ? "completed" : "failed")} after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3} ms");
                if (!connected)
                {
                    Console.WriteLine("Relay connection failed; phase is not inferred.");
                    Console.WriteLine($"Connection timings:{Environment.NewLine}{string.Join(Environment.NewLine, timings)}");
                    if (connecting?.Exception is { } failure)
                    {
                        foreach (var raw in failure.InnerExceptions) { Console.WriteLine(raw.ToString()); }
                    }
                    Console.WriteLine($"Relay logs:{Environment.NewLine}{string.Join(Environment.NewLine, relay.Logs)}");
                }
            }
        }
        await using var own = await ConnectObservedAsync(relay.TokenFor(1, "own"), "own-host");
        await using var other = await ConnectObservedAsync(relay.TokenFor(2, "other"), "other-host");
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
