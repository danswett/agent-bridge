using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;

namespace AgentBridge.HomeAssistant;

// One topic compared: the same, different, or held by one side only.
public sealed record ShadowEntry(string Topic, string Verdict, string? Daemon, string? Host, string? Note);

public sealed record ShadowReport(
    string Machine,
    string Slug,
    IReadOnlyList<string> HostNodes,
    IReadOnlyList<string> DaemonNodes,
    int Same,
    IReadOnlyList<ShadowEntry> Differences);

// The read-only shadow (docs/ha-connector.md): what this host's connector would publish for the
// machine, beside what the broker actually holds for it now, which while agent-ha-bridge runs is
// what its daemon published. Meant to run beside the daemon before a machine is moved, so the two
// can be compared on the real dashboard's own Home Assistant without either touching the other.
//
// Nothing is written. The connector runs one pass against DryRun, which forwards reads (states,
// the entity registry, the served dashboard) and keeps every write to itself; what the broker
// holds is read through Home Assistant's mqtt/subscribe, which only listens.
public static partial class HaShadow
{
    public const string Same = "same";
    public const string Differs = "differs";
    public const string DaemonOnly = "agent-ha-bridge only";
    public const string HostOnly = "host only";

    // Fields that carry the moment they were written, which no two publishers share.
    static readonly string[] Volatile = ["updated", "asked_at"];

    // usage: the host's agent usage, as serve would publish it; without it the usage sensors are
    // reported as not compared rather than as something only the daemon serves.
    public static async Task<ShadowReport> CompareAsync(IHomeAssistant real, ISessionSource source, HaConnectorOptions options, bool servesAnswers,
        TimeSpan window, Func<(UsageReport Report, long Version)>? usage = null, CancellationToken cancellationToken = default)
    {
        var dry = new DryRun(real);
        var dryOptions = options with
        {
            StateFile = null,
            PinDelay = TimeSpan.Zero,
            FieldPinDelay = TimeSpan.Zero,
            SelectDelay = TimeSpan.Zero,
            SelectAttempts = 1,
            ConsumeAnswers = false,
        };
        // Never asked to deliver anything: the dry run watches nothing, so no press reaches it.
        Func<AnswerRequest, CancellationToken, Task<AnswerResult>>? answer = servesAnswers
            ? (_, _) => Task.FromResult(new AnswerResult(AnswerOutcome.Refused, "shadow"))
            : null;
        Func<StopRequest, CancellationToken, Task<StopResult>>? stop = servesAnswers
            ? (_, _) => Task.FromResult(new StopResult(StopOutcome.Refused, "shadow"))
            : null;
        await using (var connector = new HaConnector(dryOptions, dry, source, () => null, usage, answer: answer, stop: stop))
        {
            await connector.TickAsync(cancellationToken);
        }

        var slug = HaContract.MachineSlug(options.MachineName);
        var hostNodes = source.Snapshot().Select(s => HaContract.NodeId(s.Id)).Distinct().Order(StringComparer.Ordinal).ToList();
        var machineFilters = new[]
        {
            $"{HaContract.DiscoveryPrefix}/+/{HaContract.MachineNode(slug)}/#",
            $"{HaContract.MachineTopicRoot(slug)}/#",
        };
        var held = (await real.ReadTopicsAsync(machineFilters, window, cancellationToken)).Where(m => m.Retain && m.Payload.Length > 0).ToList();
        // The daemon's own list of this machine's sessions, so a session only it knows is seen.
        var daemonNodes = new List<string>();
        if (held.FirstOrDefault(m => m.Topic == HaContract.GlobalAttributesTopic(slug)) is { } global)
        {
            try
            {
                using var doc = JsonDocument.Parse(global.Payload);
                if (doc.RootElement.TryGetProperty("sessions", out var list) && list.ValueKind == JsonValueKind.Array)
                {
                    daemonNodes.AddRange(list.EnumerateArray()
                        .Select(s => s.ValueKind == JsonValueKind.Object && s.TryGetProperty("node", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null)
                        .OfType<string>().Where(n => n.StartsWith("agent_bridge_", StringComparison.Ordinal)));
                }
            }
            catch (JsonException) { }
        }
        var nodes = hostNodes.Union(daemonNodes, StringComparer.Ordinal).ToList();
        var sessionFilters = nodes.SelectMany(n => new[] { $"{HaContract.DiscoveryPrefix}/+/{n}/#", $"{HaContract.TopicRoot}/{n}/#" }).ToList();
        if (sessionFilters.Count > 0)
        {
            held.AddRange((await real.ReadTopicsAsync(sessionFilters, window, cancellationToken)).Where(m => m.Retain && m.Payload.Length > 0));
        }

        var daemon = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in held) { daemon[m.Topic] = m.Payload; }
        var host = dry.Retained.Where(r => machineFilters.Concat(sessionFilters).Any(f => Matches(f, r.Key))).ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);

