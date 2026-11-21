using System.Text.Json.Nodes;
using AgentBridge.App.Core;
using Xunit;

public sealed class OrderMergeTests
{
    [Theory]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.PositiveInfinity, "Infinity")]
    [InlineData(double.NegativeInfinity, "-Infinity")]
    public void Nonfinite_values_use_JSON_null_but_retain_JS_row_key_spelling(double number, string text)
    {
        var a = new JsonObject { ["value"] = number, ["counter"] = 1d, ["deviceTag"] = "01" };
        var b = new JsonObject { ["value"] = null, ["counter"] = 1d, ["deviceTag"] = "01" };
        Assert.Equal(0, OrderMerge.CompareRegisters(a, b));
        a["value"] = new JsonObject { ["key"] = number };
        var order = OrderMerge.EmptyOrder();
        order["rows"]!["r"] = a;
        Assert.Equal(text, OrderMerge.OrderedRows(order)[0]!["key"]!.GetValue<string>());
    }

    public static IEnumerable<object[]> Vectors()
    {
        var vectors = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "order-merge.json")))!.AsObject();
        foreach (var (kind, cases) in vectors)
            foreach (var vector in cases!.AsArray())
                yield return [kind, vector!["name"]!.GetValue<string>(), vector.ToJsonString()];
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Shared_vectors_converge_without_mutating_inputs(string kind, string name, string json)
    {
        var vector = JsonNode.Parse(json)!;
        var a = vector["a"];
        var b = vector["b"];
        var before = vector.ToJsonString();
        var actual = Merge(kind, a, b);
        Assert.True(JsonNode.DeepEquals(vector["expected"], actual), name);
        Assert.True(JsonNode.DeepEquals(actual, Merge(kind, b, a)), name + ": either order");
        Assert.True(JsonNode.DeepEquals(actual, Merge(kind, actual, a)), name + ": stale repeat");
        Assert.True(JsonNode.DeepEquals(actual, Merge(kind, actual, actual)), name + ": idempotent");
        Assert.Equal(before, vector.ToJsonString());
        if (kind == "order")
        {
            var rows = OrderMerge.OrderedRows(actual);
            Assert.Equal(vector["rows"]!.AsArray().Select(r => r!.GetValue<string>()),
                rows.Select(r => r!["row"]!.GetValue<string>()));
            Assert.Equal(vector["next"]!.GetValue<double>(), OrderMerge.NextCounter(actual));
            if (vector["view"] is { } view) Assert.True(JsonNode.DeepEquals(view, rows), name);
            if (vector["groups"] is JsonObject groups)
                foreach (var (row, group) in groups)
                    Assert.Equal(group?.GetValue<string>(), OrderMerge.GroupOf(actual, row));
        }
        if (actual is JsonObject map) map.Clear();
        Assert.Equal(before, vector.ToJsonString());
    }

    static JsonNode? Merge(string kind, JsonNode? a, JsonNode? b) => kind switch
    {
        "order" => OrderMerge.MergeOrder(a, b),
        "register" => OrderMerge.MergeRegisters(a, b),
        "marker" => OrderMerge.MergeMarkers(a, b),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
