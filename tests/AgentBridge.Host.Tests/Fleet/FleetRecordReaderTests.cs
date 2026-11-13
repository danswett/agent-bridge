using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Fleet;
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

    static JsonElement Frame() => JsonSerializer.SerializeToElement(new
    {
        machine = "fixture", online = true,
        record = new
        {
            v = 1, machine = "fixture", epoch = new string('b', 16), seq = 1, at = At,
            truncated = false,
            sessions = new[] { new { id = new string('a', 16), node = Node, name = "SG Fixture", state = "idle", since = At } },
        },
    });
}
