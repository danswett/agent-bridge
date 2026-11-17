using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Fleet;
using AgentBridge.Roster;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class FleetHostVerificationTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    static readonly string Node = "agent_bridge_" + new string('a', 16);
    static JsonElement Json(JsonNode value) => JsonSerializer.SerializeToElement(value);
    static JsonElement Find(IReadOnlyList<FleetMachine> machines, string? filter = null) =>
        JsonSerializer.SerializeToElement(FleetList.Find(new("account", machines), "SG Fixture", filter));

    [Fact]
    public void Only_an_active_enrolled_host_signature_can_supply_a_lookup_node()
    {
        var f = new Fixture();
        var machines = f.Read(f.Record());
        Assert.True(Assert.Single(machines).Verified);
        Assert.True(machines[0].HasRegistryRecord);
        foreach (var filter in new[] { null, f.Machine })
        {
            var found = Find(machines, filter);
            Assert.Equal("found", found.GetProperty("result").GetString());
            Assert.Equal(Node, found.GetProperty("session").GetProperty("Node").GetString());
        }
        f.World.Remove("removed", "G", "H", "H");
        Assert.All(f.World.Feed(f.Roster, "removed"), o => Assert.Equal(Verdict.Accepted, o.Verdict));
        Assert.Empty(Assert.Single(f.Read(f.Record(seq: 3))).Sessions);
    }

    [Theory]
    [InlineData(-61, false)]
    [InlineData(-60, true)]
    [InlineData(60, true)]
    [InlineData(61, false)]
    public void The_reader_checks_signed_time_against_its_own_clock(int seconds, bool accepted)
    {
        var f = new Fixture();
        var machine = Assert.Single(f.Read(f.Record(seconds: seconds)));
        Assert.Equal(accepted, machine.Verified);
        Assert.Equal(accepted, machine.HasRegistryRecord);
        Assert.Equal(accepted ? 1 : 0, machine.Sessions.Count);
    }

    [Fact]
    public void Sequence_and_epoch_order_survive_reads_and_invalid_inputs_do_not_advance_them()
    {
        var f = new Fixture();
        var accepted = f.Record();
        Assert.True(Assert.Single(f.Read(accepted)).Verified);
        Assert.True(Assert.Single(f.Read(accepted)).Verified);
        foreach (var record in new[] { f.Record(seq: 1), f.Record(epoch: new string('c', 16)) })
        { Assert.Empty(Assert.Single(f.Read(record)).Sessions); }
        Assert.True(Assert.Single(f.Read(f.Record(seq: 3))).Verified);
        Assert.True(Assert.Single(f.Read(f.Record(seq: 1, epoch: new string('c', 16), seconds: 1))).Verified);
        Assert.Empty(Assert.Single(f.Read(f.Record(seq: 4))).Sessions);
    }

    [Fact]
    public void A_host_signed_by_an_approver_on_probation_cannot_authorize_its_fleet_record()
    {
        var f = new Fixture();
        var world = new World();
        world.Genesis();
        world.Add("+A", "G", "A", "G");
        world.AddHostV2("+H", "A", "H", "+A");
        var roster = new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => world.Now) { AcceptHostSign = true };
        Assert.All(world.Feed(roster, [.. world.Labels]), offer => Assert.Equal(Verdict.Accepted, offer.Verdict));
        Assert.NotNull(roster.State.ActiveAdd(f.Machine));
        Assert.DoesNotContain(f.Machine, roster.Serving.Keys);
        f.Trusted = roster;
        var machine = Assert.Single(f.Read(f.Record()));
        Assert.False(machine.Verified);
        Assert.Empty(machine.Sessions);
    }

    [Fact]
    public void Forged_wrong_key_and_relay_rewritten_records_are_dropped_before_listing_or_lookup()
    {
        var f = new Fixture();
        var bad = f.Record(seq: 99);
        bad["sig"] = "AQ";
        Assert.Empty(Assert.Single(f.Read(bad)).Sessions);
        var wrong = FleetRecordSigning.Sign(Json(f.Record()), World.Keys("other").Sign.Sign);
        Assert.Empty(Assert.Single(f.Read(JsonNode.Parse(wrong.GetRawText())!.AsObject())).Sessions);
        var altered = f.Record();
        altered["sessions"]![0]!["node"] = "agent_bridge_" + new string('f', 16);
        Assert.Empty(Assert.Single(f.Read(altered)).Sessions);
        Assert.Empty(Assert.Single(f.Read(f.Record(), "another-machine")).Sessions);
        Assert.True(Assert.Single(f.Read(f.Record())).Verified);
    }

    [Theory, MemberData(nameof(FleetRecordSigningTests.Mutations), MemberType = typeof(FleetRecordSigningTests))]
    public void The_shared_signed_field_tamper_vectors_cannot_supply_sessions(string name, string path, string value)
    {
        var f = new Fixture();
        var record = VectorFiles.Read("fleet-registry-signing.json")["record"]!.DeepClone().AsObject();
        record["machine"] = f.Machine;
        record["at"] = Now.ToString("O", CultureInfo.InvariantCulture);
        record = f.Sign(record);
        JsonNode target = record;
        var parts = path.Split('.');
        foreach (var part in parts[..^1]) { target = target is JsonArray array ? array[int.Parse(part, CultureInfo.InvariantCulture)]! : target[part]!; }
        target[parts[^1]] = JsonNode.Parse(value);
        Assert.Empty(Assert.Single(f.Read(record)).Sessions);
        Assert.True(Assert.Single(f.Read(f.Record())).Verified, name);
    }

    [Fact]
    public void Unsigned_hints_never_become_verified_and_missing_lists_never_become_complete()
    {
        var f = new Fixture();
        var unsigned = f.Record();
        unsigned.Remove("sig");
        var hints = f.Read(unsigned);
        Assert.False(Assert.Single(hints).Verified);
        foreach (var filter in new[] { null, f.Machine })
        {
            var result = Find(hints, filter);
            Assert.Equal("unverified", result.GetProperty("result").GetString());
            Assert.DoesNotContain("node", result.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        var missing = f.Record();
        missing.Remove("sessions");
        var rejected = Assert.Single(f.Read(f.Sign(missing)));
        Assert.False(rejected.HasRegistryRecord);
        Assert.Empty(rejected.Sessions);
        Assert.False(Assert.Single(RelayFleetRoster.Parse([JsonSerializer.SerializeToElement(new
        { machine = f.Machine, online = true, record = (object?)null })])).HasRegistryRecord);
        var empty = f.Record();
        empty["sessions"] = new JsonArray();
        Assert.True(Assert.Single(f.Read(f.Sign(empty))).HasRegistryRecord);
    }

    [Theory]
    [InlineData("v", "2")]
    [InlineData("epoch", "\"invalid\"")]
    [InlineData("seq", "0")]
    [InlineData("at", "\"not a time\"")]
    [InlineData("sessions", "null")]
    [InlineData("prompt", "\"private\"")]
    [InlineData("truncated", "1")]
    [InlineData("total", "65")]
    [InlineData("seq", "\"2\"")]
    [InlineData("sessions.0.node", "\"agent_bridge_short\"")]
    [InlineData("sessions.0.id", "\"bad\"")]
    [InlineData("sessions.0.state", "\"invented\"")]
    [InlineData("sessions.0.since", "\"not a time\"")]
    [InlineData("sessions.0.name", "\"hidden\\nname\"")]
    public void A_signature_does_not_authorize_an_invalid_record_shape(string field, string json)
    {
        var f = new Fixture();
        var record = f.Record();
        var parts = field.Split('.');
        JsonNode target = parts.Length == 1 ? record : record["sessions"]![0]!;
        target[parts[^1]] = JsonNode.Parse(json);
        var machine = Assert.Single(f.Read(f.Sign(record)));
        Assert.False(machine.Verified);
        Assert.Empty(machine.Sessions);
        Assert.True(Assert.Single(f.Read(f.Record())).Verified);
    }

    [Fact]
    public void No_roster_or_a_v1_add_or_another_accounts_roster_cannot_authorize_a_signed_record()
    {
        var f = new Fixture();
        f.Trusted = null;
        Assert.Empty(Assert.Single(f.Read(f.Record())).Sessions);
        f.Trusted = f.Roster;
        Assert.True(Assert.Single(f.Read(f.Record())).Verified);
        var v1 = new World();
        v1.Genesis();
        v1.Add("H", "G", "H", "G", kind: "host");
        var reader = new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => v1.Now);
        Assert.All(v1.Feed(reader, "G", "H"), o => Assert.Equal(Verdict.Accepted, o.Verdict));
        f.Trusted = reader;
        Assert.Empty(Assert.Single(f.Read(f.Record(seq: 3))).Sessions);
        var verifier = new FleetRecordVerifier(() => f.Trusted, new FakeTimeProvider(Now));
        Assert.Throws<InvalidOperationException>(() => verifier.Verify(f.Machine, Json(f.Record()), "another-account"));
    }

    [Fact]
    public void Relay_verification_claims_are_ignored_and_schema_timestamps_remain_readable()
    {
        var f = new Fixture();
        var record = f.Record();
        record["sessions"]![0]!["since"] = "November 12, 2026 12:00:00Z";
        record = f.Sign(record);
        var frame = JsonSerializer.SerializeToElement(new { machine = f.Machine, online = true, verified = true, record });
        Assert.Empty(Assert.Single(RelayFleetRoster.Parse([frame])).Sessions);
        Assert.Equal(Now, Assert.Single(Assert.Single(f.Read(record)).Sessions).Since);
    }

    sealed class Fixture
    {
        public World World { get; } = new();
        public RosterVerifier Roster { get; }
        public RosterVerifier? Trusted { get; set; }
        public string Machine => global::World.Keys("H").Dh.Device;
        readonly RelayFleetRoster reader;

        public Fixture()
        {
            World.Genesis();
            World.AddHostV2("H", "G", "H", "G");
            Roster = new(RosterFixture.Account, RosterFixture.Generation, () => World.Now) { AcceptHostSign = true };
            Assert.All(World.Feed(Roster, "G", "H"), o => Assert.Equal(Verdict.Accepted, o.Verdict));
            Trusted = Roster;
            reader = new(() => Trusted, new FakeTimeProvider(Now));
        }

        public JsonObject Sign(JsonObject record) =>
            JsonNode.Parse(FleetRecordSigning.Sign(Json(record), global::World.Keys("H").Sign.Sign).GetRawText())!.AsObject();

        public JsonObject Record(long seq = 2, string? epoch = null, int seconds = 0) => Sign(JsonNode.Parse(
            JsonSerializer.Serialize(new
            {
                v = 1, machine = Machine, epoch = epoch ?? new string('b', 16), seq, at = Now.AddSeconds(seconds),
                truncated = false, sessions = new[] { new { id = new string('a', 16), node = Node, name = "SG Fixture", state = "idle", since = Now } },
            }))!.AsObject());

        public IReadOnlyList<FleetMachine> Read(JsonObject record, string? machine = null) =>
            reader.Read([JsonSerializer.SerializeToElement(new { machine = machine ?? Machine, online = true, record })]);
    }
}