        var same = 0;
        var differences = new List<ShadowEntry>();
        foreach (var topic in daemon.Keys.Union(host.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var d = daemon.GetValueOrDefault(topic);
            var h = host.GetValueOrDefault(topic);
            if (d is not null && h is not null)
            {
                if (Equivalent(d, h)) { same++; continue; }
                differences.Add(new ShadowEntry(topic, Differs, d, h, Keys(d, h)));
            }
            else if (d is not null) { differences.Add(new ShadowEntry(topic, DaemonOnly, d, null, NotServed(topic, servesAnswers, usage is not null, options.UsageAccountSensors))); }
            else { differences.Add(new ShadowEntry(topic, HostOnly, null, h, null)); }
        }
        return new ShadowReport(options.MachineName, slug, hostNodes, [.. daemonNodes.Distinct().Order(StringComparer.Ordinal)], same, differences);
    }

    // Why a topic the daemon holds is not one this host publishes, when that is by design.
    static string? NotServed(string topic, bool servesAnswers, bool hasUsage, IReadOnlyList<string> accountSensors)
    {
        if (!hasUsage && (topic.Contains("/usage_", StringComparison.Ordinal) || topic.Contains("/usage/", StringComparison.Ordinal))) { return "usage not compared (no reading was taken)"; }
        // A further login's sensor is compared like any other when the host reports that login; one
        // it is not configured with is the daemon's alone, and the connector withdraws it.
        if (NumberedUsage().Match(topic) is { Success: true } m && !accountSensors.Contains(m.Groups[1].Value))
        {
            return "a further Copilot login this host is not configured to report (usage.copilotAccounts)";
        }
        // The card's choices are the launcher's, which a shadow does not ask for.
        if (topic.Contains("/new_", StringComparison.Ordinal) || topic.Contains("/newsession/", StringComparison.Ordinal))
        {
            return "launch card not compared (the shadow takes no launch options)";
        }
        // The update buttons are served only by a host its updater installed, which a shadow beside
        // the daemon does not stand in for: it looks for no release.
        foreach (var part in new[] { "/install_update/", "/check_updates/" })
        {
            if (topic.Contains(part, StringComparison.Ordinal)) { return "update buttons not compared (the shadow looks for no release)"; }
        }
        foreach (var part in new[] { "/transfer", "/transfer_request/" })
        {
            if (topic.Contains(part, StringComparison.Ordinal)) { return "not served yet (a later stage)"; }
        }
        if (!servesAnswers && (new[] { "/decision/", "/reply/", "/replypayload/", "/submit/", "/stop/" }.Any(p => topic.Contains(p, StringComparison.Ordinal))
            || FieldConfig().IsMatch(topic)))
        {
            return "not served without answers";
        }
        return null;
    }

    [System.Text.RegularExpressions.GeneratedRegex("/f[0-9]+/config$")]
    private static partial System.Text.RegularExpressions.Regex FieldConfig();

    [System.Text.RegularExpressions.GeneratedRegex("/usage[_/](copilot_[0-9]+)/")]
    private static partial System.Text.RegularExpressions.Regex NumberedUsage();

