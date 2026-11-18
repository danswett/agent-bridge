using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Delivery.Launch;

namespace AgentBridge.Host;

internal static class FleetWorkerSettings
{
    public const string Op = "host.workspace.fleetWorkers";

    // HostControl holds its settings gate across the read, patch, write and audit.
    public static object Handle(string folder, JsonElement data, Action<string> log)
    {
        if (data.ValueKind != JsonValueKind.Object
            || data.EnumerateObject().Any(p => p.Name is not ("workspace" or "fleetWorkers"))
            || data.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1))
        { throw new InvalidOperationException("refused: only workspace and fleetWorkers may be supplied"); }
        if (!data.TryGetProperty("workspace", out var requested) || requested.ValueKind != JsonValueKind.String
            || Folders.Expand(requested.GetString()!) is not { } workspace)
        { throw new InvalidOperationException("refused: unknown workspace; supply its approved absolute path"); }
        bool? change = null;
        if (data.TryGetProperty("fleetWorkers", out var setting))
        {
            if (setting.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            { throw new InvalidOperationException("refused: fleetWorkers must be true or false"); }
            change = setting.GetBoolean();
        }
        var path = Path.Combine(folder, HostSettings.FileName);
        try
        {
            var text = File.ReadAllText(path);
            var (settings, problem) = HostSettings.Parse(text, path);
            if (problem is not null || settings.LaunchProblems.Count != 0)
            { throw new InvalidOperationException("refused: fix the host configuration before reading or changing fleet workers"); }
            if (!settings.Launch.Workspaces.Any(w => Folders.Same(w.Path, workspace)))
            { throw new InvalidOperationException("refused: unknown workspace"); }
            var root = JsonNode.Parse(text, documentOptions: HostSettings.Options)!.AsObject();
            var entries = root["launch"]!["workspaces"]!.AsArray();
            var matches = entries.Select((entry, index) => (entry, index)).Where(pair =>
            {
                var node = pair.entry is JsonObject obj ? obj["path"] : pair.entry;
                return node is JsonValue value && value.TryGetValue<string>(out var raw)
                    && Folders.Expand(raw) is { } full && Folders.Same(full, workspace);
            }).ToArray();
            if (matches.Length != 1) { throw new InvalidOperationException("refused: the workspace setting is ambiguous"); }
            var (entry, index) = matches[0];
            var current = false;
            if (entry is JsonObject saved && saved.ContainsKey("fleetWorkers"))
            {
                if (saved["fleetWorkers"] is not JsonValue flag || !flag.TryGetValue<bool>(out current))
                { throw new InvalidOperationException("refused: the stored fleetWorkers setting must be true or false"); }
            }
            if (change is { } next && next != current)
            {
                var target = entry as JsonObject;
                if (target is null) { entries[index] = target = new JsonObject { ["path"] = entry!.GetValue<string>() }; }
                target["fleetWorkers"] = next;
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    File.Move(temp, path, overwrite: true);
                }
                finally { File.Delete(temp); }
                log($"local pipe: {Op} changed workspace {index} from {current} to {next} by the local app");
                current = next;
            }
            return new { workspace, value = current, defaultValue = false };
        }
        catch (FileNotFoundException) { throw new InvalidOperationException("refused: unknown workspace; no host configuration exists"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { throw new InvalidOperationException("refused: the fleet worker configuration could not be read or saved", e); }
    }
}
