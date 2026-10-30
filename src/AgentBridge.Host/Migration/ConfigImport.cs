using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBridge.Host.Migration;

// agent-ha-bridge's settings carried into the profile's config.json (docs/host-config.md), each
// key keeping its meaning. Nothing is removed from agent-ha-bridge's file, and no secret is ever
// copied: a key with no equivalent here is named in the report and stays where it is.
//
//   newSession.enabled                          launch.enabled
//   newSession.workspaces (label, path, isolate) launch.workspaces, added to any already approved
//   newSession.defaultWorkspace                 launch.defaultWorkspace
//   newSession.launcher                         launch.defaultAgent
//   newSession.profiles, defaultProfile         launch.profiles, launch.defaultProfile
//   newSession.resumeCount                      launch.resumeCount (at most 50 here)
//   newSession.allowAllTools                    launch.permissions (allowAll or ask)
//   newSession.model (every agent's, or per agent)  launch.agents.<agent>.model
//   newSession.effort/context.<agent>           launch.agents.<agent>.effort/context
//   newSession.models/efforts/contexts.<agent>  launch.agents.<agent>.models/efforts/contexts
//   newSession.extraArgs (every launcher's)     launch.agents.<each agent>.extraArgs
//   newSession.<agent>Path                      launch.agents.<agent>.path
//   newSession.worktreeRoot/Limit/IdleHours     launch.worktrees.root/limit/idleHours
//   homeAssistant.baseUrl                       homeAssistant.baseUrl (the connector, still off)
//   dashboard.urlPath                           homeAssistant.dashboardUrlPath
//   detailedActivity                            homeAssistant.detailedActivity
//   homeAssistant.agentUserIds, agentTokenEnvVar homeAssistant.agentUserIds, agentTokenEnvVar
//
// A setting this profile already has is kept, and the report says so: the person set it here.
public sealed record ConfigImport(string? Text, IReadOnlyList<string> Imported, IReadOnlyList<string> Kept, IReadOnlyList<string> NotCarried, IReadOnlyList<string> Problems)
{
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly string[] Agents = ["copilot", "claude", "codex"];
    static readonly string[] AllLaunchers = ["agency", "copilot", "claude", "codex"];
    static readonly string[] Secrets = ["homeAssistant.token", "homeAssistant.agentToken", "updates.token", "newSession.transferSecret"];

    static readonly HashSet<string> MappedNewSession =
    [
        "enabled", "workspaces", "defaultWorkspace", "launcher", "profiles", "defaultProfile", "resumeCount", "allowAllTools",
        "model", "effort", "context", "models", "efforts", "contexts", "extraArgs",
        "copilotPath", "agencyPath", "claudePath", "codexPath", "worktreeRoot", "worktreeLimit", "worktreeIdleHours",
    ];

