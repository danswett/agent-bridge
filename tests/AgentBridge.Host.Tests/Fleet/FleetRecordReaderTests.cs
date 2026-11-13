using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Fleet;
using AgentBridge.Roster;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class FleetRecordReaderTests
{
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    static readonly DateTimeOffset At = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    static readonly string Node = "agent_bridge_" + new string('a', 16);

    static JsonElement Find(IReadOnlyList<FleetMachine> machines, string? machine = null) =>
        JsonSerializer.SerializeToElement(FleetList.Find(new("account-a", machines), "SG Fixture", machine), Web);

    [Theory]
    [InlineData("""{"known":[{"machine":"pending","online":true}]}""")]
    [InlineData("""{"machine":"pending","online":true}""")]
    [InlineData("""{"machine":"pending","online":true,"record":{}}""")]
    [InlineData("""{"machine":"pending","online":true,"record":{"sessions":null}}""")]
    public void A_live_machine_without_a_received_session_list_cannot_hide_a_duplicate(string json)
    {
        var pending = Assert.Single(RelayFleetRoster.Parse([JsonSerializer.Deserialize<JsonElement>(json)]));
        var verified = new FleetMachine("verified", true,
            [new(Node, "SG Fixture", "idle", At)], Verified: true);
        var reply = Find([verified, pending]);
        Assert.Equal("incomplete", reply.GetProperty("result").GetString());
        Assert.Equal("pending", Assert.Single(reply.GetProperty("machines").EnumerateArray()).GetString());
        Assert.DoesNotContain("node", reply.GetRawText());
        Assert.Equal("found", Find([verified, pending], "verified").GetProperty("result").GetString());
    }

    [Fact]
    public void A_received_empty_list_is_complete_enough_to_report_no_match()
    {
        var machines = RelayFleetRoster.Parse([JsonSerializer.Deserialize<JsonElement>(
            """{"machine":"fixture","online":true,"record":{"sessions":[]}}""")]);
        Assert.Equal("notFound", Find(machines).GetProperty("result").GetString());
    }

    [Fact]
    public void An_offline_machine_without_a_list_does_not_hide_a_live_verified_match()
    {
        var offline = Assert.Single(RelayFleetRoster.Parse([JsonSerializer.Deserialize<JsonElement>(
            """{"known":[{"machine":"offline","online":false}]}""")]));
        var verified = new FleetMachine("verified", true,
            [new(Node, "SG Fixture", "idle", At)], Verified: true);
        Assert.Equal("found", Find([verified, offline]).GetProperty("result").GetString());
    }

    [Fact]
    public void An_unsigned_record_never_supplies_a_lookup_node_with_or_without_a_machine_filter()
    {
        var machines = RelayFleetRoster.Parse([Frame()]);
        Assert.False(Assert.Single(machines).Verified);
        foreach (var filter in new string?[] { null, "fixture" })
        {
            var reply = Find(machines, filter);
            Assert.Equal("unverified", reply.GetProperty("result").GetString());
            Assert.DoesNotContain("node", reply.GetRawText());
            Assert.DoesNotContain("SG Fixture", reply.GetRawText());
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("AQ")]
    [InlineData("AA==")]
    public void The_default_reader_never_takes_verification_from_a_relay_claim_or_a_signature_field(string sig)
    {
        var frame = JsonNode.Parse(Frame().GetRawText())!.AsObject();
        frame["verified"] = true;
        frame["record"]!["verified"] = true;
        frame["record"]!["sig"] = sig;
        var machines = RelayFleetRoster.Parse([JsonSerializer.SerializeToElement(frame)]);
        Assert.False(Assert.Single(machines).Verified);
        foreach (var filter in new string?[] { null, "fixture" })
        {
            var reply = Find(machines, filter);
            Assert.Equal("unverified", reply.GetProperty("result").GetString());
            Assert.DoesNotContain("node", reply.GetRawText());
        }
    }

    [Fact]
    public void An_enrolled_host_signature_yields_a_live_node_with_and_without_a_machine_filter()
    {
        var fixture = new SignedFixture();
        var machines = fixture.Read(fixture.Record());
        Assert.True(Assert.Single(machines).Verified);
        foreach (var filter in new string?[] { null, fixture.Machine })
        {
            var reply = Find(machines, filter);
            Assert.Equal("found", reply.GetProperty("result").GetString());
            Assert.Equal(Node, reply.GetProperty("session").GetProperty("node").GetString());
        }
    }

    [Theory]
    [InlineData("session-name")]
    [InlineData("relay-rewritten-node")]
    [InlineData("machine")]
    public void Altered_or_relay_rewritten_signed_fields_are_dropped(string mutationName)
    {
        var fixture = new SignedFixture();
        var mutation = VectorFiles.Read("fleet-registry-signing.json")["mutations"]!.AsArray()
            .Single(m => m!["name"]!.GetValue<string>() == mutationName)!;
        var record = JsonNode.Parse(fixture.Record().GetRawText())!;
        var path = mutation["path"]!.GetValue<string>();
        var target = path.StartsWith("sessions.", StringComparison.Ordinal) ? record["sessions"]![0]! : record;
        target[path.Split('.').Last()] = mutation["value"]!.DeepClone();
        Assert.Empty(fixture.Read(JsonSerializer.SerializeToElement(record)).SelectMany(m => m.Sessions));
        Assert.True(Assert.Single(fixture.Read(fixture.Record())).Verified);
    }

    [Fact]
    public void Forged_or_noncanonical_signatures_and_a_different_enrolled_key_are_dropped()
    {
        foreach (var sig in VectorFiles.Read("fleet-registry-signing.json")["refusedSignatures"]!.AsArray())
        {
            var fixture = new SignedFixture();
            var record = JsonNode.Parse(fixture.Record().GetRawText())!;
            record["sig"] = sig!.DeepClone();
            Assert.Empty(fixture.Read(JsonSerializer.SerializeToElement(record)).SelectMany(m => m.Sessions));
            Assert.True(Assert.Single(fixture.Read(fixture.Record())).Verified);
        }
        var wrongKey = new SignedFixture(wrongKey: true);
        Assert.Empty(wrongKey.Read(wrongKey.Record()).SelectMany(m => m.Sessions));
    }

    [Fact]
    public void Authenticated_sequence_and_epoch_replays_are_dropped_but_a_later_restart_is_accepted()
    {
        var fixture = new SignedFixture();
        var baseline = fixture.Record();
        Assert.True(Assert.Single(fixture.Read(baseline)).Verified);
        Assert.Empty(fixture.Read(baseline).SelectMany(m => m.Sessions));
        var old = fixture.Record(seq: 1);
        Assert.True(FleetRecordSigning.Verify(old, World.Keys("H").Sign.Public));
        Assert.Empty(fixture.Read(old).SelectMany(m => m.Sessions));
        Assert.True(Assert.Single(fixture.Read(fixture.Record(seq: 3))).Verified);
        Assert.Empty(fixture.Read(fixture.Record(seq: 1, epoch: new string('c', 16))).SelectMany(m => m.Sessions));
        Assert.True(Assert.Single(fixture.Read(fixture.Record(seq: 1, epoch: new string('c', 16), seconds: 1))).Verified);
        Assert.Empty(fixture.Read(fixture.Record(seq: 4)).SelectMany(m => m.Sessions));
    }

    [Theory]
    [InlineData(-61, false)]
    [InlineData(-60, true)]
    [InlineData(60, true)]
    [InlineData(61, false)]
    public void Signed_liveness_is_bounded_by_the_readers_clock_in_both_directions(int seconds, bool live)
    {
        var fixture = new SignedFixture();
        var sessions = fixture.Read(fixture.Record(seconds: seconds)).SelectMany(m => m.Sessions).ToList();
        Assert.Equal(live ? 1 : 0, sessions.Count);
    }

    [Fact]
    public void Trusted_roster_input_does_not_make_an_unsigned_record_verified()
    {
        var fixture = new SignedFixture();
        var machines = fixture.Read(Frame(fixture.Machine).GetProperty("record"));
        Assert.False(Assert.Single(machines).Verified);
        Assert.DoesNotContain("node", Find(machines, fixture.Machine).GetRawText());
    }

    [Fact]
    public void A_relay_cannot_move_a_signed_record_to_another_machine()
    {
        var fixture = new SignedFixture();
        Assert.Empty(fixture.Read(fixture.Record(), "another-machine").SelectMany(m => m.Sessions));
    }

    [Fact]
    public void A_good_signature_does_not_make_an_unknown_record_member_valid()
    {
        var fixture = new SignedFixture();
        var record = JsonNode.Parse(fixture.Record().GetRawText())!;
        record["prompt"] = "not registry data";
        var signed = FleetRecordSigning.Sign(JsonSerializer.SerializeToElement(record), World.Keys("H").Sign.Sign);
        Assert.True(FleetRecordSigning.Verify(signed, World.Keys("H").Sign.Public));
        Assert.Empty(fixture.Read(signed).SelectMany(m => m.Sessions));
    }

    sealed class SignedFixture
    {
        public string Machine { get; } = World.Keys("H").Dh.Device;
        public FakeTimeProvider Clock { get; } = new(At);
        readonly RelayFleetRoster reader;

        public SignedFixture(bool wrongKey = false)
        {
            var world = new World();
            world.Genesis();
            world.AddHostV2("H", "G", "H", "G", hostSignOf: wrongKey ? "wrong" : null);
            var roster = new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => world.Now) { AcceptHostSign = true };
            Assert.All(world.Feed(roster, "G", "H"), offered => Assert.Equal(Verdict.Accepted, offered.Verdict));
            reader = new RelayFleetRoster(() => roster, Clock);
        }

        public JsonElement Record(long seq = 2, string? epoch = null, int seconds = 0)
        {
            var record = JsonNode.Parse(Frame(Machine).GetProperty("record").GetRawText())!;
            record["seq"] = seq;
            record["epoch"] = epoch ?? new string('b', 16);
            record["at"] = At.AddSeconds(seconds).ToString("O");
            return FleetRecordSigning.Sign(JsonSerializer.SerializeToElement(record), World.Keys("H").Sign.Sign);
        }

        public IReadOnlyList<FleetMachine> Read(JsonElement record, string? machine = null) =>
            reader.Read([JsonSerializer.SerializeToElement(new { machine = machine ?? Machine, online = true, record })]);
    }

    static JsonElement Frame(string machine = "fixture") => JsonSerializer.SerializeToElement(new
    {
        machine, online = true,
        record = new
        {
            v = 1, machine, epoch = new string('b', 16), seq = 1, at = At,
            truncated = false,
            sessions = new[] { new { id = new string('a', 16), node = Node, name = "SG Fixture", state = "idle", since = At } },
        },
    });
}
