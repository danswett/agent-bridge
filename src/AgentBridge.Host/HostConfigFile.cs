using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBridge.Host;

// Writing the person's choices into the profile's config.json (HostSettings reads it), and
// the machine's name, which the app sets. Everything else in the file is kept as it is: the
// file is the person's, and other settings live beside these.
public static class HostConfigFile
{
    public const int MaxName = 64;

    // The name this machine is shown by, if one was chosen. Unreadable or absent is none.
    public static string? ReadName(string folder)
    {
        try
        {
            var path = Path.Combine(folder, HostSettings.FileName);
            if (!File.Exists(path)) { return null; }
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? CleanName(n.GetString())
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // Trimmed, control characters gone, at most MaxName bytes; null if nothing is left.
    public static string? CleanName(string? name)
    {
        if (name is null) { return null; }
        var clean = new string([.. name.Where(c => !char.IsControl(c))]).Trim();
        if (clean.Length == 0) { return null; }
        while (Encoding.UTF8.GetByteCount(clean) > MaxName) { clean = clean[..^(char.IsLowSurrogate(clean[^1]) && clean.Length > 1 ? 2 : 1)]; }
        return clean.TrimEnd();
    }

    // Sets permissions.remoteApproval and name (an empty name removes it). A file that is not
    // valid JSON is left alone and the change refused, rather than replaced by one that has
    // lost whatever the person meant it to say.
    public static void Patch(string folder, bool? remoteApproval, string? name)
    {
        var path = Path.Combine(folder, HostSettings.FileName);
        JsonObject root;
        if (File.Exists(path))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                    ?? throw new InvalidOperationException($"{path} is not a JSON object; fix it by hand first");
            }
            catch (JsonException e) { throw new InvalidOperationException($"{path} is not valid JSON ({e.Message}); fix it by hand first"); }
        }
        else { root = new JsonObject { ["version"] = HostSettings.Version }; }
        // A newer schema is not this host's to edit: it reads that file as all off anyway.
        if (root["version"] is JsonValue v && v.TryGetValue<int>(out var version) && version > HostSettings.Version)
        {
            throw new InvalidOperationException($"{path} is version {version}, written by a newer Agent Bridge; update this one first");
        }
        if (remoteApproval is { } approve)
        {
            if (root["permissions"] is not JsonObject permissions)
            {
                if (root["permissions"] is not null) { throw new InvalidOperationException($"permissions in {path} is not an object; fix it by hand first"); }
                root["permissions"] = permissions = [];
            }
            permissions["remoteApproval"] = approve;
        }
        if (name is not null)
        {
            if (CleanName(name) is { } n) { root["name"] = n; }
            else { root.Remove("name"); }
        }
        // Written beside and moved over, so a crash mid-write never leaves half a file.
        var temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, path, overwrite: true);
    }
}