    public static ConfigImport Build(JsonObject ahb, string? hostText)
    {
        var imported = new List<string>();
        var kept = new List<string>();
        var notCarried = new List<string>();
        var problems = new List<string>();
        JsonObject host;
        try
        {
            host = hostText is null ? new JsonObject { ["version"] = HostSettings.Version }
                : JsonNode.Parse(hostText, documentOptions: HostSettings.Options) as JsonObject ?? throw new JsonException("not a JSON object");
        }
        catch (JsonException e)
        {
            return new ConfigImport(null, [], [], [], [$"this profile's config.json is not valid ({e.Message}); fix it before migrating"]);
        }
        var before = host.ToJsonString();

        JsonObject Section(JsonObject parent, string name)
        {
            if (parent[name] is JsonObject o) { return o; }
            if (parent[name] is not null) { throw new InvalidDataException($"{name} in this profile's config.json is not an object"); }
            var created = new JsonObject();
            parent[name] = created;
            return created;
        }
        void Put(JsonObject target, string key, JsonNode value, string from, string to)
        {
            if (target[key] is { } existing)
            {
                if (!JsonNode.DeepEquals(existing, value)) { kept.Add($"{to}: this profile already sets it, so agent-ha-bridge's {from} was not imported"); }
                return;
            }
            target[key] = value;
            imported.Add($"{from} -> {to}");
        }

        try
        {
            var ns = ahb["newSession"] as JsonObject;
            var launch = Section(host, "launch");
            if (ns is not null)
            {
                if (Bool(ns["enabled"]) is { } enabled) { Put(launch, "enabled", enabled, "newSession.enabled", "launch.enabled"); }
                Workspaces(ns["workspaces"], launch, imported, kept);
                foreach (var (from, to) in new[] { ("defaultWorkspace", "defaultWorkspace"), ("launcher", "defaultAgent"), ("defaultProfile", "defaultProfile") })
                {
                    if (Str(ns[from]) is { } value) { Put(launch, to, value, $"newSession.{from}", $"launch.{to}"); }
                }
                if (Strings(ns["profiles"]) is { Count: > 0 } profiles) { Put(launch, "profiles", Array(profiles), "newSession.profiles", "launch.profiles"); }
                if (Number(ns["resumeCount"]) is { } resume)
                {
                    var clamped = Math.Clamp((int)resume, 0, AgentBridge.Protocol.LaunchProtocol.MaxResumable);
                    if (clamped != (int)resume) { kept.Add($"launch.resumeCount: agent-ha-bridge offered {resume}; this host offers at most {clamped}"); }
                    Put(launch, "resumeCount", clamped, "newSession.resumeCount", "launch.resumeCount");
                }
                if (Bool(ns["allowAllTools"]) is { } allowAll) { Put(launch, "permissions", allowAll ? "allowAll" : "ask", "newSession.allowAllTools", "launch.permissions"); }

                var worktrees = new JsonObject();
                if (Str(ns["worktreeRoot"]) is { } root) { worktrees["root"] = root; }
                if (Number(ns["worktreeLimit"]) is { } limit) { worktrees["limit"] = (int)limit; }
                if (Number(ns["worktreeIdleHours"]) is { } idle) { worktrees["idleHours"] = idle; }
                if (worktrees.Count > 0)
                {
                    var target = Section(launch, "worktrees");
                    foreach (var (key, value) in worktrees.ToList()) { Put(target, key, value!.DeepClone(), $"newSession.worktree{char.ToUpperInvariant(key[0])}{key[1..]}", $"launch.worktrees.{key}"); }
                }

                JsonObject Agent(string id) => Section(Section(launch, "agents"), id);
                // newSession.model: when it is text, every agent's model (Get-BridgeConfiguredTuning
                // falls back to it for whichever launcher has none of its own; Agency's is
                // Copilot's); one per agent when it is an object.
                if (Str(ns["model"]) is { } scalarModel)
                {
                    foreach (var id in Agents) { Put(Agent(id), "model", scalarModel, "newSession.model", $"launch.agents.{id}.model"); }
                }
                foreach (var axis in new[] { "model", "effort", "context" })
                {
                    if (ns[axis] is JsonObject perAgent)
                    {
                        foreach (var id in Agents)
                        {
                            if (Str(perAgent[id]) is { } value) { Put(Agent(id), axis, value, $"newSession.{axis}.{id}", $"launch.agents.{id}.{axis}"); }
                        }
                    }
                    else if (axis != "model" && ns[axis] is not null) { notCarried.Add($"newSession.{axis} (agent-ha-bridge reads it per agent, as newSession.{axis}.<agent>)"); }
                    if (ns[axis + "s"] is JsonObject lists)
                    {
                        foreach (var id in Agents)
                        {
                            if (Strings(lists[id]) is { Count: > 0 } values) { Put(Agent(id), axis + "s", Array(values), $"newSession.{axis}s.{id}", $"launch.agents.{id}.{axis}s"); }
                        }
                    }
                }
                if (Strings(ns["extraArgs"]) is { Count: > 0 } extra)
                {
                    // agent-ha-bridge added them to every launcher's command line.
                    foreach (var id in AllLaunchers) { Put(Agent(id), "extraArgs", Array(extra), "newSession.extraArgs", $"launch.agents.{id}.extraArgs"); }
                }
                foreach (var id in AllLaunchers)
                {
                    if (Str(ns[id + "Path"]) is { } path) { Put(Agent(id), "path", path, $"newSession.{id}Path", $"launch.agents.{id}.path"); }
                }
                foreach (var (key, _) in ns.Where(p => !MappedNewSession.Contains(p.Key)))
                {
                    notCarried.Add(Secrets.Contains($"newSession.{key}") ? $"newSession.{key} (a secret: never copied)" : $"newSession.{key}");
                }
                // An empty agents section adds nothing to read.
                if (launch["agents"] is JsonObject agents)
                {
                    foreach (var (id, node) in agents.ToList()) { if (node is JsonObject { Count: 0 }) { agents.Remove(id); } }
                    if (agents.Count == 0) { launch.Remove("agents"); }
                }
            }
            if (launch.Count == 0) { host.Remove("launch"); }

            // The Home Assistant connector (docs/ha-connector.md) is pointed at the same Home
            // Assistant, and left off: it takes its own token, given at the machine.
            var ha = ahb["homeAssistant"] as JsonObject;
            var target2 = new JsonObject();
            if (ha is not null && Str(ha["baseUrl"]) is { } baseUrl) { target2["baseUrl"] = baseUrl; }
            if (ahb["dashboard"] is JsonObject dash && Str(dash["urlPath"]) is { } urlPath) { target2["dashboardUrlPath"] = urlPath; }
            if (Bool(ahb["detailedActivity"]) is { } detailed) { target2["detailedActivity"] = detailed; }
            if (ha is not null && Strings(ha["agentUserIds"]) is { Count: > 0 } ids) { target2["agentUserIds"] = Array(ids); }
            if (ha is not null && Str(ha["agentTokenEnvVar"]) is { } envVar) { target2["agentTokenEnvVar"] = envVar; }
            if (target2.Count > 0)
            {
                var section = Section(host, "homeAssistant");
                var names = new Dictionary<string, string>
                {
                    ["baseUrl"] = "homeAssistant.baseUrl", ["dashboardUrlPath"] = "dashboard.urlPath", ["detailedActivity"] = "detailedActivity",
                    ["agentUserIds"] = "homeAssistant.agentUserIds", ["agentTokenEnvVar"] = "homeAssistant.agentTokenEnvVar",
                };
                foreach (var (key, value) in target2.ToList()) { Put(section, key, value!.DeepClone(), names[key], $"homeAssistant.{key}"); }
            }
            if (ha is not null)
            {
                foreach (var (key, _) in ha.Where(p => p.Key is not ("baseUrl" or "agentUserIds" or "agentTokenEnvVar")))
                {
                    notCarried.Add(Secrets.Contains($"homeAssistant.{key}") ? $"homeAssistant.{key} (a secret: never copied; give the connector its own with agent-bridge-host ha token set)" : $"homeAssistant.{key}");
                }
            }
            if (ahb["dashboard"] is JsonObject dashboard)
            {
                foreach (var (key, _) in dashboard.Where(p => p.Key != "urlPath"))
                {
                    notCarried.Add(key == "publication" ? "dashboard.publication (this host never publishes the dashboard, G7)" : $"dashboard.{key}");
                }
            }
            foreach (var (key, value) in ahb.Where(p => p.Key is not ("newSession" or "homeAssistant" or "dashboard" or "detailedActivity")))
            {
                notCarried.Add(value is JsonObject ? $"{key}.*" : key);
            }
        }
        catch (InvalidDataException e)
        {
            return new ConfigImport(null, [], [], [], [e.Message + "; fix it before migrating"]);
        }

        // Read back as serve would, so a migration never leaves the host unable to read its file.
        host["version"] ??= HostSettings.Version;
        var text = host.ToJsonString(Pretty) + Environment.NewLine;
        problems.AddRange(Check(text));
        var changed = hostText is null ? imported.Count > 0 : host.ToJsonString() != before;
        return new ConfigImport(changed ? text : null, imported, kept, notCarried, problems);
    }

