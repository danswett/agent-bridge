using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentBridge.HomeAssistant;

// agent-ha-bridge's per-machine Home Assistant contract: the topics, entity ids and discovery
// payloads its PowerShell daemon publishes (hooks/decision-mqtt.ps1), which the dashboard
// DSWETT-HOME generates is built around. A migrated machine has to publish exactly these, or
// its cards stop drawing (docs/ha-connector.md). Every function here is a port of one there,
// named in its comment, and tests/AgentBridge.HomeAssistant.Tests checks each against the
// fixture recorded by running the original (tools/ha-contract/Export-HaContract.ps1).
//
// Nothing here is this product's design: a name that looks odd (copilot/cli for every agent,
// object_id that Home Assistant ignores) is kept because the dashboard and the other machines'
// daemons read it.
public static partial class HaContract
{
    public const string DiscoveryPrefix = "homeassistant";
    public const string TopicRoot = "copilot/cli";
    // Home Assistant caps a state at 255 characters; long text rides in attributes.
    public const int StateMaxChars = 255;
    // Per-field dropdown slots a session publishes ($script:DecisionMaxFormFields).
    public const int MaxFields = 12;
    // The liveness sensor's expire_after, and the beat that keeps it on: three missed beats
    // before a machine reads offline (OnlineExpireSeconds, OnlineHeartbeatSeconds).
    public const int OnlineExpireSeconds = 180;
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);
    // Republishing the machine's session list this often even when nothing changed, so a Home
    // Assistant restart that drops retained values heals within the window (GlobalReassertSeconds).
    public static readonly TimeSpan GlobalReassert = TimeSpan.FromSeconds(300);
    public const string Manufacturer = "AI CLI bridge";
    public const string UpdateTitle = "AI coding agent Home Assistant bridge";

    // Payloads are compact JSON; Home Assistant parses them, so escaping only has to be valid.
    public static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Serialize(JsonNode node) => node.ToJsonString(Json);

    // PowerShell's -replace is case-insensitive; the patterns are ported with that option so a
    // character such as the Kelvin sign is treated as it is there.
    [GeneratedRegex("[^a-z0-9]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SlugRun();

    [GeneratedRegex("[^a-zA-Z0-9]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NodeStrip();

    // Get-BridgeMachineSlug: lowercase, every run of anything else one underscore, at most 24.
    public static string MachineSlug(string machineName)
    {
        var clean = SlugRun().Replace(machineName.ToLowerInvariant(), "_").Trim('_');
        if (clean.Length > 24) { clean = clean[..24].Trim('_'); }
        return string.IsNullOrWhiteSpace(clean) ? "machine" : clean;
    }

    // Get-CopilotMqttNodeId: the session id's letters and digits, at most 16, lowercased; an id
    // with none gets the first 12 hex digits of its SHA-256 so distinct ids stay distinct.
    public static string NodeId(string sessionId)
    {
        var clean = NodeStrip().Replace(sessionId, "");
        if (string.IsNullOrWhiteSpace(clean))
        {
            clean = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)))[..12];
        }
        if (clean.Length > 16) { clean = clean[..16]; }
        return "agent_bridge_" + clean.ToLowerInvariant();
    }

    // Get-CopilotMqttMachineNode, Get-CopilotMqttMachineTopicRoot, Get-BridgeMachineEntityId.
    public static string MachineNode(string slug) => $"agent_bridge_{slug}";
    public static string MachineTopicRoot(string slug) => $"{TopicRoot}/machine/{slug}";
    public static string MachineEntityId(string domain, string key, string slug) => $"{domain}.agent_bridge_{slug}_{key}";
    public static string DiscoveryTopic(string component, string node, string objectId) => $"{DiscoveryPrefix}/{component}/{node}/{objectId}/config";

    // Get-CopilotMqttMachineDevice: one Home Assistant device per machine.
    public static JsonObject MachineDevice(string slug, string machineName) => new()
    {
        ["identifiers"] = new JsonArray($"agent_bridge_{slug}"),
        ["name"] = $"AI Agent Bridge ({machineName})",
        ["manufacturer"] = Manufacturer,
    };

    // New-CopilotMqttDeviceBlock: one device per session, named as the session is.
    public static JsonObject SessionDevice(string node, string sessionName, string machineName) => new()
    {
        ["identifiers"] = new JsonArray(node),
        ["name"] = sessionName,
        ["manufacturer"] = Manufacturer,
        ["model"] = machineName,
    };

    public static JsonArray SessionAvailability(SessionTopics topics) =>
    [
        new JsonObject { ["topic"] = topics.Availability, ["payload_available"] = "online", ["payload_not_available"] = "offline" },
    ];

    // New-CopilotMqttSensorConfigs: the two sensors a session's card reads.
    public static (string Topic, JsonObject Config) StatusConfig(SessionTopics t, string sessionName, string machineName) =>
        (DiscoveryTopic("sensor", t.Node, "status"), new JsonObject
        {
            ["name"] = "Status",
            ["unique_id"] = $"{t.Node}_status",
            ["state_topic"] = t.StatusState,
            ["json_attributes_topic"] = t.StatusAttributes,
            ["icon"] = "mdi:robot",
            ["device"] = SessionDevice(t.Node, sessionName, machineName),
            ["availability"] = SessionAvailability(t),
        });

    public static (string Topic, JsonObject Config) ActivityConfig(SessionTopics t, string sessionName, string machineName) =>
        (DiscoveryTopic("sensor", t.Node, "activity"), new JsonObject
        {
            ["name"] = "Activity",
            ["unique_id"] = $"{t.Node}_activity",
            ["state_topic"] = t.ActivityState,
            ["json_attributes_topic"] = t.ActivityAttributes,
            ["icon"] = "mdi:pulse",
            ["device"] = SessionDevice(t.Node, sessionName, machineName),
            ["availability"] = SessionAvailability(t),
        });

    // Publish-CopilotMqttMachineOnlineConfig. The beat on its state topic is deliberately not
    // retained: expire_after turns the machine offline when beats stop, and a Home Assistant
    // restart cannot resurrect a stale "online".
    public static (string Topic, JsonObject Config) OnlineConfig(string slug, string machineName, int expireAfter = OnlineExpireSeconds) =>
        (DiscoveryTopic("binary_sensor", MachineNode(slug), "online"), new JsonObject
        {
            ["name"] = "Online",
            ["unique_id"] = $"agent_bridge_{slug}_online",
            ["object_id"] = $"agent_bridge_{slug}_online",
            ["state_topic"] = $"{MachineTopicRoot(slug)}/online/state",
            ["device_class"] = "connectivity",
            ["payload_on"] = "online",
            ["payload_off"] = "offline",
            ["expire_after"] = expireAfter,
            ["device"] = MachineDevice(slug, machineName),
        });

    public static string OnlineStateTopic(string slug) => $"{MachineTopicRoot(slug)}/online/state";

    // Publish-CopilotMqttGlobalStatus: the machine's session list, which is also how every other
    // machine and the dashboard writer learn what this one runs.
    public static (string Topic, JsonObject Config) SessionsConfig(string slug, string machineName) =>
        (DiscoveryTopic("sensor", MachineNode(slug), "sessions"), new JsonObject
        {
            ["name"] = "Sessions",
            ["unique_id"] = $"agent_bridge_{slug}_sessions",
            ["object_id"] = $"agent_bridge_{slug}_sessions",
            ["state_topic"] = $"{MachineTopicRoot(slug)}/global/state",
            ["json_attributes_topic"] = $"{MachineTopicRoot(slug)}/global/attr",
            ["icon"] = "mdi:robot-happy",
            ["device"] = MachineDevice(slug, machineName),
        });

    public static string GlobalStateTopic(string slug) => $"{MachineTopicRoot(slug)}/global/state";
    public static string GlobalAttributesTopic(string slug) => $"{MachineTopicRoot(slug)}/global/attr";

    // Publish-CopilotMqttUsage: one sensor per agent, so each has its own history.
    public static (string Topic, JsonObject Config) UsageConfig(string slug, string machineName, string client, string label) =>
        (DiscoveryTopic("sensor", MachineNode(slug), $"usage_{client}"), new JsonObject
        {
            ["name"] = $"{label} usage",
            ["unique_id"] = $"agent_bridge_{slug}_usage_{client}",
            ["object_id"] = $"agent_bridge_{slug}_usage_{client}",
            ["state_topic"] = $"{MachineTopicRoot(slug)}/usage/{client}/state",
            ["json_attributes_topic"] = $"{MachineTopicRoot(slug)}/usage/{client}/attr",
            ["unit_of_measurement"] = "%",
            ["state_class"] = "measurement",
            ["icon"] = "mdi:gauge",
            ["device"] = MachineDevice(slug, machineName),
        });

    public static string UsageStateTopic(string slug, string client) => $"{MachineTopicRoot(slug)}/usage/{client}/state";
    public static string UsageAttributesTopic(string slug, string client) => $"{MachineTopicRoot(slug)}/usage/{client}/attr";

    // Publish-CopilotMqttUpdate's entity. The machine row on the dashboard reads its
    // installed_version; availability follows latest_version being known.
    public static (string Topic, JsonObject Config) UpdateConfig(string slug, string machineName) =>
        (DiscoveryTopic("update", MachineNode(slug), "update"), new JsonObject
        {
            ["name"] = "Update",
            ["unique_id"] = $"agent_bridge_{slug}_update",
            ["object_id"] = $"agent_bridge_{slug}_update",
            ["state_topic"] = UpdateStateTopic(slug),
            ["json_attributes_topic"] = UpdateAttributesTopic(slug),
            ["availability_topic"] = UpdateStateTopic(slug),
            ["availability_template"] = "{{ 'online' if value_json.get('latest_version') else 'offline' }}",
            ["device_class"] = "firmware",
            ["icon"] = "mdi:package-up",
            ["device"] = MachineDevice(slug, machineName),
        });

    public static string UpdateStateTopic(string slug) => $"{MachineTopicRoot(slug)}/update/state";
    public static string UpdateAttributesTopic(string slug) => $"{MachineTopicRoot(slug)}/update/attributes";

    // Publish-CopilotMqttSession's answer controls: the selector parked on Idle, the reply box,
    // the sensor the reply card publishes through, the twelve field slots and Send.
    public const int ReplyMaxChars = 255;

    public static (string Topic, JsonObject Config) DecisionConfig(SessionTopics t, string sessionName, string machineName, IEnumerable<string> options, bool enabledByDefault)
    {
        var config = new JsonObject
        {
            ["name"] = "Decision",
            ["unique_id"] = $"{t.Node}_decision",
            ["command_topic"] = t.DecisionCommand,
            ["json_attributes_topic"] = t.DecisionAttributes,
            ["options"] = new JsonArray([.. options.Select(o => (JsonNode)o)]),
            ["icon"] = "mdi:comment-question-outline",
            ["device"] = SessionDevice(t.Node, sessionName, machineName),
            ["availability"] = SessionAvailability(t),
        };
        // Only the session's first publish says so; arming and clearing never did.
        if (enabledByDefault) { config["enabled_by_default"] = true; }
        return (DiscoveryTopic("select", t.Node, "decision"), config);
    }

    public static (string Topic, JsonObject Config) ReplyConfig(SessionTopics t, string sessionName, string machineName) =>
        (DiscoveryTopic("text", t.Node, "reply"), new JsonObject
        {
            ["name"] = "Reply",
            ["unique_id"] = $"{t.Node}_reply",
            ["command_topic"] = t.ReplyCommand,
            ["max"] = ReplyMaxChars,
            ["mode"] = "text",
            ["icon"] = "mdi:reply",
            ["device"] = SessionDevice(t.Node, sessionName, machineName),
            ["availability"] = SessionAvailability(t),
        });

    // New-CopilotMqttReplyPayloadConfig: its state is the publish's stamp, its attributes what
    // was sent.
    public static (string Topic, JsonObject Config) ReplyPayloadConfig(SessionTopics t, string sessionName, string machineName) =>
        (DiscoveryTopic("sensor", t.Node, "replypayload"), new JsonObject
        {
            ["name"] = "Reply payload",
            ["unique_id"] = $"{t.Node}_reply_payload",
            ["state_topic"] = t.ReplyPayload,
            ["value_template"] = "{{ value_json.at }}",
            ["json_attributes_topic"] = t.ReplyPayload,
            ["icon"] = "mdi:reply-all",
            ["device"] = SessionDevice(t.Node, sessionName, machineName),
            ["availability"] = SessionAvailability(t),
        });

    public static (string Topic, JsonObject Config) FieldConfig(SessionTopics t, string sessionName, string machineName, int index, string label, IEnumerable<string> options) =>
        (DiscoveryTopic("select", t.Node, $"f{index}"), new JsonObject
        {
            ["name"] = label,
            ["unique_id"] = $"{t.Node}_f{index}",
            ["command_topic"] = $"{t.FieldCommandPrefix}{index}/set",
            ["options"] = new JsonArray([.. options.Select(o => (JsonNode)o)]),
            ["icon"] = "mdi:form-select",
            ["device"] = SessionDevice(t.Node, sessionName, machineName),
            ["availability"] = SessionAvailability(t),
        });

    public static (string Topic, JsonObject Config) SubmitConfig(SessionTopics t, string sessionName, string machineName) =>
        (DiscoveryTopic("button", t.Node, "submit"), new JsonObject
        {
            ["name"] = "Submit answer",
            ["unique_id"] = $"{t.Node}_submit",
            ["command_topic"] = t.SubmitCommand,
            ["icon"] = "mdi:send-check",
            ["device"] = SessionDevice(t.Node, sessionName, machineName),
            ["availability"] = SessionAvailability(t),
        });

    // End session (Publish-CopilotMqttSession's $stopConfig): two presses for a session that is
    // not idle, one for an idle one (HaConnector.Stops.cs).
    public static (string Topic, JsonObject Config) StopConfig(SessionTopics t, string sessionName, string machineName) =>
        (DiscoveryTopic("button", t.Node, "stop"), new JsonObject
        {
            ["name"] = "End session",
            ["unique_id"] = $"{t.Node}_stop",
            ["command_topic"] = t.StopCommand,
            ["icon"] = "mdi:stop-circle-outline",
            ["device"] = SessionDevice(t.Node, sessionName, machineName),
            ["availability"] = SessionAvailability(t),
        });

    // The ids of the controls a session publishes now, keyed by unique_id, for pinning.
    public static IReadOnlyDictionary<string, string> SessionControlIds(string node)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [$"{node}_decision"] = SessionEntities.Decision(node),
            [$"{node}_reply"] = SessionEntities.Reply(node),
            [$"{node}_reply_payload"] = SessionEntities.ReplyPayload(node),
            [$"{node}_submit"] = SessionEntities.Submit(node),
        };
        for (var i = 1; i <= MaxFields; i++) { ids[$"{node}_f{i}"] = SessionEntities.Field(node, i); }
        return ids;
    }

    // Get-CopilotMqttSessionDiscoveryTopic: every retained discovery topic of one session node.
    public static IReadOnlyList<string> SessionDiscoveryTopics(string node)
    {
        var topics = new List<string>();
        foreach (var (component, obj) in new[]
        {
            ("select", "decision"), ("text", "reply"), ("sensor", "replypayload"),
            ("sensor", "status"), ("sensor", "activity"), ("button", "submit"), ("button", "stop"),
        })
        {
            topics.Add(DiscoveryTopic(component, node, obj));
        }
        for (var i = 1; i <= MaxFields; i++) { topics.Add(DiscoveryTopic("select", node, $"f{i}")); }
        return topics;
    }

    // Get-CopilotMqttSessionStateTopic: every retained state topic of one session node.
    public static IReadOnlyList<string> SessionStateTopics(string node)
    {
        var root = $"{TopicRoot}/{node}";
        return
        [
            $"{root}/decision/state", $"{root}/decision/attr", $"{root}/reply/state", $"{root}/replypayload/set",
            $"{root}/status/state", $"{root}/status/attr", $"{root}/activity/state", $"{root}/activity/attr", $"{root}/available",
        ];
    }

    // Get-CopilotMqttMachineTopic: every retained topic of one machine's own entities (25).
    public static IReadOnlyList<string> MachineTopics(string slug)
    {
        var node = MachineNode(slug);
        var root = MachineTopicRoot(slug);
        return
        [
            DiscoveryTopic("update", node, "update"),
            DiscoveryTopic("button", node, "install_update"),
            DiscoveryTopic("text", node, "new_prompt"),
            DiscoveryTopic("sensor", node, "new_prompt_payload"),
            DiscoveryTopic("sensor", node, "transfer_request"),
            DiscoveryTopic("select", node, "new_workspace"),
            DiscoveryTopic("select", node, "new_profile"),
            DiscoveryTopic("select", node, "new_agent"),
            DiscoveryTopic("select", node, "new_model"),
            DiscoveryTopic("select", node, "new_effort"),
            DiscoveryTopic("select", node, "new_context"),
            DiscoveryTopic("select", node, "new_resume"),
            DiscoveryTopic("select", node, "new_permissions"),
            DiscoveryTopic("button", node, "new_session"),
            DiscoveryTopic("sensor", node, "new_session_result"),
            DiscoveryTopic("sensor", node, "sessions"),
            DiscoveryTopic("binary_sensor", node, "online"),
            $"{root}/update/state",
            $"{root}/update/attributes",
            $"{root}/newsession/result",
            $"{root}/newsession/promptpayload",
            $"{root}/transfer/request",
            $"{root}/online/state",
            $"{root}/global/state",
            $"{root}/global/attr",
        ];
    }

    public static readonly IReadOnlyList<string> UsageClients = ["copilot", "claude", "codex"];

    // Get-CopilotMqttUsageTopics.
    public static IReadOnlyList<string> UsageTopics(string slug, IEnumerable<string>? clients = null)
    {
        var topics = new List<string>();
        foreach (var client in clients ?? UsageClients)
        {
            topics.Add(DiscoveryTopic("sensor", MachineNode(slug), $"usage_{client}"));
            topics.Add(UsageStateTopic(slug, client));
            topics.Add(UsageAttributesTopic(slug, client));
        }
        return topics;
    }

    // Get-BridgeMachineForgetTopic: the machine's own topics, then each listed session's.
    public static IReadOnlyList<string> ForgetTopics(string slug, IEnumerable<string> sessionNodes)
    {
        var topics = new List<string>(MachineTopics(slug));
        foreach (var node in sessionNodes.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            topics.AddRange(SessionDiscoveryTopics(node));
            topics.AddRange(SessionStateTopics(node));
        }
        return topics;
    }

    // Remove-CopilotMqttSession: offline first, so a card still open greys out, then every
    // discovery topic (Home Assistant drops the entities) and every state topic (a node reused
    // later cannot inherit stale values). All retained; 29 messages.
    public static IReadOnlyList<HaMessage> SessionRemoval(string node)
    {
        var t = Topics(node);
        var messages = new List<HaMessage> { new(t.Availability, "offline", true) };
        messages.AddRange(SessionDiscoveryTopics(node).Select(topic => new HaMessage(topic, "", true)));
        messages.AddRange(SessionStateTopics(node).Select(topic => new HaMessage(topic, "", true)));
        return messages;
    }

    public static SessionTopics Topics(string node) => new(node);

    // Entity ids Home Assistant would otherwise derive from device and entity names. The daemon
    // forces them onto these (Set-CopilotMqttEntityIds and friends, through the entity
    // registry), and the dashboard and other machines address them by these ids alone.
    public static IReadOnlyDictionary<string, string> SessionSensorIds(string node) => new Dictionary<string, string>
    {
        [$"{node}_status"] = $"sensor.{node}_status",
        [$"{node}_activity"] = $"sensor.{node}_activity",
    };
}

