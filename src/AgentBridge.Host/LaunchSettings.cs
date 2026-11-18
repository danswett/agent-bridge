using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Migration;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

internal static class LaunchSettings
{
    public static LaunchFolderSnapshot Handle(string folder, LaunchFolderRequest request, Action<string> log)
    {
        var path = Path.Combine(folder, HostSettings.FileName);
        byte[]? before;
        try { before = File.ReadAllBytes(path); }
        catch (FileNotFoundException) { before = null; }
        catch (DirectoryNotFoundException) { before = null; }
        var text = before is null ? "{}" : Registrations.Decode(before);
        var (settings, problem) = HostSettings.Parse(text, path);
        if (problem is not null || settings.LaunchProblems.Count > 0)
        { throw new InvalidOperationException("Repair the host launch configuration before editing approved folders."); }
        var expected = before is null ? null : MigrationRecord.Hash(before);
        var baseline = expected ?? "absent";
        var current = settings.Launch.Workspaces;
        if (request.Folders is not { } folders) { return new(baseline, [.. current.Select(w => w.Path)]); }
        if (!request.Confirmed) { throw new InvalidOperationException("Explicit confirmation is required to save launch folders."); }
        if (request.Baseline != baseline) { throw new InvalidOperationException("Launch folder draft is stale; reload the current configuration before saving."); }
        if (folders.Count > LaunchProtocol.MaxWorkspaces) { throw new ArgumentException("Too many approved folders."); }
        var root = JsonNode.Parse(text, documentOptions: HostSettings.Options)!.AsObject();
        var launch = root["launch"] as JsonObject ?? new JsonObject();
        var entries = launch["workspaces"] as JsonArray ?? [];
        var next = new JsonArray();
        var paths = new List<string>();
        foreach (var requested in folders)
        {
            if (requested?.Trim() is { } candidate && (candidate.StartsWith(@"\\", StringComparison.Ordinal) || candidate.StartsWith("//", StringComparison.Ordinal)))
            { throw new ArgumentException("UNC paths cannot be approved for launching."); }
            if (string.IsNullOrWhiteSpace(requested) || Folders.Expand(requested) is not { } full)
            { throw new ArgumentException("Each launch folder must be an absolute path."); }
            if (full.StartsWith(@"\\", StringComparison.Ordinal))
            { throw new ArgumentException("UNC and device paths cannot be approved for launching."); }
            if (Registrations.FirstLink(full) is not null)
            { throw new ArgumentException("A launch folder cannot contain a symbolic link or junction."); }
            if (paths.Any(p => Folders.Same(p, full))) { throw new ArgumentException("Duplicate launch folders are not allowed."); }
            paths.Add(full);
            var matches = current.Select((w, index) => (w, index)).Where(p => Folders.Same(p.w.Path, full)).ToArray();
            if (matches.Length > 1) { throw new ArgumentException("The stored folder approval is ambiguous; repair it before saving."); }
            // Retaining an approval is not approving it again: preserve its raw entry even offline.
            if (matches.Length == 1) { next.Add(entries[matches[0].index]!.DeepClone()); continue; }
            if (Folders.Physical(full) is not { } physical) { throw new ArgumentException("A new launch folder must exist and be physically resolvable."); }
            if (Folders.IsSystemDirectory(full) || Folders.IsSystemDirectory(physical))
            { throw new ArgumentException("A system folder cannot be approved for launching."); }
            next.Add(new JsonObject { ["path"] = full });
        }
        launch["workspaces"] = next;
        if (root["launch"] is null) { root["launch"] = launch; }
        var saved = Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        DurableFile.Replace(path, saved, expected);
        log("local app saved approved launch folders; no restart, trust or launch was requested");
        return new(MigrationRecord.Hash(saved), paths);
    }
}
