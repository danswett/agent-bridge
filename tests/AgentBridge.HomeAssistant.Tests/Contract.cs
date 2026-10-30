using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;
using Xunit;

// The contract as agent-ha-bridge's own code published it (Fixtures/ha-contract.json, recorded by
// tools/ha-contract/Export-HaContract.ps1), and the comparison every contract test uses.
internal static class Contract
{
    static readonly Lazy<JsonObject> Loaded = new(() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ha-contract.json")))!);

    public static JsonObject Root => Loaded.Value;

    public static JsonNode At(string path)
    {
        JsonNode node = Root;
        foreach (var part in path.Split('.'))
        {
            node = int.TryParse(part, out var i) ? node.AsArray()[i]! : node.AsObject()[part]!;
        }
        return node;
    }

    public static string Text(string path) => At(path).GetValue<string>();

    public static IReadOnlyList<string> Strings(string path) => [.. At(path).AsArray().Select(n => n!.GetValue<string>())];

    // A recorded publication, as the connector's would be compared.
    public static IReadOnlyList<Published> Publications(string name) =>
        [.. At("publications." + name).AsArray().Select(n => Published.FromFixture(n!.AsObject()))];

    // Payloads compare as JSON (key order is not part of the contract; Home Assistant parses
    // them), numbers by value, and every "updated" stamp as the same placeholder.
    public static void Equal(Published expected, Published actual)
    {
        Assert.Equal(expected.Topic, actual.Topic);
        Assert.Equal(expected.Retain, actual.Retain);
        if (expected.Json is null || actual.Json is null)
        {
            Assert.Equal(expected.Payload, actual.Payload);
            return;
        }
        AssertJson(expected.Json, actual.Json, actual.Topic);
    }

    public static void Equal(IReadOnlyList<Published> expected, IReadOnlyList<Published> actual)
    {
        Assert.Equal(expected.Select(p => p.Topic), actual.Select(p => p.Topic));
        for (var i = 0; i < expected.Count; i++) { Equal(expected[i], actual[i]); }
    }

    public static void AssertJson(JsonNode? expected, JsonNode? actual, string where = "")
    {
        var e = Normalise(expected);
        var a = Normalise(actual);
        Assert.True(JsonEqual(e, a), $"{where}\nexpected {e?.ToJsonString()}\nactual   {a?.ToJsonString()}");
    }

    static JsonNode? Normalise(JsonNode? node)
    {
        // Through text, so a node built in code compares like one parsed from a payload.
        var copy = node is null ? null : JsonNode.Parse(node.ToJsonString());
        if (copy is JsonObject o && o.ContainsKey("updated")) { o["updated"] = "<now>"; }
        if (copy is JsonObject q && q.ContainsKey("asked_at")) { q["asked_at"] = "<now>"; }
        return copy;
    }

    static bool JsonEqual(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) { return a is null && b is null; }
        switch (a)
        {
            case JsonObject oa when b is JsonObject ob:
                return oa.Count == ob.Count && oa.All(p => ob.TryGetPropertyValue(p.Key, out var v) && JsonEqual(p.Value, v));
            case JsonArray aa when b is JsonArray ab:
                return aa.Count == ab.Count && aa.Zip(ab).All(p => JsonEqual(p.First, p.Second));
            case JsonValue va when b is JsonValue vb:
                var ka = va.GetValueKind();
                var kb = vb.GetValueKind();
                if (ka == JsonValueKind.Number && kb == JsonValueKind.Number)
                {
                    return decimal.Parse(va.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture)
                        == decimal.Parse(vb.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);
                }
                return ka == kb && va.ToJsonString() == vb.ToJsonString();
            default:
                return false;
        }
    }
}

// One mqtt.publish as the tests see it: the payload as sent, and parsed when it is JSON.
internal sealed record Published(string Topic, string Payload, bool Retain)
{
    public JsonNode? Json => Payload.StartsWith('{') || Payload.StartsWith('[') ? JsonNode.Parse(Payload) : null;

    public static Published FromFixture(JsonObject o)
    {
        var payload = o["payload"];
        var text = o["json"]!.GetValue<bool>() ? payload!.ToJsonString() : payload?.GetValue<string>() ?? "";
        return new Published(o["topic"]!.GetValue<string>(), text, o["retain"]!.GetValue<bool>());
    }

    public static Published Of(HaMessage m) => new(m.Topic, m.Payload, m.Retain);
}
