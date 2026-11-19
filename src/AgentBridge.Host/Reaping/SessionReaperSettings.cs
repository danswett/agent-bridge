using System.Text.Json;

namespace AgentBridge.Host.Reaping;

public sealed record SessionReaperSettings
{
    public static readonly SessionReaperSettings Default = new();
    public bool Live { get; init; }
    public string? MachineId { get; init; }
    public bool? IsVm { get; init; }
    public bool? OwnerLaptop { get; init; }
    public string? ApprovedReceipt { get; init; }
    public string? ApprovedSha256 { get; init; }
    public IReadOnlyList<string> ProtectedIds { get; init; } = [];
    public IReadOnlyList<string> ProtectedNames { get; init; } = [];

    public static SessionReaperSettings Read(JsonElement root)
    {
        if (root.EnumerateObject().Count(p => p.Name == "reaper") > 1) { throw new JsonException("duplicate reaper section"); }
        if (!root.TryGetProperty("reaper", out var section)) { return Default; }
        if (section.ValueKind != JsonValueKind.Object || section.EnumerateObject().GroupBy(p => p.Name).Any(group => group.Count() != 1))
            throw new JsonException("reaper must be an unambiguous object");
        return new()
        {
            Live = Boolean(section, "live") ?? false, MachineId = Text(section, "machineId"),
            IsVm = Boolean(section, "isVm"), OwnerLaptop = Boolean(section, "ownerLaptop"),
            ApprovedReceipt = Text(section, "approvedReceipt"), ApprovedSha256 = Text(section, "approvedSha256"),
            ProtectedIds = Strings(section, "protectedIds"), ProtectedNames = Strings(section, "protectedNames"),
        };
    }

    static bool? Boolean(JsonElement section, string key) => !section.TryGetProperty(key, out var value) ? null
        : value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false,
            _ => throw new JsonException($"reaper.{key} must be true or false") };
    static string? Text(JsonElement section, string key) => !section.TryGetProperty(key, out var value) ? null
        : value.ValueKind == JsonValueKind.String && value.GetString() is { } text
            && !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl) ? text
        : throw new JsonException($"reaper.{key} must be nonempty text");
    static IReadOnlyList<string> Strings(JsonElement section, string key)
    {
        if (!section.TryGetProperty(key, out var value)) { return []; }
        if (value.ValueKind != JsonValueKind.Array) { throw new JsonException($"reaper.{key} must be an array"); }
        return value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String && item.GetString() is { } text
            && !string.IsNullOrWhiteSpace(text) && !text.Any(char.IsControl) ? text
            : throw new JsonException($"reaper.{key} must contain nonempty strings")).Distinct(StringComparer.Ordinal).ToArray();
    }
}
