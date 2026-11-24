using System.Text.Json;
using AgentBridge.Local;

namespace AgentBridge.Host;

// host.workspace.list and host.workspace.remove (docs/protocol/local-pipe.md, #2694): the person in the app sees the approved folders with a baseline,
// and removes one exact folder against it. Closed codes, every remove outcome audited. Who may call is LocalApi's guard. Workspaces are read at start,
// so a removal applies after a restart. Nothing is approved, trusted or launched here.
internal static class WorkspaceRemoveOp
{
    const string InvalidRequest = "invalid-request";
    const string ConfigUnreadable = "config-unreadable";
    const int MaxPath = 4096;
    const int MaxBaseline = 128;

    public static object List(string folder, Action<string> log)
    {
        try
        {
            var listing = WorkspaceApproval.List(folder);
            return new { result = "listed", workspaces = listing.Workspaces.Select(w => new { path = w.Path, label = w.Label, isolate = w.Isolate }).ToArray(), baseline = listing.Baseline };
        }
        catch (JsonException) { return new { result = "refused", code = ConfigUnreadable }; }
    }

    public static object Handle(string folder, JsonElement data, Action<string> log)
    {
        string path = "", baseline = "";
        bool valid;
        try { valid = Parse(data, out path, out baseline); } catch (InvalidOperationException) { valid = false; } // text that is not valid Unicode
        if (!valid) { return Refused(InvalidRequest, log); }
        try
        {
            var removed = WorkspaceApproval.Remove(folder, path, baseline);
            if (removed.Code != WorkspaceApproval.Removed) { return Refused(removed.Code, log); }
            log($"local pipe: {HostOps.WorkspaceRemove} removed {JsonSerializer.Serialize(removed.Label)} at {JsonSerializer.Serialize(removed.Path)} by the local app");
            return new { result = "removed", label = removed.Label, path = removed.Path, baseline = WorkspaceApproval.List(folder).Baseline, restartRequired = true };
        }
        catch (JsonException) { return Refused(ConfigUnreadable, log); }
    }

    static object Refused(string code, Action<string> log)
    {
        log($"local pipe: {HostOps.WorkspaceRemove} refused: {code} (local app)");
        return new { result = "refused", code };
    }

    // Exactly { path, baseline }, both non-empty text.
    static bool Parse(JsonElement data, out string path, out string baseline)
    {
        path = baseline = "";
        if (data.ValueKind != JsonValueKind.Object
            || data.EnumerateObject().Any(p => p.Name is not ("path" or "baseline"))
            || data.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1)
            || !data.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String || p.GetString() is not { Length: > 0 and <= MaxPath } text
            || !data.TryGetProperty("baseline", out var b) || b.ValueKind != JsonValueKind.String || b.GetString() is not { Length: > 0 and <= MaxBaseline } hash) { return false; }
        path = text;
        baseline = hash;
        return true;
    }
}
