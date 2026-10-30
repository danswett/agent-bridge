using System.Text.Json.Nodes;

namespace AgentBridge.HomeAssistant;

// The launch card (Publish-CopilotMqttNewSession): the controls that start a new session on this
// machine, its note and the outcomes an agent reads (Fixtures/ha-launch.json).
public static partial class HaContract
{
    // The resume selector's first option, and its default (CopilotMqttNewSessionOption).
    public const string NewSessionOption = "New session";
    // Every tuning axis opens on this: launch without the flag, so the agent uses its own
    // default (BridgeTuningDefaultOption).
    public const string TuningDefault = "Agent default";
    // The permissions selector, cautious option first (Get-BridgePermissionOptions).
    public const string PermissionAsk = "Ask permission";
    public const string PermissionAllowAll = "Allow all";
    public static readonly IReadOnlyList<string> PermissionOptions = [PermissionAsk, PermissionAllowAll];
    // What a selector offers when there is nothing to offer: an MQTT select needs one option.
    public const string NoWorkspaces = "(no workspaces configured)";
    public const string NoProfile = "(default)";
    public const string NoAgents = "(no agents installed)";
    // The note a launch leaves is cleared after this long (NoteExpirySeconds).
    public static readonly TimeSpan LaunchNoteExpiry = TimeSpan.FromSeconds(600);
    // How many outcomes an agent can read back (Set-DaemonLaunchOutcome keeps five).
    public const int LaunchOutcomesKept = 5;

    // The tuning axes, in display order, with their selector's name and icon (BridgeTuningAxes).
    public static readonly IReadOnlyList<(string Axis, string Label, string Icon)> TuningAxes =
    [
        ("model", "Model", "mdi:chip"),
        ("effort", "Effort", "mdi:speedometer"),
        ("context", "Context", "mdi:arrow-expand-horizontal"),
    ];

    // What the card offers: each list as its selector shows it, without the placeholders.
    public sealed record LaunchCard
    {
        public IReadOnlyList<string> Workspaces { get; init; } = [];
        public IReadOnlyList<string> Profiles { get; init; } = [];
        public IReadOnlyList<string> Resumable { get; init; } = [];
        public IReadOnlyList<string> Agents { get; init; } = [];
        // Per axis, the selected agent's options, TuningDefault first.
        public IReadOnlyDictionary<string, IReadOnlyList<string>> Tuning { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
    }

    public static string NewSessionTopic(string slug, string what) => $"{MachineTopicRoot(slug)}/newsession/{what}";
    public static string LaunchResultTopic(string slug) => NewSessionTopic(slug, "result");
    public static string LaunchOutcomeTopic(string slug) => NewSessionTopic(slug, "outcome");
    public static string PromptPayloadTopic(string slug) => NewSessionTopic(slug, "promptpayload");

    // The card's entities, by key, as Set-CopilotMqttNewSessionEntityIds pins them (the transfer
    // request aside: a later stage).
    public static readonly IReadOnlyList<(string Domain, string Key)> LaunchEntities =
    [
        ("text", "new_prompt"), ("sensor", "new_prompt_payload"), ("select", "new_workspace"), ("select", "new_profile"),
        ("select", "new_agent"), ("select", "new_model"), ("select", "new_effort"), ("select", "new_context"),
        ("select", "new_resume"), ("select", "new_permissions"), ("button", "new_session"), ("sensor", "new_session_result"),
    ];

