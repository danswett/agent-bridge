using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBridge.Learning;

static class JsonData
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static T Read<T>(string text)
    {
        using var document = JsonDocument.Parse(text);
        CheckKeys(document.RootElement);
        return JsonSerializer.Deserialize<T>(text, Options) ?? throw new FormatException("JSON must not be null.");
    }

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));

    static void CheckKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new FormatException($"Duplicate JSON field '{property.Name}'.");
                CheckKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                CheckKeys(item);
    }
}
