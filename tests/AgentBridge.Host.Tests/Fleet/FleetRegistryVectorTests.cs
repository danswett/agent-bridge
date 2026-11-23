using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentBridge.Host.Fleet;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Slice 10 (docs/design/fleet-session-registry.md; #1407): the shared reader-behaviour vector, replayed by the host's
// reader. pwa/test/unit/fleet-registry-reader-vectors.test.mjs replays the same file; both must give the same snapshots.
public sealed class FleetRegistryVectorTests
{
    static readonly string Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "../../../../../docs/protocol/vectors/fleet-registry-reader.json"));
    static readonly JsonNode Vectors = JsonNode.Parse(File.ReadAllText(Path))!;

    public static IEnumerable<object[]> Scenarios() => Vectors["scenarios"]!.AsArray().Select(s => new object[] { s!["name"]!.GetValue<string>() });

    // "@-20" in `at` and `lastSeen` is that many seconds from the reader's clock at the step.
    static JsonNode? Materialize(JsonNode? node, DateTimeOffset now)
    {
        switch (node)
        {
            case JsonObject o:
                var copy = new JsonObject();
                foreach (var (k, v) in o)
                {
                    copy[k] = (k is "at" or "lastSeen") && v is JsonValue s && s.TryGetValue<string>(out var text) && Regex.IsMatch(text, "^@-?[0-9]+$")
                        ? now.AddSeconds(int.Parse(text[1..])).ToString("o") : Materialize(v, now);
                }
                return copy;
            case JsonArray a: return new JsonArray(a.Select(n => Materialize(n, now)).ToArray());
            default: return node?.DeepClone();
        }
    }

    static JsonElement El(JsonNode? n) => JsonDocument.Parse(n!.ToJsonString()).RootElement.Clone();

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void The_reader_gives_the_vectors_snapshots(string name)
    {
        var scenario = Vectors["scenarios"]!.AsArray().Single(s => s!["name"]!.GetValue<string>() == name)!;
        var time = new FakeTimeProvider(DateTimeOffset.Parse(Vectors["start"]!.GetValue<string>()));
        var reader = new FleetRegistryReader(time);
        foreach (var step in scenario["steps"]!.AsArray().Select(s => s!.AsObject()))
        {
            if (step.ContainsKey("advanceSeconds")) { time.Advance(TimeSpan.FromSeconds(step["advanceSeconds"]!.GetValue<double>())); }
            else if (step.ContainsKey("get")) { reader.ApplyGet(step["get"]!.AsArray().Select(f => El(Materialize(f, time.GetUtcNow()))).ToList()); }
            else if (step.ContainsKey("changed")) { reader.ApplyChanged(El(Materialize(step["changed"], time.GetUtcNow()))); }
            else
            {
                var got = reader.Snapshot().OrderBy(m => m.Id, StringComparer.Ordinal).Select(m => (m.Id, m.Online, m.Live, m.Verified,
                    Record: m.Record is { } r ? $"{r.GetProperty("epoch").GetString()}/{r.GetProperty("seq").GetInt64()}" : null)).ToList();
                var want = step["expect"]!.AsArray().Select(e => (Id: e!["id"]!.GetValue<string>(), Online: e["online"]!.GetValue<bool>(), Live: e["live"]!.GetValue<bool>(),
                    Verified: e["verified"]!.GetValue<bool>(), Record: e["record"] is JsonObject r ? $"{r["epoch"]!.GetValue<string>()}/{r["seq"]!.GetValue<long>()}" : null)).ToList();
                Assert.Equal(want, got);
            }
        }
    }

    [Fact]
    public void The_vector_covers_every_registry_capability()
    {
        Assert.Equal(["registry-cache-expiry", "registry-freshness", "registry-order-and-replay", "registry-removal-and-purge"],
            Vectors["scenarios"]!.AsArray().Select(s => s!["capability"]!.GetValue<string>()).Distinct().Order(StringComparer.Ordinal).ToArray());
    }
}
