using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Delivery.Launch;

namespace AgentBridge.Host;

// The host's config.json from the command line, at the machine (docs/host-config.md). This is
// how a workspace is approved for launching until the app has a screen for it: a person at the
// machine runs it, and nothing another device sends can change this file.
//
//   agent-bridge-host workspace list
//   agent-bridge-host workspace add <folder> [--label <label>] [--isolate]
//   agent-bridge-host workspace remove <label or folder>
//   agent-bridge-host config show
//   agent-bridge-host config set <key> <value>     e.g. launch.defaultAgent copilot
//   agent-bridge-host config unset <key>
//
// A <value> is read as JSON when it is JSON (true, 12, ["a","b"]), and as text otherwise.
// Every change is checked by reading the whole file back as serve would; one that would leave
// it with a problem is not written. The file is replaced in one step, so serve never reads half
// of it. serve reads it when it starts: restart it to apply a change.
public static class ConfigCommands
{
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Settable keys, by dotted path. Anything else is refused, so a typo is never written.
    static readonly string[] Keys =
    [
        "permissions.remoteApproval", "push.enabled", "delivery.readOnly",
        "launch.enabled", "launch.defaultWorkspace", "launch.defaultAgent", "launch.permissions",
        "launch.worktrees.root", "launch.worktrees.limit", "launch.worktrees.idleHours",
        "launch.profiles", "launch.defaultProfile", "launch.resumeCount",
        "homeAssistant.enabled", "homeAssistant.baseUrl", "homeAssistant.dashboardUrlPath", "homeAssistant.detailedActivity",
        "homeAssistant.machineName", "homeAssistant.disposable", "homeAssistant.agentUserIds",
        "usage.publish", "usage.intervalSeconds", "usage.idleIntervalSeconds", "usage.copilotAccounts",
    ];

    static readonly string[] AgentKeys = ["path", "model", "effort", "context", "models", "efforts", "contexts", "extraArgs"];

    // Whether these arguments would change the file, rather than only show it.
    public static bool Writes(IReadOnlyList<string> args) =>
        (args.ElementAtOrDefault(0), args.ElementAtOrDefault(1)) is ("workspace", "add" or "remove") or ("config", "set" or "unset");

    public static int Run(IReadOnlyList<string> args, string folder, TextWriter output, TextWriter error, Func<string, bool>? isSystemDirectory = null)
    {
        var path = Path.Combine(folder, HostSettings.FileName);
        try
        {
            return (args.ElementAtOrDefault(0), args.ElementAtOrDefault(1)) switch
            {
                ("workspace", "list") => List(path, output),
                ("workspace", "add") when args.Count > 2 => Add(path, args[2], Option(args, "--label"), args.Contains("--isolate"), isSystemDirectory ?? Folders.IsSystemDirectory, output, error),
                ("workspace", "remove") when args.Count > 2 => Remove(path, args[2], output, error),
                ("config", "show") => Show(path, output),
                ("config", "set") when args.Count > 3 => Set(path, args[2], args[3], output, error),
                ("config", "unset") when args.Count > 2 => Set(path, args[2], null, output, error),
                _ => Usage(error),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            error.WriteLine($"{path}: {e.Message}");
            return 1;
        }
    }

    static int Usage(TextWriter error)
    {
        error.WriteLine("usage: agent-bridge-host workspace list | workspace add <folder> [--label <label>] [--isolate] | workspace remove <label or folder>");
        error.WriteLine("       agent-bridge-host config show | config set <key> <value> | config unset <key>   [--profile <name>]");
        return 2;
    }

    static string? Option(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++) { if (args[i] == name) { return args[i + 1]; } }
        return null;
    }

    static int List(string path, TextWriter output)
    {
        var (settings, problem) = HostSettings.Load(Path.GetDirectoryName(path)!);
        if (problem is not null) { output.WriteLine(problem); }
        foreach (var p in settings.LaunchProblems) { output.WriteLine(p); }
        if (settings.Launch.Workspaces.Count == 0)
        {
            output.WriteLine("No workspace is approved for launching. Approve one with: agent-bridge-host workspace add <folder>");
            return 0;
        }
        foreach (var w in settings.Launch.Workspaces)
        {
            var state = Folders.Physical(w.Path) is null ? "  (missing: not offered)" : "";
            output.WriteLine($"{w.Label}\t{w.Path}{(w.Isolate ? "\tisolated (a git worktree per session)" : "")}{state}");
        }
        if (!settings.Launch.Enabled) { output.WriteLine("Launching is turned off (launch.enabled)."); }
        return 0;
    }

    static int Show(string path, TextWriter output)
    {
        output.WriteLine(path);
        output.WriteLine(File.Exists(path) ? File.ReadAllText(path) : "(no file: every setting is its default, and no workspace is approved)");
        var (settings, problem) = HostSettings.Load(Path.GetDirectoryName(path)!);
        if (problem is not null) { output.WriteLine(problem); }
        foreach (var p in settings.SectionProblems) { output.WriteLine(p); }
        return 0;
    }

