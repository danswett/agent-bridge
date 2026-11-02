using System.Text.Json;

namespace AgentBridge.Host.Core.DevBox;

// Where this Dev Box lives: its Dev Center, project and own name. Read from the Dev Box agent's
// settings, as agent-ha-bridge's Get-BridgeDevBoxIdentity reads them, rather than configured by
// hand, so it keeps working after a reprovision onto another pool, project or region, and an
// ordinary machine simply is not a Dev Box instead of needing the feature turned off.
public sealed record DevBoxIdentity(Uri Endpoint, string Project, string DevBox)
{
    public const string AgentRoot = @"C:\Program Files\Microsoft Dev Box Agent";
    public const string SettingsFile = "appsettings.Production.json";

    // The Dev Box agent's own settings file, the newest when there are several; null when the
    // machine has no Dev Box agent.
    public static string? FindSettings(string root = AgentRoot)
    {
        try
        {
            if (!Directory.Exists(root)) { return null; }
            return Directory.EnumerateFiles(root, SettingsFile, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // This machine's identity, or null with why when it is not a Dev Box this host can act for.
    // A settings file that cannot be read is indistinguishable from not being on a Dev Box, as in
    // agent-ha-bridge, but is said, so a Dev Box whose agent changed its file does not quietly stop
    // being kept awake.
    public static (DevBoxIdentity? Identity, string? Problem) Read(string? settingsPath)
    {
        if (settingsPath is null || !File.Exists(settingsPath)) { return (null, null); }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!Child(doc.RootElement, "DevBoxAgent", out var agent) || !Child(agent, "metadata", out var meta))
            {
                return (null, $"{settingsPath} has no DevBoxAgent.metadata, so this Dev Box cannot be found in Dev Center");
            }
            var endpoint = Text(meta, "devCenterUrl")?.TrimEnd('/');
            var project = Text(meta, "projectName");
            // devBoxDataplaneId is "<tenant>:<devcenter>:<project>:<userObjectId>:<devBoxName>". The
            // Dev Box's own name appears nowhere else in the file.
            var devBox = Text(meta, "devBoxDataplaneId")?.Split(':')[^1];
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(devBox))
            {
                return (null, $"{settingsPath} does not say which Dev Center, project and Dev Box this is");
            }
            // The user's own Dev Center token goes to this address, so only to Dev Center itself:
            // a settings file pointing anywhere else is not followed.
            if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)
                || !uri.Host.EndsWith(".devcenter.azure.com", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath != "/")
            {
                return (null, $"{settingsPath} names a Dev Center that is not https://<name>.devcenter.azure.com, so no token is sent to it");
            }
            return (new DevBoxIdentity(uri, project.Trim(), devBox.Trim()), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, $"{settingsPath} could not be read ({e.Message})");
        }
    }

    // The URL of something under this Dev Box in Dev Center's developer API, users/me: the only
    // scope that lets a user skip or delay their own Dev Box's actions.
    public Uri Url(string path, string query) =>
        new($"{Endpoint.GetLeftPart(UriPartial.Authority)}/projects/{Uri.EscapeDataString(Project)}/users/me/devboxes/{Uri.EscapeDataString(DevBox)}/{path}?{query}");

    // Names in any case, as PowerShell's ConvertFrom-Json reads them for agent-ha-bridge.
    static bool Property(JsonElement e, string name, out JsonElement value)
    {
        value = default;
        if (e.ValueKind != JsonValueKind.Object) { return false; }
        foreach (var p in e.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; }
        }
        return false;
    }

    static bool Child(JsonElement e, string name, out JsonElement child) =>
        Property(e, name, out child) && child.ValueKind == JsonValueKind.Object;

    static string? Text(JsonElement e, string name) =>
        Property(e, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
