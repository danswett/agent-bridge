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
                // Get-BridgeWorkspaceChoices reads them through @(...): one path or entry is a list of one.
                var legacyWorkspaces = ns["workspaces"] is JsonArray or null ? ns["workspaces"] : new JsonArray(ns["workspaces"]!.DeepClone());
                Workspaces(legacyWorkspaces, launch, imported, kept);
                if (Str(ns["defaultWorkspace"]) is { } workspace) { Put(launch, "defaultWorkspace", DefaultLabel(launch, legacyWorkspaces, workspace), "newSession.defaultWorkspace", "launch.defaultWorkspace"); }
                // As Get-BridgeLauncherKind reads it: trimmed, any case, and one it does not know
                // (or that is not installed) is auto. Copied as written, "Claude" would fail the
                // profile's config.
                if (Str(ns["launcher"]) is { } written)
                {
                    var launcher = written.Trim().ToLowerInvariant();
                    if (launcher != "auto" && !AgentBridge.Protocol.LaunchProtocol.Agents.Contains(launcher))
                    {
                        kept.Add($"launch.defaultAgent: agent-ha-bridge's newSession.launcher '{written}' is not a launcher it knows, so it picked one itself; so does this host (auto)");
                        launcher = "auto";
                    }
                    Put(launch, "defaultAgent", launcher, "newSession.launcher", "launch.defaultAgent");
                }
                if (Strings(ns["profiles"]) is { Count: > 0 } profiles) { Put(launch, "profiles", Array(profiles), "newSession.profiles", "launch.profiles"); }
                // After the profiles, which it is matched against.
                if (Str(ns["defaultProfile"]) is { } defaultProfile) { Put(launch, "defaultProfile", DefaultProfile(launch, defaultProfile), "newSession.defaultProfile", "launch.defaultProfile"); }
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
                            if (Tuning(perAgent[id]) is { } value) { Put(Agent(id), axis, value, $"newSession.{axis}.{id}", $"launch.agents.{id}.{axis}"); }
                        }
                    }
                    else if (axis != "model" && ns[axis] is not null) { notCarried.Add($"newSession.{axis} (agent-ha-bridge reads it per agent, as newSession.{axis}.<agent>)"); }
                    if (ns[axis + "s"] is JsonObject lists)
                    {
                        foreach (var id in Agents)
                        {
                            if (Tunings(lists[id]) is { Count: > 0 } values) { Put(Agent(id), axis + "s", Array(values), $"newSession.{axis}s.{id}", $"launch.agents.{id}.{axis}s"); }
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

    // agent-ha-bridge finds its default workspace with PowerShell's -eq, which ignores case, and
    // takes the first; the host compares labels exactly. So the default is carried as the label
    // the host gives the folder agent-ha-bridge would have chosen: the profile may already
    // approve that folder under a label of its own, and then agent-ha-bridge's entry is not
    // imported. Failing that, as the first label matching it whatever the case, else as written
    // (falling back to the first workspace, in both).
    // Each list is read by its own program's rules: a second entry under a label already taken is
    // offered as "label (folder)" by both, but agent-ha-bridge counts a label taken whatever its
    // case (LegacyChoices) and the host only as written (LaunchConfig).
    static string DefaultLabel(JsonObject launch, JsonNode? source, string configured)
    {
        var host = Parsed(launch["workspaces"]);
        var chosen = LegacyChoices(source).FirstOrDefault(w => string.Equals(w.Label, configured, StringComparison.OrdinalIgnoreCase));
        if (chosen.Path is not null && host.FirstOrDefault(w => string.Equals(Key(w.Path), chosen.Path, PathCase)) is { } kept) { return kept.Label; }
        return host.FirstOrDefault(w => string.Equals(w.Label, configured, StringComparison.OrdinalIgnoreCase))?.Label ?? configured;
    }

    // The workspaces as Get-BridgeWorkspaceChoices offered them: labelled by folder name when
    // unlabelled, and one whose label is taken already, whatever its case (a PowerShell
    // hashtable), offered as "label (folder)", or dropped if that is taken too. (It also left
    // out folders that did not exist when it ran, which is not known here.)
    static List<(string Label, string Path)> LegacyChoices(JsonNode? source)
    {
        var choices = new List<(string Label, string Path)>();
        if (source is not JsonArray list) { return choices; }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in list)
        {
            var (path, label) = Read(entry is JsonObject o ? Canonical(o) : entry);
            if (string.IsNullOrWhiteSpace(path)) { continue; }
            var folder = Key(path);
            var name = string.IsNullOrWhiteSpace(label) ? LabelOf(null, path) : label;
            if (seen.Contains(name)) { name = $"{name} ({folder})"; }
            if (!seen.Add(name)) { continue; }
            choices.Add((name, folder));
        }
        return choices;
    }

    static IReadOnlyList<AgentBridge.Host.Delivery.Launch.WorkspaceConfig> Parsed(JsonNode? workspaces)
    {
        if (workspaces is not JsonArray list) { return []; }
        var canonical = new JsonArray(list.Select(n => n is JsonObject o ? (JsonNode)Canonical(o) : n?.DeepClone()).ToArray());
        using var doc = JsonDocument.Parse(new JsonObject { ["workspaces"] = canonical }.ToJsonString());
        return AgentBridge.Host.Delivery.Launch.LaunchConfig.Read(doc.RootElement).Config.Workspaces;
    }

    // The same for the Agency profile (Get-BridgeDefaultAgencyProfile's -eq), against the profiles
    // the host offers from its configured list. Profiles Agency lists at launch are not known
    // here, so with no list the default is carried as written.
    static string DefaultProfile(JsonObject launch, string configured) =>
        (launch["profiles"] as JsonArray ?? []).Select(p => Str(p)).FirstOrDefault(p => string.Equals(p, configured, StringComparison.OrdinalIgnoreCase)) ?? configured;

    static readonly StringComparison PathCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    static void Workspaces(JsonNode? source, JsonObject launch, List<string> imported, List<string> kept)
    {
        if (source is not JsonArray list || list.Count == 0) { return; }
        if (launch["workspaces"] is not null and not JsonArray) { kept.Add("launch.workspaces: this profile's is not a list, so agent-ha-bridge's workspaces were not imported"); return; }
        var target = launch["workspaces"] as JsonArray ?? [];
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
                // Looked up through the source's own case-insensitive names (agent-ha-bridge reads
                // "Isolate" as isolate) and written under the names LaunchConfig reads.
                JsonObject o => Canonical(o),
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

    static JsonObject Canonical(JsonObject o)
    {
        var copy = new JsonObject();
        foreach (var key in new[] { "path", "label", "isolate" })
        {
            if (o[key] is { } value) { copy[key] = value.DeepClone(); }
        }
        return copy;
    }

    // A tuning value as agent-ha-bridge passes it on the command line: a number is its text
    // (newSession.context.codex: 200000).
    static string? Tuning(JsonNode? n) =>
        Str(n) ?? (n is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.ToJsonString() : null);

    // Read through @(...) by agent-ha-bridge too (Get-BridgeTuningChoices): one value is a list of one.
    static List<string>? Tunings(JsonNode? n) => n is JsonArray a ? a.Select(Tuning).OfType<string>().ToList() : Tuning(n) is { } one ? [one] : null;

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    static double? Number(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    // agent-ha-bridge reads its lists through @(...), so one string is a list of one.
    static List<string>? Strings(JsonNode? n) => n is JsonArray a ? a.Select(Str).OfType<string>().ToList() : Str(n) is { } one ? [one] : null;

    static JsonArray Array(IEnumerable<string> values) => new(values.Select(v => (JsonNode)v).ToArray());
}