    // Payloads as Home Assistant reads them: JSON by value, the moment of writing left out.
    static bool Equivalent(string a, string b) => a == b || (Parse(a) is { } x && Parse(b) is { } y && JsonNode.DeepEquals(x, y));

    static JsonNode? Parse(string payload)
    {
        if (!(payload.StartsWith('{') || payload.StartsWith('['))) { return null; }
        try
        {
            var node = JsonNode.Parse(payload);
            if (node is JsonObject o) { foreach (var key in Volatile) { o.Remove(key); } }
            return node;
        }
        catch (JsonException) { return null; }
    }

    // Which top-level keys differ, for the report.
    static string? Keys(string a, string b)
    {
        if (Parse(a) is not JsonObject x || Parse(b) is not JsonObject y) { return null; }
        var keys = x.Select(p => p.Key).Union(y.Select(p => p.Key)).Where(k => !JsonNode.DeepEquals(x[k], y[k])).Order(StringComparer.Ordinal);
        return "keys: " + string.Join(", ", keys);
    }

    internal static bool Matches(string filter, string topic)
    {
        var f = filter.Split('/');
        var t = topic.Split('/');
        for (var i = 0; i < f.Length; i++)
        {
            if (f[i] == "#") { return true; }
            if (i >= t.Length || (f[i] != "+" && f[i] != t[i])) { return false; }
        }
        return f.Length == t.Length;
    }

    // Reads go to Home Assistant; writes go nowhere. Kept as a separate type, with no path from a
    // write method to the real one, so "read-only" is a property of the code, not of care.
    sealed class DryRun(IHomeAssistant real) : IHomeAssistant
    {
        public Dictionary<string, string> Retained { get; } = new(StringComparer.Ordinal);

        public Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken = default)
        {
            if (retain)
            {
                if (payload.Length == 0) { Retained.Remove(topic); } else { Retained[topic] = payload; }
            }
            return Task.CompletedTask;
        }

        public Task CallServiceAsync(string domain, string service, JsonObject data, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RenameEntityAsync(string entityId, string newEntityId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteImageAsync(string imageId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        // As Home Assistant would name it, so a dry pass reads it as made and touches nothing more.
        public Task<string> CreateInputBooleanAsync(string name, string icon, CancellationToken cancellationToken = default) =>
            Task.FromResult(HaContract.HelperIdOf(name));
        public Task DeleteInputBooleanAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetEntityNameAsync(string entityId, string name, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListInputBooleansAsync(CancellationToken cancellationToken = default) => real.ListInputBooleansAsync(cancellationToken);
        public Task<byte[]?> DownloadImageAsync(string imageId, long maxBytes, CancellationToken cancellationToken = default) => real.DownloadImageAsync(imageId, maxBytes, cancellationToken);
        public async IAsyncEnumerable<HaTrigger> WatchAsync(IReadOnlyCollection<string> entityIds, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        // A dry pass takes no tap from a phone either.
        public async IAsyncEnumerable<HaEvent> EventsAsync(string eventType, Action? subscribed = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<HaState?> GetStateAsync(string entityId, CancellationToken cancellationToken = default) => real.GetStateAsync(entityId, cancellationToken);
        public Task<IReadOnlyList<HaState>> GetStatesAsync(CancellationToken cancellationToken = default) => real.GetStatesAsync(cancellationToken);
        public Task<IReadOnlyList<HaRegistryEntry>> ListEntityRegistryAsync(CancellationToken cancellationToken = default) => real.ListEntityRegistryAsync(cancellationToken);
        public Task<HaDashboard> ReadDashboardAsync(string urlPath, CancellationToken cancellationToken = default) => real.ReadDashboardAsync(urlPath, cancellationToken);
        public Task<IReadOnlyList<HaMessage>> ReadTopicsAsync(IReadOnlyList<string> filters, TimeSpan window, CancellationToken cancellationToken = default) => real.ReadTopicsAsync(filters, window, cancellationToken);
    }
}
