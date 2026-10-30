using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentBridge.Host.Core;
using AgentBridge.HomeAssistant;
using AgentBridge.Protocol;

// An in-process Home Assistant with its MQTT broker, enough of both for the connector: retained
// topics as a broker keeps them (an empty retained payload clears one), MQTT discovery creating
// and removing entities the way Home Assistant does (named after device and entity, object_id
// ignored, so the connector has to pin them), states derived from what is retained, the entity
// registry, and the dashboard's served config. Nothing here touches a network.
internal sealed partial class FakeHomeAssistant : IHomeAssistant
{
    readonly object gate = new();
    public List<Published> Log { get; } = [];
    public Dictionary<string, string> Retained { get; } = new(StringComparer.Ordinal);
    // unique_id -> entity_id, as the entity registry holds them.
    public Dictionary<string, string> Registry { get; } = new(StringComparer.Ordinal);
    // discovery topic -> unique_id, so clearing a config removes its entity.
    readonly Dictionary<string, string> discovered = new(StringComparer.Ordinal);
    // States the test sets directly (helpers, other machines' sensors).
    public Dictionary<string, HaState> States { get; } = new(StringComparer.Ordinal);
    public List<(string From, string To)> Renames { get; } = [];
    public string? Dashboard { get; set; }
    public Exception? DashboardFails { get; set; }
    public Exception? PublishFails { get; set; }
    public Func<string, string, bool>? PublishFailsFor { get; set; }
    public int RegistryLists { get; private set; }

    public Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken = default)
    {
        if (PublishFails is { } e) { throw e; }
        if (PublishFailsFor?.Invoke(topic, payload) == true) { throw new HttpRequestException("Connection reset"); }
        lock (gate)
        {
            Log.Add(new Published(topic, payload, retain));
            if (retain)
            {
                if (payload.Length == 0) { Retained.Remove(topic); }
                else { Retained[topic] = payload; }
            }
            if (topic.StartsWith(HaContract.DiscoveryPrefix + "/", StringComparison.Ordinal) && topic.EndsWith("/config", StringComparison.Ordinal))
            {
                if (payload.Length == 0)
                {
                    if (discovered.Remove(topic, out var gone)) { Registry.Remove(gone); }
                }
                else
                {
                    var config = JsonNode.Parse(payload)!.AsObject();
                    var uniqueId = config["unique_id"]!.GetValue<string>();
                    discovered[topic] = uniqueId;
                    if (!Registry.ContainsKey(uniqueId))
                    {
                        var component = topic.Split('/')[1];
                        var name = $"{config["device"]?["name"]?.GetValue<string>()} {config["name"]?.GetValue<string>()}";
                        Registry[uniqueId] = Unique($"{component}.{Slugify(name)}");
                    }
                }
            }
        }
        return Task.CompletedTask;
    }

    string Unique(string entityId)
    {
        var taken = Registry.Values.ToHashSet(StringComparer.Ordinal);
        if (!taken.Contains(entityId)) { return entityId; }
        for (var i = 2; ; i++) { if (!taken.Contains($"{entityId}_{i}")) { return $"{entityId}_{i}"; } }
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotSlug();

    static string Slugify(string text) => NotSlug().Replace(text.ToLowerInvariant(), "_").Trim('_');

    public Task<HaState?> GetStateAsync(string entityId, CancellationToken cancellationToken = default)
    {
        lock (gate) { return Task.FromResult(States.TryGetValue(entityId, out var s) ? s : Derived(entityId)); }
    }

    public Task<IReadOnlyList<HaState>> GetStatesAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var all = new Dictionary<string, HaState>(States, StringComparer.Ordinal);
            foreach (var id in Registry.Values) { if (!all.ContainsKey(id) && Derived(id) is { } s) { all[id] = s; } }
            return Task.FromResult<IReadOnlyList<HaState>>([.. all.Values]);
        }
    }

    // An MQTT entity's state as Home Assistant would show it: from the retained state and
    // attribute topics its discovery config names.
    HaState? Derived(string entityId)
    {
        var uniqueId = Registry.FirstOrDefault(p => p.Value == entityId).Key;
        if (uniqueId is null) { return null; }
        var topic = discovered.First(p => p.Value == uniqueId).Key;
        var config = JsonNode.Parse(Retained[topic])!.AsObject();
        var state = config["state_topic"]?.GetValue<string>() is { } st && Retained.TryGetValue(st, out var v) ? v : "unknown";
        var attributes = config["json_attributes_topic"]?.GetValue<string>() is { } at && Retained.TryGetValue(at, out var a) ? a : "{}";
        return new HaState(entityId, state, JsonDocument.Parse(attributes).RootElement.Clone());
    }

    public Task<IReadOnlyList<HaRegistryEntry>> ListEntityRegistryAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            RegistryLists++;
            return Task.FromResult<IReadOnlyList<HaRegistryEntry>>([.. Registry.Select(p => new HaRegistryEntry(p.Value, p.Key))]);
        }
    }

    public Task RenameEntityAsync(string entityId, string newEntityId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (Registry.ContainsValue(newEntityId)) { throw new HaCommandException("config/entity_registry/update", "invalid_info", "Entity with this ID is already registered"); }
            var uniqueId = Registry.First(p => p.Value == entityId).Key;
            Registry[uniqueId] = newEntityId;
            Renames.Add((entityId, newEntityId));
        }
        return Task.CompletedTask;
    }

    public Task<HaDashboard> ReadDashboardAsync(string urlPath, CancellationToken cancellationToken = default)
    {
        if (DashboardFails is { } e) { throw e; }
        return Task.FromResult(Dashboard is null ? HaDashboard.NotFound : new HaDashboard(true, Dashboard));
    }

    // A dashboard as the writer would generate it, drawing these session nodes' cards.
    public void Draw(params string[] nodes) =>
        Dashboard = HaContract.Serialize(new JsonObject
        {
            ["views"] = new JsonArray(new JsonObject
            {
                ["cards"] = new JsonArray([.. nodes.Select(n => (JsonNode)new JsonObject { ["type"] = "custom:agent-bridge-activity-card", ["status"] = $"sensor.{n}_status", ["activity"] = $"sensor.{n}_activity" })]),
            }),
        });

    public void ClearLog() { lock (gate) { Log.Clear(); } }

    public IReadOnlyList<Published> To(string topic) { lock (gate) { return [.. Log.Where(p => p.Topic == topic)]; } }

    public bool Exists(string entityId) { lock (gate) { return Registry.ContainsValue(entityId); } }

    // A retained message from another bridge, as Home Assistant would have it after its discovery.
    public void Seed(string topic, string payload) => PublishAsync(topic, payload, true).GetAwaiter().GetResult();
}

