using System.Globalization;
using System.Text.Json;

namespace AgentBridge.Host.Core;

// Tolerant reads from agent JSON. Agents add and reshape fields freely, and a missing or
// differently typed field must cost that one value, never the whole event: the old
// bridge lost nearly half of all assistant messages to one strict property read.
internal static class Json
{
    public static JsonElement? Get(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v : null;

    public static JsonElement? Obj(JsonElement e, string name) => Get(e, name) is { ValueKind: JsonValueKind.Object } v ? v : null;

    public static JsonElement? Arr(JsonElement e, string name) => Get(e, name) is { ValueKind: JsonValueKind.Array } v ? v : null;

    public static string? Str(JsonElement e, string name) => Get(e, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;

    public static int? Int(JsonElement e, string name) => Get(e, name) switch
    {
        { ValueKind: JsonValueKind.Number } v when v.TryGetInt32(out var i) => i,
        { ValueKind: JsonValueKind.String } v when int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) => i,
        _ => null,
    };

    public static bool? Bool(JsonElement e, string name) => Get(e, name) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => null,
    };

    public static DateTimeOffset? Time(JsonElement e, string name) => Get(e, name) is { ValueKind: JsonValueKind.String } v ? ParseTime(v.GetString()) : null;

    public static DateTimeOffset? ParseTime(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t.ToUniversalTime() : null;

    // An element that outlives its document, for Field.Default.
    public static JsonElement Detach(JsonElement e) => e.Clone();
}
