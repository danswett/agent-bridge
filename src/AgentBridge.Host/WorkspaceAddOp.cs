using System.Globalization;
using System.Text.Json;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

// host.workspace.add (docs/protocol/local-pipe.md): the person in the app approves a folder for launching, with no terminal. The same check as
// `workspace add`, answered as a closed code, every outcome audited. Who may call it is LocalApi's guard. Workspaces are read at start, so it applies after a restart.
internal static class WorkspaceAddOp
{
    public const string InvalidRequest = "invalid-request";
    public const string ConfigUnreadable = "config-unreadable";
    const int MaxPath = 4096;

    // HostControl holds its settings gate across the read, check, write and audit.
    public static object Handle(string folder, JsonElement data, Action<string> log)
    {
        string path = "";
        string? label = null;
        bool valid;
        try { valid = Parse(data, out path, out label); } catch (InvalidOperationException) { valid = false; } // text that is not valid Unicode
        if (!valid) { return Refused(InvalidRequest, log); }
        try
        {
            var added = WorkspaceApproval.Add(folder, path, label, isolate: false, Folders.IsSystemDirectory);
            if (added.Code != WorkspaceApproval.Added) { return Refused(added.Code, log); }
            log($"local pipe: {HostOps.WorkspaceAdd} approved {JsonSerializer.Serialize(added.Label)} at {JsonSerializer.Serialize(added.Path)} by the local app");
            return new { result = "added", label = added.Label, path = added.Path, restartRequired = true };
        }
        catch (JsonException) { return Refused(ConfigUnreadable, log); }
    }

    static object Refused(string code, Action<string> log)
    {
        log($"local pipe: {HostOps.WorkspaceAdd} refused: {code} (local app)");
        return new { result = "refused", code };
    }

    // Controls, and the characters that hide or reorder text (zero-width and direction marks and overrides, line and paragraph separators, private use).
    static bool Hidden(char c) => char.IsControl(c)
        || char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse;

    // Exactly { path, label? }: text, a path of a sane length, and a label with no control characters. Nothing else is read.
    static bool Parse(JsonElement data, out string path, out string? label)
    {
        path = "";
        label = null;
        if (data.ValueKind != JsonValueKind.Object
            || data.EnumerateObject().Any(p => p.Name is not ("path" or "label"))
            || data.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)
            || !data.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String
            || p.GetString() is not { Length: > 0 and <= MaxPath } text) { return false; }
        path = text;
        if (!data.TryGetProperty("label", out var l)) { return true; }
        if (l.ValueKind != JsonValueKind.String || l.GetString() is not { } name || name.Length > LaunchProtocol.MaxLabel || name.Any(Hidden)) { return false; }
        label = name;
        return true;
    }
}
