using System.Text.Json;
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
    public void An_unsigned_record_never_supplies_a_lookup_node_with_or_without_a_machine_filter()
    {
        var frame = JsonSerializer.SerializeToElement(new
        {
            machine = "fixture", online = true,
            record = new
            {
                v = 1, machine = "fixture", epoch = new string('b', 16), seq = 1, at = At,
                truncated = false,
                sessions = new[] { new { id = new string('a', 16), node = Node, name = "SG Fixture", state = "idle", since = At } },
            },
        });
        var machines = RelayFleetRoster.Parse([frame]);
        Assert.False(Assert.Single(machines).Verified);
        foreach (var filter in new string?[] { null, "fixture" })
        {
            var reply = Find(machines, filter);
            Assert.Equal("unverified", reply.GetProperty("result").GetString());
            Assert.DoesNotContain("node", reply.GetRawText());
            Assert.DoesNotContain("SG Fixture", reply.GetRawText());
        }
    }
}