// Get-CopilotMqttTopics: the topics one session node's entities use.
public sealed record SessionTopics(string Node)
{
    public string Root => $"{HaContract.TopicRoot}/{Node}";
    public string Availability => $"{Root}/available";
    public string StatusState => $"{Root}/status/state";
    public string StatusAttributes => $"{Root}/status/attr";
    public string ActivityState => $"{Root}/activity/state";
    public string ActivityAttributes => $"{Root}/activity/attr";
    public string DecisionCommand => $"{Root}/decision/set";
    public string DecisionAttributes => $"{Root}/decision/attr";
    public string ReplyCommand => $"{Root}/reply/set";
    public string ReplyPayload => $"{Root}/replypayload/set";
    public string FieldCommandPrefix => $"{Root}/field";
    public string SubmitCommand => $"{Root}/submit/set";
    public string StopCommand => $"{Root}/stop/set";
}

// The entity ids of one session's controls, as the daemon pins them (Get-CopilotMqttEntityIds).
public static class SessionEntities
{
    public static string Decision(string node) => $"select.{node}_decision";
    public static string Reply(string node) => $"text.{node}_reply";
    public static string ReplyPayload(string node) => $"sensor.{node}_reply_payload";
    public static string Submit(string node) => $"button.{node}_submit";
    public static string Stop(string node) => $"button.{node}_stop";
    public static string Status(string node) => $"sensor.{node}_status";
    public static string Activity(string node) => $"sensor.{node}_activity";
    // Get-CopilotMqttFieldEntityId.
    public static string Field(string node, int index) => $"select.{node}_f{index}";
}

// One mqtt.publish: the topic, the payload as sent, and whether the broker keeps it.
public sealed record HaMessage(string Topic, string Payload, bool Retain);
