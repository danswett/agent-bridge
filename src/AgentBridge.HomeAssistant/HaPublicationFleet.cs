using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentBridge.HomeAssistant;

internal sealed record HaPublicationFleet(IReadOnlyCollection<HaFleetSession> Sessions, IReadOnlyCollection<HaFleetMachine> Machines)
{
    static readonly Regex Sensor = new(@"^sensor\.agent_bridge_([a-z0-9_]+)_sessions$", RegexOptions.CultureInvariant);
    static bool Node(string value) => value.StartsWith("agent_bridge_", StringComparison.Ordinal) &&
        value.Length > "agent_bridge_".Length && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    public static HaPublicationFleet Read(IReadOnlyList<HaState> states, string localSlug, string localName,
        IReadOnlyCollection<HaFleetSession> localSessions, JsonObject localCapabilities)
    {
        if (states.Select(s => s.EntityId).Distinct(StringComparer.Ordinal).Count() != states.Count)
        {
            throw new InvalidDataException("Ambiguous retained fleet inventory.");
        }
        var machines = new List<HaFleetMachine>();
        var sessions = new Dictionary<string, HaFleetSession>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in localSessions)
        {
            if (!Node(session.Node) || !sessions.TryAdd(session.Node, session)) { throw new InvalidDataException("Ambiguous local session descriptors."); }
        }
        machines.Add(Machine(localSlug, localName, JsonSerializer.SerializeToElement(localCapabilities), true, [.. sessions.Keys]));
        var inventory = states.ToDictionary(s => s.EntityId, StringComparer.Ordinal);
        foreach (var state in states.Where(s => Sensor.IsMatch(s.EntityId)).OrderBy(s => s.EntityId, StringComparer.CurrentCultureIgnoreCase))
        {
            var slug = Sensor.Match(state.EntityId).Groups[1].Value;
            // Our own complete descriptors win over an MQTT state still catching up.
            if (slug == localSlug) { continue; }
            var attributes = Object(state.Attributes);
            var name = Text(attributes, "machine", required: true);
            if (HaContract.MachineSlug(name) != slug ||
                (attributes.TryGetProperty("machine_slug", out var declared) && (declared.ValueKind != JsonValueKind.String || declared.GetString() != slug)))
            {
                throw new InvalidDataException("Retained machine identity does not match its entity.");
            }
            var onlineId = HaContract.MachineEntityId("binary_sensor", "online", slug);
            var online = inventory.TryGetValue(onlineId, out var liveness) && liveness.State == "on";
            if (liveness is not null && liveness.State is not ("on" or "off" or "unknown" or "unavailable"))
            {
                throw new InvalidDataException("Malformed retained machine liveness.");
            }
            if (!attributes.TryGetProperty("sessions", out var descriptors) || descriptors.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Incomplete retained machine session inventory.");
            }
            var nodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in descriptors.EnumerateArray())
            {
                var descriptor = Object(item);
                var node = Text(descriptor, "node", required: true);
                if (!Node(node) || !nodes.Add(node)) { throw new InvalidDataException("Ambiguous retained session identity."); }
                var remote = new HaFleetSession(node, HaPayloads.NeutraliseTemplates(Text(descriptor, "name", required: true)),
                    HaPayloads.NeutraliseTemplates(Text(descriptor, "machine") is { Length: > 0 } machine ? machine : name), Text(descriptor, "kind"));
                if (!online) { continue; }
                if (sessions.TryGetValue(node, out var existing))
                {
                    // Legacy MCP descriptors are deliberately shared by several machines.
                    if (existing != remote && (existing.Kind != "mcp" || remote.Kind != "mcp"))
                    {
                        throw new InvalidDataException("Conflicting peer session descriptors.");
                    }
                }
                else { sessions.Add(node, remote); }
            }
            var capabilities = attributes.TryGetProperty("capabilities", out var caps) && caps.ValueKind != JsonValueKind.Null ? Object(caps) : default;
            machines.Add(Machine(slug, HaPayloads.NeutraliseTemplates(name), capabilities, online, [.. nodes]));
        }
        return new([.. sessions.Values], machines);
    }

    static JsonElement Object(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object ||
            value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count())
        {
            throw new InvalidDataException("Malformed retained fleet attributes.");
        }
        return value;
    }

    static string Text(JsonElement value, string name, bool required = false)
    {
        if (!value.TryGetProperty(name, out var field))
        {
            return required ? throw new InvalidDataException("Incomplete retained fleet descriptor.") : "";
        }
        if (field.ValueKind != JsonValueKind.String || (required && string.IsNullOrWhiteSpace(field.GetString())))
        {
            throw new InvalidDataException("Malformed retained fleet descriptor.");
        }
        return field.GetString()!;
    }

    static HaFleetMachine Machine(string slug, string name, JsonElement capabilities, bool online, string[] nodes)
    {
        bool Capability(string key)
        {
            if (capabilities.ValueKind == JsonValueKind.Undefined || !capabilities.TryGetProperty(key, out var field)) { return false; }
            if (field.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) { throw new InvalidDataException("Malformed retained fleet capability."); }
            return field.GetBoolean();
        }
        return new(slug, name, Capability("profile"), Capability("resume"), Capability("agent"), Capability("tuning"),
            Capability("permissions"), Capability("detailed"), Capability("dev"), online, nodes);
    }
}
