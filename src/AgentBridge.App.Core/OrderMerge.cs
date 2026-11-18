using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBridge.App.Core;

// Pure JSON-record counterpart of pwa/js/order-merge.js; no clock, storage or UI.
public static class OrderMerge
{
    const double MaxSafeInteger = 9007199254740991;
    static JsonNode? Field(JsonNode? node, string key) => (node as JsonObject)?[key];
    static bool IsTrue(JsonNode? node) => node?.GetValueKind() == JsonValueKind.True;
    static string? Text(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
    static double Number(JsonNode node) => double.Parse(node.ToJsonString(), CultureInfo.InvariantCulture);
    static bool SafeInteger(JsonNode? node) => node?.GetValueKind() == JsonValueKind.Number
        && Number(node) is var n && n >= 0 && n <= MaxSafeInteger && Math.Truncate(n) == n;

    static IEnumerable<KeyValuePair<string, JsonNode?>> Entries(JsonNode? node) => node switch
    {
        JsonObject map => map,
        JsonArray array => array.Select((value, index) => KeyValuePair.Create(index.ToString(CultureInfo.InvariantCulture), value)),
        _ => [],
    };

    static JsonObject? CleanRegister(JsonNode? node)
    {
        var tag = Text(Field(node, "deviceTag"));
        var counter = Field(node, "counter");
        if (tag is null || tag.Length % 2 != 0 || !tag.All(char.IsAsciiHexDigit) || !SafeInteger(counter)) return null;
        var result = new JsonObject { ["counter"] = Number(counter!), ["deviceTag"] = tag.ToLowerInvariant() };
        if (IsTrue(Field(node, "tombstone"))) result["tombstone"] = true;
        else if (node is JsonObject map && map.ContainsKey("value")) result["value"] = map["value"]?.DeepClone();
        return result;
    }

    public static int CompareRegisters(JsonObject a, JsonObject b)
    {
        var counter = Number(a["counter"]!).CompareTo(Number(b["counter"]!));
        if (counter != 0) return counter;
        var tag = string.CompareOrdinal(Text(a["deviceTag"]), Text(b["deviceTag"]));
        if (tag != 0) return tag;
        var tombstone = IsTrue(a["tombstone"]).CompareTo(IsTrue(b["tombstone"]));
        return tombstone != 0 ? tombstone : string.CompareOrdinal(JsonText(a["value"]), JsonText(b["value"]));
    }

    public static JsonObject? MergeRegisters(JsonNode? a, JsonNode? b)
    {
        var x = CleanRegister(a);
        var y = CleanRegister(b);
        if (x is null || y is null) return x ?? y;
        return CompareRegisters(x, y) >= 0 ? x : y;
    }

    static JsonObject MergeMaps(JsonNode? a, JsonNode? b)
    {
        var result = new JsonObject();
        foreach (var (key, value) in Entries(a).Concat(Entries(b)))
            if (MergeRegisters(result[key], value) is { } register) result[key] = register;
        return result;
    }

    public static JsonObject EmptyOrder() => new() { ["rows"] = new JsonObject(), ["membership"] = new JsonObject(), ["groups"] = new JsonObject() };

    public static JsonObject MergeOrder(JsonNode? a, JsonNode? b) => new()
    {
        ["rows"] = MergeMaps(Field(a, "rows"), Field(b, "rows")),
        ["membership"] = MergeMaps(Field(a, "membership"), Field(b, "membership")),
        ["groups"] = MergeMaps(Field(a, "groups"), Field(b, "groups")),
    };

    public static double NextCounter(JsonNode? order)
    {
        double max = 0;
        foreach (var name in new[] { "rows", "membership", "groups" })
            foreach (var (_, register) in Entries(Field(order, name)))
                if (Field(register, "counter") is { } counter && counter.GetValueKind() == JsonValueKind.Number)
                    max = Math.Max(max, Number(counter));
        return max + 1;
    }

    public static string? GroupOf(JsonNode? order, string rowKey)
    {
        var id = Text(Field(Field(Field(order, "membership"), rowKey), "value"));
        return id is not null && Field(Field(order, "groups"), id) is { } group
            && !IsTrue(Field(group, "tombstone")) ? id : null;
    }

    public static JsonArray OrderedRows(JsonNode? order)
    {
        var rows = Entries(Field(order, "rows")).Where(pair => !IsTrue(Field(pair.Value, "tombstone")))
            .Select(pair => new JsonObject
            {
                ["row"] = pair.Key,
                ["key"] = JsString(Field(Field(pair.Value, "value"), "key")),
                ["pinned"] = IsTrue(Field(Field(pair.Value, "value"), "pinned")),
                ["deviceTag"] = Text(Field(pair.Value, "deviceTag")),
                ["group"] = GroupOf(order, pair.Key),
            })
            .OrderBy(row => Text(row["key"]), StringComparer.Ordinal)
            .ThenBy(row => Text(row["deviceTag"]), StringComparer.Ordinal)
            .ThenBy(row => Text(row["row"]), StringComparer.Ordinal);
        return new JsonArray(rows.Cast<JsonNode?>().ToArray());
    }

    public static JsonObject MergeMarkers(JsonNode? a, JsonNode? b)
    {
        var result = new JsonObject();
        foreach (var (key, value) in Entries(a).Concat(Entries(b)))
            if (SafeInteger(value) && (result[key] is null || Number(value!) > Number(result[key]!)))
                result[key] = Number(value!);
        return result;
    }

    // JSON.stringify uses literal Unicode, JS number spelling and integer property keys first.
    static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            result.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when c < 32 || (char.IsSurrogate(c) && !(char.IsHighSurrogate(c) && i + 1 < value.Length
                    && char.IsLowSurrogate(value[i + 1])) && !(char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(value[i - 1])))
                    => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                _ => c.ToString(),
            });
        }
        return result.Append('"').ToString();
    }

    static string NumberText(JsonNode node)
    {
        var n = Number(node);
        if (n == 0) return "0";
        var text = n.ToString("R", CultureInfo.InvariantCulture).ToLowerInvariant();
        var parts = text.Split('e');
        if (parts.Length == 1) return text;
        var exponent = int.Parse(parts[1], CultureInfo.InvariantCulture);
        if (Math.Abs(n) < 1e-6 || Math.Abs(n) >= 1e21)
            return parts[0] + "e" + (exponent >= 0 ? "+" : "") + exponent.ToString(CultureInfo.InvariantCulture);
        var sign = n < 0 ? "-" : "";
        var digits = parts[0].TrimStart('-').Replace(".", "");
        var point = 1 + exponent;
        return sign + (point <= 0 ? "0." + new string('0', -point) + digits
            : point >= digits.Length ? digits + new string('0', point - digits.Length) : digits.Insert(point, "."));
    }

    static uint PropertyIndex(string key) => uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
        && n < uint.MaxValue && key == n.ToString(CultureInfo.InvariantCulture) ? n : uint.MaxValue;

    static string JsonText(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject map => "{" + string.Join(",", map.OrderBy(pair => PropertyIndex(pair.Key))
            .Select(pair => Quote(pair.Key) + ":" + JsonText(pair.Value))) + "}",
        JsonArray array => "[" + string.Join(",", array.Select(JsonText)) + "]",
        _ when node.GetValueKind() == JsonValueKind.String => Quote(node.GetValue<string>()),
        _ when node.GetValueKind() == JsonValueKind.Number => NumberText(node),
        _ => node.ToJsonString(),
    };

    static string JsString(JsonNode? node) => node switch
    {
        null => "",
        JsonObject => "[object Object]",
        JsonArray array => string.Join(",", array.Select(JsString)),
        _ when node.GetValueKind() == JsonValueKind.String => node.GetValue<string>(),
        _ => JsonText(node),
    };
}
