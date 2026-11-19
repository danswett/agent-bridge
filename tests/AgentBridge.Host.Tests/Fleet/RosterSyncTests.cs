using System.Text.Json;
using AgentBridge.Host.Fleet;
using AgentBridge.Roster;
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

    static JsonElement Body(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
