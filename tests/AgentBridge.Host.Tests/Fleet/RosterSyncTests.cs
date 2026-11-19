using System.Text.Json;
using AgentBridge.Host.Fleet;
using AgentBridge.Protocol;
using AgentBridge.Relay;
using AgentBridge.Roster;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Cross-host roster sync (#2643) over fake registry frames; every machine is a roster fixture.
public sealed class RosterSyncTests
{
    static readonly DateTimeOffset Start = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    readonly FakeTimeProvider time = new(Start);
    readonly World world = new();
    readonly RosterVerifier signed;
    readonly RelayFleetRoster reader;

    public RosterSyncTests()
    {
        world.Genesis("P");
        world.Add("H", "P", "H", "G", kind: "host");
        world.Add("J", "P", "J", "H", kind: "host");
        signed = new RosterVerifier(world.AccountId, world.GenerationId, () => world.Now);
        Assert.All(world.Feed(signed, "G", "H"), r => Assert.Equal(Verdict.Accepted, r.Verdict));
        reader = new RelayFleetRoster(() => signed, time);
    }

    JsonElement Frame(string device, bool online = true, long seq = 1, string epoch = "00000000000000e1", int? at = null, string[]? ids = null) =>
        JsonSerializer.SerializeToElement(new
        {
            machine = world.Id(device), online,
            record = online ? new
            {
                epoch, seq, at = Start.AddSeconds(at ?? seq),
                sessions = (ids ?? []).Select(id => new { id, node = "agent_bridge_" + id, state = "idle", since = Start }).ToArray(),
            } : null,
        });

    JsonElement Known(string device) => JsonSerializer.SerializeToElement(new { known = new[] { new { machine = world.Id(device), online = false } } });

    static string[] Ids(FleetMachine machine) => [.. machine.Sessions.Select(s => s.Id!)];

    [Fact]
    public void Rows_from_a_peer_not_in_the_signed_roster_are_ignored()
    {
        var frames = new[] { Frame("H", ids: ["a"]), Frame("J", ids: ["b"]), Frame("X", ids: ["c"]), Known("P"), Body("""{"machine":"x","online":true,"record":{"sessions":[{"bad":1}]}}""") };
        var host = Assert.Single(reader.Read(frames));
        Assert.Equal(world.Id("H"), host.Id);
        Assert.Equal(["a"], Ids(host));
        Assert.False(host.Verified);
        // Until this host holds a verified signed roster, rows pass unverified, as before.
        Assert.Equal(3, new RelayFleetRoster(() => null).Read(frames[..3]).Count);
    }

    [Fact]
    public void Silent_host_rows_are_marked_stale_then_dropped_never_silently_removed()
    {
        Assert.True(Assert.Single(reader.Read([Frame("H", ids: ["a"])])).Online);
        time.Advance(TimeSpan.FromMinutes(2));
        var silent = Assert.Single(reader.Read([Frame("H", online: false)]));
        Assert.False(silent.Online);
        Assert.Equal(["a"], Ids(silent));
        Assert.Equal(["a"], Ids(Assert.Single(reader.Read([Known("H")]))));
        var row = JsonSerializer.SerializeToElement(new FleetListSession(silent.Id, "n", "idle", Start, 0, silent.Online, null), Web);
        Assert.True(row.GetProperty("stale").GetBoolean());
        Assert.False(JsonSerializer.SerializeToElement(new FleetListSession(silent.Id, "n", "idle", Start, 0, true, null), Web).TryGetProperty("stale", out _));
        time.Advance(TimeSpan.FromHours(24));
        Assert.Empty(Assert.Single(reader.Read([Frame("H", online: false)])).Sessions);
    }

    [Fact]
    public void Joining_host_appears_and_leaving_host_drops_without_config_change()
    {
        Assert.DoesNotContain(reader.Read([Frame("H", ids: ["a"]), Frame("J", ids: ["b"])]), m => m.Id == world.Id("J"));
        Assert.Equal(Verdict.Accepted, Assert.Single(world.Feed(signed, "J")).Verdict);
        var joined = Assert.Single(reader.Read([Frame("H", ids: ["a"]), Frame("J", ids: ["b"])]), m => m.Id == world.Id("J"));
        Assert.True(joined.Online);
        Assert.Equal(["b"], Ids(joined));
        world.Remove("-J", "P", "J", "J");
        Assert.Equal(Verdict.Accepted, Assert.Single(world.Feed(signed, "-J")).Verdict);
        Assert.DoesNotContain(reader.Read([Frame("H", ids: ["a"]), Known("J")]), m => m.Id == world.Id("J"));
        // A machine the relay no longer lists at all leaves at once, cache included.
        Assert.Empty(reader.Read([]));
        Assert.Empty(Assert.Single(reader.Read([Known("H")])).Sessions);
        // A new connection (perhaps another account) starts with nothing cached.
        Assert.Equal(["a"], Ids(Assert.Single(reader.Read([Frame("H", ids: ["a"])]))));
        reader.Attach(null);
        Assert.Empty(Assert.Single(reader.Read([Known("H")])).Sessions);
    }

    [Fact]
    public void Events_apply_in_order_and_a_gap_triggers_a_fresh_snapshot()
    {
        Assert.Equal(["a"], Ids(Assert.Single(reader.Read([Frame("H", seq: 5, ids: ["a"])]))));
        var replay = Assert.Single(reader.Read([Frame("H", seq: 3, ids: ["old"])]));
        Assert.False(replay.Online);
        Assert.Equal(["a"], Ids(replay));
        Assert.True(Assert.Single(reader.Read([Frame("H", seq: 5, ids: ["a"])])).Online);
        var gap = Assert.Single(reader.Read([Frame("H", seq: 9, ids: ["b", "c"])]));
        Assert.True(gap.Online);
        Assert.Equal(["b", "c"], Ids(gap));
        Assert.False(Assert.Single(reader.Read([Frame("H", seq: 1, epoch: "00000000000000e2", at: 4, ids: ["d"])])).Online);
        var restarted = Assert.Single(reader.Read([Frame("H", seq: 1, epoch: "00000000000000e2", at: 10, ids: ["d"])]));
        Assert.True(restarted.Online);
        Assert.Equal(["d"], Ids(restarted));
    }

    [Fact]
    public void A_record_more_than_60_s_from_this_hosts_clock_is_stale_even_while_the_relay_says_online()
    {
        var old = Assert.Single(reader.Read([Frame("H", seq: 1, at: -61, ids: ["a"])]));
        Assert.False(old.Online);
        Assert.Equal(["a"], Ids(old));
        Assert.False(Assert.Single(reader.Read([Frame("H", seq: 2, at: 61, ids: ["a"])])).Online);
        Assert.True(Assert.Single(reader.Read([Frame("H", seq: 3, at: 60, ids: ["b"])])).Online);
        // A half-open host: the relay still lists its last record as online, but nothing new comes.
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.True(Assert.Single(reader.Read([Frame("H", seq: 3, at: 60, ids: ["b"])])).Online);
        time.Advance(TimeSpan.FromSeconds(1));
        var silent = Assert.Single(reader.Read([Frame("H", seq: 3, at: 60, ids: ["b"])]));
        Assert.False(silent.Online);
        Assert.Equal(["b"], Ids(silent));
    }

    [Fact]
    public void A_record_without_epoch_seq_and_at_never_replaces_the_cached_order()
    {
        Assert.True(Assert.Single(reader.Read([Frame("H", seq: 5, ids: ["a"])])).Online);
        var unordered = JsonSerializer.SerializeToElement(new
        {
            machine = world.Id("H"), online = true,
            record = new { sessions = new[] { new { id = "x", node = "agent_bridge_x", state = "idle", since = Start } } },
        });
        var refused = Assert.Single(reader.Read([unordered]));
        Assert.False(refused.Online);
        Assert.Equal(["a"], Ids(refused));
        // Had the unordered record been taken, nothing would be left to refuse this replay with.
        var replay = Assert.Single(reader.Read([Frame("H", seq: 3, ids: ["old"])]));
        Assert.False(replay.Online);
        Assert.Equal(["a"], Ids(replay));
    }

    sealed class SteppedClock : TimeProvider
    {
        public DateTimeOffset Wall = Start;
        public long Ticks;
        public override DateTimeOffset GetUtcNow() => Wall;
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    [Fact]
    public void Cached_rows_age_by_the_monotonic_clock_whatever_the_wall_clock_does()
    {
        var clock = new SteppedClock();
        var cached = new RelayFleetRoster(() => signed, clock);
        Assert.True(Assert.Single(cached.Read([Frame("H", ids: ["a"])])).Online);
        clock.Wall = Start.AddDays(2);
        clock.Ticks += TimeSpan.FromMinutes(2).Ticks;
        Assert.Equal(["a"], Ids(Assert.Single(cached.Read([Frame("H", online: false)]))));
        clock.Wall = Start.AddDays(-2);
        clock.Ticks += RelayFleetRoster.CacheAge.Ticks;
        Assert.Empty(Assert.Single(cached.Read([Frame("H", online: false)])).Sessions);
    }

    [Fact]
    public async Task Peer_rows_from_a_host_in_the_signed_device_roster_appear_with_all_A1_fields()
    {
        var source = new FleetPublisherTests.Source();
        source.Sessions[0] = source.Sessions[0] with
        {
            Owner = new() { State = SessionEvidenceState.Known, Principal = "agent:fixture/one" },
            Pr = new() { State = SessionEvidenceState.Known, Number = 42 },
            Finished = new() { State = SessionEvidenceState.Known, Value = true },
        };
        source.Sessions[1] = source.Sessions[1] with { Owner = new(), Pr = new() { State = SessionEvidenceState.None }, Finished = new() };
        // Malformed own evidence is left out rather than costing the whole record.
        source.Sessions[2] = source.Sessions[2] with { Owner = new() { State = SessionEvidenceState.Known }, Pr = new() { State = SessionEvidenceState.Known, Number = 0 } };
        var registry = new SessionRegistry(time);
        Assert.True(await new FleetPublisher(source, world.Id("H"), time: time).PublishAsync((record, _) =>
        {
            Assert.Null(registry.Put("account", world.Id("H"), record));
            return Task.CompletedTask;
        }));
        var record = JsonDocument.Parse(Assert.Single(registry.Read("account")).Json).RootElement.Clone();
        var host = Assert.Single(reader.Read([JsonSerializer.SerializeToElement(new { machine = world.Id("H"), online = true, record }), Frame("X", ids: ["c"])]));
        JsonElement[] Rows(FleetMachine m) => [.. m.Sessions.Select(s => JsonSerializer.SerializeToElement(new FleetListSession(m.Id, s.Node, s.State, s.Since, 0, m.Online, null, null, s.Evidence), Web))];
        // Newest state change first: sessions 3, 2, 1.
        var rows = Rows(host);
        Assert.Equal("agent:fixture/one", rows[2].GetProperty("owner").GetProperty("principal").GetString());
        Assert.Equal(42, rows[2].GetProperty("pr").GetProperty("number").GetInt32());
        Assert.True(rows[2].GetProperty("finished").GetProperty("value").GetBoolean());
        Assert.Equal("none", rows[1].GetProperty("pr").GetProperty("state").GetString());
        // Unknown travels as absent, as from an older host: never as none or false.
        Assert.False(rows[1].TryGetProperty("owner", out _) || rows[1].TryGetProperty("finished", out _));
        Assert.False(rows[0].TryGetProperty("owner", out _) || rows[0].TryGetProperty("pr", out _));
        time.Advance(TimeSpan.FromMinutes(2));
        var stale = Rows(Assert.Single(reader.Read([Frame("H", online: false)])));
        Assert.True(stale[2].GetProperty("stale").GetBoolean() && stale[2].GetProperty("finished").GetProperty("value").GetBoolean());
    }

    [Fact, Trait("Category", "Network")]
    public async Task Each_lookup_refreshes_the_signed_roster_first_so_a_host_that_left_is_gone_from_that_same_answer()
    {
        await using var relay = await TestRelay.StartAsync();
        await using var own = await relay.ConnectAsync(relay.TokenFor(1, "own"), "own-host", "host");
        await using var peer = await relay.ConnectAsync(relay.TokenFor(1, "own"), "peer-host", "host");
        await own.Relay.WaitForPeerAsync(p => p.Id == peer.Id, TimeSpan.FromSeconds(10));
        RosterVerifier? current = null;
        var seen = new List<RelayClient>();
        var roster = new RelayFleetRoster(() => current, time, (connection, _) =>
        {
            seen.Add(connection);
            current = new RosterVerifier(world.AccountId, world.GenerationId, () => world.Now);
            return Task.CompletedTask;
        });
        roster.Attach(own.Relay);
        // Before the refresh no roster is held, so every host is read; after it, none is in the roster.
        Assert.Empty((await roster.ReadScopedAsync(default)).Machines);
        Assert.Same(own.Relay, Assert.Single(seen));
    }

    [Fact, Trait("Category", "Network")]
    public async Task A_refresh_that_fails_leaves_the_lookup_on_the_roster_it_already_held()
    {
        await using var relay = await TestRelay.StartAsync();
        await using var own = await relay.ConnectAsync(relay.TokenFor(1, "own"), "own-host", "host");
        var tries = 0;
        var roster = new RelayFleetRoster(() => null, time, (_, _) => { tries++; throw new InvalidOperationException("roster unavailable"); });
        roster.Attach(own.Relay);
        Assert.Equal(own.Id, Assert.Single((await roster.ReadScopedAsync(default)).Machines).Id);
        Assert.Equal(1, tries);
    }

    static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
