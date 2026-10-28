using System.Text.Json;

namespace AgentBridge.Host;

// What this host may do on this machine, set by the person who runs it: config.json in the
// profile's folder, beside the device key. Read when serve starts; a change takes a restart.
//
//   { "permissions": { "remoteApproval": true } }
//
// permissions.remoteApproval: whether Copilot's permission prompts may be approved or denied
// from another device (docs/host-answers.md). On when the file or the setting is absent. A
// file that cannot be read, or a setting that is not true or false, turns it off: a setting
// the person may have meant as off is never read as on.
public sealed record HostSettings(bool RemoteApproval)
{
    public const string FileName = "config.json";

    public static readonly HostSettings Default = new(RemoteApproval: true);

    public static (HostSettings Settings, string? Problem) Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        string text;
        try
        {
            if (!File.Exists(path)) { return (Default, null); }
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (Default with { RemoteApproval = false }, $"{path} could not be read ({e.Message}); remote approval of permission prompts is off");
        }
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { throw new JsonException("not an object"); }
            if (!root.TryGetProperty("permissions", out var permissions)) { return (Default, null); }
            if (permissions.ValueKind != JsonValueKind.Object) { throw new JsonException("permissions is not an object"); }
            if (!permissions.TryGetProperty("remoteApproval", out var remote)) { return (Default, null); }
            return remote.ValueKind switch
            {
                JsonValueKind.True => (Default, null),
                JsonValueKind.False => (Default with { RemoteApproval = false }, null),
                _ => throw new JsonException("permissions.remoteApproval is not true or false"),
            };
        }
        catch (JsonException e)
        {
            return (Default with { RemoteApproval = false }, $"{path} is not valid ({e.Message}); remote approval of permission prompts is off");
        }
    }
}