// A session source the test drives: sessions appear, change and end when it says so.
internal sealed class FakeSessions : ISessionSource
{
    readonly Dictionary<string, Session> sessions = new(StringComparer.Ordinal);
    long revision;

    public long Revision => revision;
    public event EventHandler<SessionChange>? Changed;
    public DiscoveryHeld? Held { get; set; }

    public void Set(Session s)
    {
        sessions[s.Id] = s;
        Changed?.Invoke(this, new SessionChange(SessionChangeKind.Upserted, s.Id, s.WithoutHistory(), ++revision));
    }

    public void End(string id)
    {
        sessions.Remove(id);
        Changed?.Invoke(this, new SessionChange(SessionChangeKind.Removed, id, null, ++revision));
    }

    // Runs once, after the next snapshot has been taken: a change landing just after it.
    public Action? AfterSnapshot { get; set; }

    public IReadOnlyList<Session> Snapshot()
    {
        IReadOnlyList<Session> snapshot = [.. sessions.Values.OrderByDescending(s => s.UpdatedAt).Select(s => s.WithoutHistory())];
        var after = AfterSnapshot;
        AfterSnapshot = null;
        after?.Invoke();
        return snapshot;
    }
    public Session? Get(string id) => sessions.TryGetValue(id, out var s) ? s : null;
    public SessionList List(string? cursor = null) => new(Snapshot(), revision);
    public SessionSubscription Subscribe() => throw new NotSupportedException();
    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public static readonly DateTimeOffset T0 = new(2026, 10, 29, 19, 0, 0, TimeSpan.Zero);

    public static Session Copilot(string id, string title = "Fixture session", SessionStatus status = SessionStatus.Idle, string? response = null, int? pid = 4242) => new()
    {
        Id = id,
        Agent = AgentKind.Copilot,
        Title = title,
        Cwd = @"C:\work\repo",
        Status = status,
        StatusSince = T0,
        UpdatedAt = T0,
        ProcessId = pid,
        Activity = new Activity { Response = response },
    };
}