    // Publish-CopilotMqttNewSession's discovery configs, in its order, without the transfer request.
    public static IReadOnlyList<(string Topic, JsonObject Config)> LaunchConfigs(string slug, string machineName, LaunchCard card)
    {
        var node = MachineNode(slug);
        var root = MachineTopicRoot(slug);
        JsonObject Base(string name, string key, string icon) => new()
        {
            ["name"] = name,
            ["unique_id"] = $"agent_bridge_{slug}_{key}",
            ["object_id"] = $"agent_bridge_{slug}_{key}",
            ["icon"] = icon,
            ["device"] = MachineDevice(slug, machineName),
        };
        (string, JsonObject) Select(string name, string key, string what, IEnumerable<string> options, string placeholder, string icon)
        {
            var config = Base(name, key, icon);
            var offered = options.Where(o => !string.IsNullOrWhiteSpace(o)).ToList();
            config["command_topic"] = $"{root}/newsession/{what}/set";
            config["options"] = new JsonArray([.. (offered.Count > 0 ? offered : [placeholder]).Select(o => (JsonNode)o)]);
            return (DiscoveryTopic("select", node, key), config);
        }

        var configs = new List<(string, JsonObject)>();
        var prompt = Base("New session prompt", "new_prompt", "mdi:message-plus-outline");
        prompt["command_topic"] = $"{root}/newsession/prompt/set";
        prompt["max"] = ReplyMaxChars;
        prompt["mode"] = "text";
        configs.Add((DiscoveryTopic("text", node, "new_prompt"), prompt));
        var payload = Base("New session prompt payload", "new_prompt_payload", "mdi:message-plus-outline");
        payload["state_topic"] = PromptPayloadTopic(slug);
        payload["value_template"] = "{{ value_json.at }}";
        payload["json_attributes_topic"] = PromptPayloadTopic(slug);
        configs.Add((DiscoveryTopic("sensor", node, "new_prompt_payload"), payload));
        configs.Add(Select("New session workspace", "new_workspace", "workspace", card.Workspaces, NoWorkspaces, "mdi:folder-open-outline"));
        configs.Add(Select("New session profile", "new_profile", "profile", card.Profiles, NoProfile, "mdi:account-cog-outline"));
        configs.Add(Select("New session agent", "new_agent", "agent", card.Agents, NoAgents, "mdi:robot-outline"));
        configs.Add(Select("New session resume", "new_resume", "resume", [NewSessionOption, .. card.Resumable], NewSessionOption, "mdi:history"));
        foreach (var (axis, label, icon) in TuningAxes)
        {
            configs.Add(Select($"New session {label}", $"new_{axis}", axis, card.Tuning.TryGetValue(axis, out var o) ? o : [], TuningDefault, icon));
        }
        configs.Add(Select("New session permissions", "new_permissions", "permissions", PermissionOptions, PermissionAsk, "mdi:shield-key-outline"));
        var button = Base("Start new session", "new_session", "mdi:rocket-launch-outline");
        button["command_topic"] = $"{root}/newsession/start";
        configs.Add((DiscoveryTopic("button", node, "new_session"), button));
        var result = Base("New session result", "new_session_result", "mdi:information-outline");
        result["state_topic"] = LaunchResultTopic(slug);
        result["json_attributes_topic"] = LaunchOutcomeTopic(slug);
        configs.Add((DiscoveryTopic("sensor", node, "new_session_result"), result));
        return configs;
    }

    // Set-CopilotMqttNewSessionResult: the note, cut to what a state holds.
    public static string LaunchNote(string text) =>
        text.Length > StateMaxChars ? text[..(StateMaxChars - 3)] + "..." : text;

    // Get-BridgeResumableSessions' labels: the agent named once the list holds more than one kind,
    // the folder's last part after the summary, cut at 120, a repeat told apart by its id's first
    // eight characters, and a second repeat left out. In: newest first.
    public static IReadOnlyList<(string Label, int Index)> ResumeLabels(IReadOnlyList<(string Id, string Kind, string AgentLabel, string? Summary, string? Folder)> sessions)
    {
        var prefixed = sessions.Select(s => s.Kind).Distinct(StringComparer.Ordinal).Count() > 1;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var labels = new List<(string, int)>();
        for (var i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            var shortId = s.Id[..Math.Min(8, s.Id.Length)];
            var summary = System.Text.RegularExpressions.Regex.Replace(s.Summary ?? "", @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(summary)) { summary = $"Session {shortId}"; }
            if (prefixed) { summary = $"{s.AgentLabel}: {summary}"; }
            var leaf = string.IsNullOrWhiteSpace(s.Folder) ? "" : s.Folder.TrimEnd('\\', '/').Split('\\', '/')[^1];
            var label = leaf.Length > 0 ? $"{summary} - {leaf}" : summary;
            if (label.Length > 120) { label = label[..117] + "..."; }
            if (seen.Contains(label)) { label = $"{label} ({shortId})"; }
            if (!seen.Add(label)) { continue; }
            labels.Add((label, i));
        }
        return labels;
    }
}
