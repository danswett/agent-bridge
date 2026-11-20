using System.Text.Json.Nodes;
using Xunit;

public class FleetRegistryOrderTests
{
    const long MaxInteger = 9007199254740991;

    [Fact]
    public void Shared_order_vectors_pin_the_spec_only_newest_wins_rule()
    {
        var file = VectorFiles.Read("fleet-registry-order.json");
        var now = file["readerAt"]!.GetValue<long>();
        var cases = file["cases"]!.AsArray();
        Assert.NotEmpty(cases);
        Assert.Equal(cases.Count, cases.Select(c => c!["name"]!.GetValue<string>()).Distinct().Count());
        foreach (var entry in cases)
        {
            var vector = entry!.AsObject();
            var records = vector["records"]!.AsArray().Select(r => r!.AsObject()).ToArray();
            Assert.NotEmpty(records);
            Assert.Contains(vector["expected"]!["result"]!.GetValue<string>(), new[] { "found", "ambiguous", "refused" });
            foreach (var order in new[] { records, records.Reverse().ToArray() })
            {
                var actual = Select(order, now);
                Assert.True(JsonNode.DeepEquals(vector["expected"], actual),
                    $"{vector["name"]}: expected {vector["expected"]}, got {actual}");
            }
        }
    }

    // A reference model, not a reader: verified includes enrolled-key/signature and replay checks.
    static JsonObject Select(JsonObject[] records, long now)
    {
        var live = records.Where(r => r["live"]!.GetValue<bool>()).ToArray();
        if (live.Length == 0 || live.Any(r => r["verified"]?.GetValue<bool>() != true))
            return Result("refused");
        var missing = false;
        foreach (var record in live)
        {
            foreach (var field in new[] { "at", "epoch", "seq" })
            {
                if (record[field] is null) { missing = true; continue; }
                if (!Integer(record[field], out var value)) return Result("refused");
                if (field == "at" && Math.Abs(value - now) > 60000) return Result("refused");
            }
            if (record["previous"] is JsonObject previous)
            {
                if (!Integer(previous["epoch"], out var epoch) || !Integer(previous["seq"], out var seq))
                    return Result("refused");
                if (Integer(record["epoch"], out var currentEpoch) && Integer(record["seq"], out var currentSeq) &&
                    (currentEpoch < epoch || currentEpoch == epoch && currentSeq <= seq))
                    return Result("refused");
            }
        }
        if (missing || live.Select(r => r["machine"]!.GetValue<string>()).Distinct().Count() != 1)
            return Result("ambiguous");
        var ranked = live.OrderByDescending(r => r["epoch"]!.GetValue<double>())
            .ThenByDescending(r => r["seq"]!.GetValue<double>()).ToArray();
        if (ranked.Length > 1 && ranked[0]["epoch"]!.GetValue<double>() == ranked[1]["epoch"]!.GetValue<double>() &&
            ranked[0]["seq"]!.GetValue<double>() == ranked[1]["seq"]!.GetValue<double>())
            return Result("ambiguous");
        return new JsonObject { ["result"] = "found", ["id"] = ranked[0]["id"]!.GetValue<string>() };
    }

    static JsonObject Result(string result) => new() { ["result"] = result };

    static bool Integer(JsonNode? node, out long value)
    {
        value = 0;
        if (node is not JsonValue json || !json.TryGetValue<double>(out var number) ||
            !double.IsFinite(number) || number < 0 || number > MaxInteger || number != Math.Truncate(number))
            return false;
        value = (long)number;
        return true;
    }
}