    static int Add(string path, string folder, string? label, bool isolate, Func<string, bool> isSystemDirectory, TextWriter output, TextWriter error)
    {
        string full;
        try { full = Path.GetFullPath(Folders.Expand(folder) ?? folder); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { error.WriteLine($"{folder} is not a folder path"); return 1; }
        if (Folders.Physical(full) is not { } physical) { error.WriteLine($"{full} does not exist"); return 1; }
        if (isSystemDirectory(physical)) { error.WriteLine($"{full} is a system folder (Windows, Program Files, ProgramData, temp or a drive root); no session should be launched there"); return 1; }
        if (isolate && !Git.Run(physical, ["rev-parse", "--show-toplevel"]).Ok) { error.WriteLine($"{full} is not in a git repository, so its sessions cannot have worktrees of their own; add it without --isolate"); return 1; }

        var root = Read(path);
        var launch = Object(root, "launch");
        var workspaces = launch["workspaces"] as JsonArray ?? [];
        launch["workspaces"] = workspaces;
        var (current, _) = HostSettings.Load(Path.GetDirectoryName(path)!);
        if (current.Launch.Workspaces.FirstOrDefault(w => Folders.Physical(w.Path) is { } p && Folders.Same(p, physical)) is { } existing)
        {
            error.WriteLine($"{full} is already approved, as '{existing.Label}'");
            return 1;
        }
        label = string.IsNullOrWhiteSpace(label) ? Folders.DefaultLabel(full) : label.Trim();
        if (current.Launch.Workspaces.Any(w => w.Label == label)) { error.WriteLine($"another workspace is already labelled '{label}'; choose one with --label"); return 1; }
        var entry = new JsonObject { ["path"] = full, ["label"] = label };
        if (isolate) { entry["isolate"] = true; }
        workspaces.Add(entry);
        if (!Save(path, root, error)) { return 1; }
        output.WriteLine($"Approved {full} as '{label}' for launching sessions from your devices{(isolate ? "; each new session starts in a git worktree of its own" : "")}.");
        output.WriteLine("Restart agent-bridge-host serve to apply it.");
        return 0;
    }

    static int Remove(string path, string which, TextWriter output, TextWriter error)
    {
        var root = Read(path);
        if (root["launch"]?["workspaces"] is not JsonArray workspaces) { error.WriteLine("no workspace is approved"); return 1; }
        var full = Folders.Expand(which) ?? (Path.IsPathRooted(which) ? which : null);
        var removed = 0;
        for (var i = workspaces.Count - 1; i >= 0; i--)
        {
            var node = workspaces[i];
            static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
            var entryPath = node is JsonObject o ? Text(o["path"]) : Text(node);
            var entryLabel = node is JsonObject o2 ? Text(o2["label"]) : null;
            var expanded = entryPath is null ? null : Folders.Expand(entryPath);
            var labelled = entryLabel ?? (expanded is null ? null : Folders.DefaultLabel(expanded));
            if (labelled == which || (full is not null && expanded is not null && Folders.Same(expanded, full)))
            {
                workspaces.RemoveAt(i);
                removed++;
            }
        }
        if (removed == 0) { error.WriteLine($"no approved workspace is labelled or found at '{which}'"); return 1; }
        if (!Save(path, root, error)) { return 1; }
        output.WriteLine($"Removed {removed} workspace(s). Restart agent-bridge-host serve to apply it.");
        return 0;
    }

    static int Set(string path, string key, string? value, TextWriter output, TextWriter error)
    {
        var parts = key.Split('.');
        var known = Keys.Contains(key, StringComparer.Ordinal)
            || (parts.Length == 4 && parts[0] == "launch" && parts[1] == "agents" && Delivery.Launch.Launchers.Find(parts[2]) is not null && AgentKeys.Contains(parts[3]));
        if (!known)
        {
            error.WriteLine($"'{key}' is not a setting. Settings: {string.Join(", ", Keys)}, launch.agents.<agency|copilot|claude|codex>.<{string.Join("|", AgentKeys)}>; workspaces are changed with agent-bridge-host workspace, and the Home Assistant token with agent-bridge-host ha token set");
            return 2;
        }
        var root = Read(path);
        var parent = root;
        foreach (var p in parts[..^1]) { parent = Object(parent, p); }
        if (value is null) { parent.Remove(parts[^1]); }
        else
        {
            JsonNode? node;
            try { node = JsonNode.Parse(value); }
            catch (JsonException) { node = JsonValue.Create(value); }
            parent[parts[^1]] = node;
        }
        if (!Save(path, root, error)) { return 1; }
        output.WriteLine($"{(value is null ? "Unset" : "Set")} {key}. Restart agent-bridge-host serve to apply it.");
        return 0;
    }

    static JsonObject Read(string path)
    {
        if (!File.Exists(path)) { return new JsonObject { ["version"] = HostSettings.Version }; }
        var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: HostSettings.Options) as JsonObject
            ?? throw new JsonException("the file is not a JSON object; fix or remove it first");
        return node;
    }

    static JsonObject Object(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject o) { return o; }
        if (parent[name] is not null) { throw new JsonException($"{name} is not an object; fix it first"); }
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    // Checked as serve would read it, then written in one step.
    static bool Save(string path, JsonObject root, TextWriter error)
    {
        root["version"] ??= HostSettings.Version;
        var text = root.ToJsonString(Pretty) + Environment.NewLine;
        var folder = Path.GetDirectoryName(path)!;
        var check = Path.Combine(folder, $".config-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(check);
        try
        {
            File.WriteAllText(Path.Combine(check, HostSettings.FileName), text);
            var (settings, problem) = HostSettings.Load(check);
            var problems = (problem is null ? [] : new[] { problem }).Concat(settings.SectionProblems).ToList();
            if (problems.Count > 0)
            {
                error.WriteLine("Not written: " + string.Join("; ", problems.Select(p => p.Replace(check, folder, StringComparison.OrdinalIgnoreCase))));
                return false;
            }
        }
        finally { Directory.Delete(check, true); }
        var temp = Path.Combine(folder, $".{HostSettings.FileName}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
        return true;
    }
}
