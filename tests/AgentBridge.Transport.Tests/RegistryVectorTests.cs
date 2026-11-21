using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Relay;
using Xunit;

namespace AgentBridge.Transport.Tests;

public sealed class RegistryVectorTests
{
    static JsonObject Vectors() => JsonNode.Parse(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "testdata", "fleet-registry-fields.json")))!.AsObject();

    public static IEnumerable<object[]> Cases() =>
        Vectors()["schema"]!.AsArray().Select(c => new object[] { c!["name"]!.GetValue<string>(), c.ToJsonString() });

    [Theory]
    [MemberData(nameof(Cases))]
    public void Shared_roster_fields_agree_with_the_relay(string name, string json)
    {
        var fixture = Vectors();
        var record = fixture["record"]!.DeepClone().AsObject();
        var c = JsonNode.Parse(json)!;
        if (c["record"] is JsonObject changes)
        {
            foreach (var p in changes) { record[p.Key] = p.Value?.DeepClone(); }
        }
        if (c["session"] is JsonObject session)
        {
            foreach (var p in session) { record["sessions"]![0]![p.Key] = p.Value?.DeepClone(); }
        }
        var error = RosterFields.Check(JsonSerializer.SerializeToElement(record),
            fixture["record"]!["machine"]!.GetValue<string>(), out _, out _, out _);
        Assert.True(c["accepted"]!.GetValue<bool>() ? error is null : error == "bad-record", name);
    }

    [Fact]
    public void Shared_lifetime_steps_replace_drop_and_erase_payloads_but_keep_offline_metadata_and_watermarks()
    {
        var fixture = Vectors();
        var original = fixture["record"]!.AsObject();
        var machine = original["machine"]!.GetValue<string>();
        var clock = new VectorClock(original["at"]!.GetValue<DateTimeOffset>());
        var registry = new SessionRegistry(clock);
        var lastSeen = clock.Now;
        foreach (var s in fixture["lifetime"]!.AsArray())
        {
            var step = s!;
            switch (step["op"]!.GetValue<string>())
            {
                case "put":
                    var record = original.DeepClone().AsObject();
                    record["seq"] = step["seq"]!.DeepClone();
                    record["sessions"] = Sessions(step);
                    var error = registry.Put("fixture", machine, JsonSerializer.SerializeToElement(record));
                    Assert.Equal(step["error"]?.GetValue<string>(), error);
                    if (error is null) { lastSeen = clock.Now; }
                    break;
                case "advance": clock.Now += TimeSpan.FromSeconds(step["seconds"]!.GetValue<int>()); break;
                case "drop": registry.Drop("fixture", machine); break;
                case "read":
                    var rows = registry.Read("fixture");
                    Assert.Empty(registry.Read("other"));
                    if (step["absent"]?.GetValue<bool>() == true) { Assert.Empty(rows); break; }
                    var row = Assert.Single(rows);
                    Assert.Equal(machine, row.Machine);
                    Assert.Equal(lastSeen, row.LastSeen);
                    Assert.Equal(step["fresh"]!.GetValue<bool>(), row.Fresh);
                    if (!row.Fresh) { Assert.Equal("null", row.Json); break; }
                    Assert.True(JsonNode.DeepEquals(Sessions(step), JsonNode.Parse(row.Json)!["sessions"]));
                    break;
                default: Assert.Fail("Unknown shared lifetime operation"); break;
            }
        }
        JsonArray Sessions(JsonNode step) => new(step["sessions"]!.AsArray()
            .Select(i => original["sessions"]![i!.GetValue<int>()]!.DeepClone()).ToArray());
    }

    sealed class VectorClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
