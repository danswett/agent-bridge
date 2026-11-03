using System.Text.Json;

namespace AgentBridge.HomeAssistant;

public sealed record HaPublicationSettings
{
    public bool Enabled { get; init; }
    public string Authority { get; init; } = "";
    public string Participant { get; init; } = "";
    public string Writer { get; init; } = "";
    public int Generation { get; init; }
    public static readonly HaPublicationSettings Default = new();

    internal static HaPublicationSettings Read(JsonElement section, List<string> problems)
    {
        if (!section.TryGetProperty("publication", out var value)) { return Default; }
        var known = new HashSet<string>(["enabled", "authority", "participant", "writer", "generation"], StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => !known.Remove(p.Name)))
        {
            problems.Add("homeAssistant.publication is not an unambiguous publication configuration");
            return Default;
        }
        var enabled = false;
        if (value.TryGetProperty("enabled", out var enable))
        {
            if (enable.ValueKind is JsonValueKind.True or JsonValueKind.False) { enabled = enable.GetBoolean(); }
            else { problems.Add("homeAssistant.publication.enabled is not true or false"); }
        }
        string Identity(string key)
        {
            if (!value.TryGetProperty(key, out var item))
            {
                if (enabled) { problems.Add($"homeAssistant.publication.{key} is required for explicit publication"); }
                return "";
            }
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: >= 1 and <= 80 } text &&
                char.IsAsciiLetterOrDigit(text[0]) && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-'))
            {
                return text;
            }
            problems.Add($"homeAssistant.publication.{key} is not a protocol-1 identity");
            return "";
        }
        var generation = 0;
        if (value.TryGetProperty("generation", out var number))
        {
            if (number.ValueKind != JsonValueKind.Number || !number.TryGetInt32(out generation) || generation < 1)
            {
                problems.Add("homeAssistant.publication.generation is not a positive protocol-1 generation");
            }
        }
        else if (enabled) { problems.Add("homeAssistant.publication.generation is required for explicit publication"); }
        return new() { Enabled = enabled, Authority = Identity("authority"), Participant = Identity("participant"), Writer = Identity("writer"), Generation = generation };
    }
}