    static void Workspaces(JsonNode? source, JsonObject launch, List<string> imported, List<string> kept)
    {
        if (source is not JsonArray list || list.Count == 0) { return; }
        if (launch["workspaces"] is not null and not JsonArray) { kept.Add("launch.workspaces: this profile's is not a list, so agent-ha-bridge's workspaces were not imported"); return; }
        var target = launch["workspaces"] as JsonArray ?? [];
        static (string? Path, string? Label) Read(JsonNode? n) => n switch
        {
            JsonValue v when v.TryGetValue<string>(out var p) => (p, null),
            JsonObject o => (Str(o["path"]), Str(o["label"])),
            _ => (null, null),
        };
        // The folder and label as LaunchConfig will read them (~, . and .. resolved), so an entry
        // naming a folder this profile already approves, however written, is never added beside
        // it with other settings.
        static string Key(string p) => AgentBridge.Host.Delivery.Launch.Folders.Expand(p) ?? System.IO.Path.TrimEndingDirectorySeparator(p.Trim());
        static string LabelOf(string? label, string path) =>
            string.IsNullOrWhiteSpace(label) ? AgentBridge.Host.Delivery.Launch.Folders.DefaultLabel(Key(path)) : label.Trim();
        var pathCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // Only the profile's own labels are kept from agent-ha-bridge's; two of agent-ha-bridge's
        // sharing one are both imported, and the host tells them apart by folder, as
        // agent-ha-bridge's workspace selector did.
        var own = target.Select(Read).ToList();
        var added = 0;
        foreach (var entry in list)
        {
            var (path, label) = Read(entry);
            if (path is null) { kept.Add("a workspace entry in newSession.workspaces has no path and was not imported"); continue; }
            if (target.Select(Read).Any(e => e.Path is { } p && string.Equals(Key(p), Key(path), pathCase))) { continue; }
            var name = LabelOf(label, path);
            if (own.Any(e => e.Path is { } p && string.Equals(LabelOf(e.Label, p), name, StringComparison.OrdinalIgnoreCase)))
            {
                kept.Add($"launch.workspaces: the label {name} is already this profile's, for another folder, so agent-ha-bridge's {path} was not imported");
                continue;
            }
            JsonNode copy = entry switch
            {
                JsonObject o => new JsonObject(o.Where(p => p.Key is "path" or "label" or "isolate").Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))),
                _ => path,
            };
            target.Add(copy);
            added++;
        }
        if (added > 0)
        {
            launch["workspaces"] = target;
            imported.Add($"newSession.workspaces -> launch.workspaces ({added} approved)");
        }
    }

    // The problems serve would find in the file, as ConfigCommands checks a change.
    static IEnumerable<string> Check(string text)
    {
        var check = Directory.CreateTempSubdirectory("abmigrate-").FullName;
        try
        {
            File.WriteAllText(System.IO.Path.Combine(check, HostSettings.FileName), text);
            var (settings, problem) = HostSettings.Load(check);
            return (problem is null ? [] : new[] { problem.Replace(check, "config.json", StringComparison.OrdinalIgnoreCase) })
                .Concat(settings.LaunchProblems).Concat(settings.HomeAssistantProblems).ToList();
        }
        finally { Directory.Delete(check, true); }
    }

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    static double? Number(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    static List<string>? Strings(JsonNode? n) => n is JsonArray a ? a.Select(Str).OfType<string>().ToList() : null;

    static JsonArray Array(IEnumerable<string> values) => new(values.Select(v => (JsonNode)v).ToArray());
}
