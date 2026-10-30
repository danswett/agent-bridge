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
    // A service call (domain.service, entity) Home Assistant fails.
    public Func<string, string, bool>? ServiceFailsFor { get; set; }
    // Something that happens on the card just before a topic is published.
    public Action<string>? BeforePublish { get; set; }
    // An entity whose state Home Assistant fails to return.
    public Func<string, bool>? GetFailsFor { get; set; }
    public int RegistryLists { get; private set; }

    public Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken = default)
    {
        BeforePublish?.Invoke(topic);
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
                    if (discovered.Remove(topic, out var gone)) { Registry.Remove(gone); optimistic.Remove(gone); }
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
        if (GetFailsFor?.Invoke(entityId) == true) { throw new HttpRequestException($"{entityId}: Home Assistant answered 502 Bad Gateway"); }
        lock (gate) { return Task.FromResult(States.TryGetValue(entityId, out var s) ? s : Derived(entityId)); }
    }

    // States Home Assistant keeps for optimistic MQTT entities (selects, buttons, text with no
    // state topic), by unique_id so a rename keeps them.
    readonly Dictionary<string, (string State, string? UserId)> optimistic = new(StringComparer.Ordinal);
    public List<(string Service, string EntityId, string Value, string? UserId)> Services { get; } = [];
    // The account the connector's token belongs to, as Home Assistant records it.
    public string TokenUser { get; set; } = "token-user";
    readonly System.Threading.Channels.Channel<HaTrigger> triggers = System.Threading.Channels.Channel.CreateUnbounded<HaTrigger>();
    public List<IReadOnlyCollection<string>> Watches { get; } = [];

    public Task CallServiceAsync(string domain, string service, JsonObject data, CancellationToken cancellationToken = default)
    {
        if (ServiceTarget.Check(data) is { } refused) { throw new InvalidOperationException(refused); }
        var entityId = data["entity_id"]!.GetValue<string>();
        if (ServiceFailsFor?.Invoke($"{domain}.{service}", entityId) == true) { throw new HttpRequestException($"{domain}.{service}: Home Assistant answered 500 Internal Server Error"); }
        var value = data["option"]?.GetValue<string>() ?? data["value"]?.GetValue<string>() ?? "";
        lock (gate) { Services.Add(($"{domain}.{service}", entityId, value, TokenUser)); }
        Apply($"{domain}.{service}", entityId, value, TokenUser);
        return Task.CompletedTask;
    }

    HaTrigger Apply(string service, string entityId, string value, string? userId)
    {
        lock (gate)
        {
            var uniqueId = Registry.FirstOrDefault(p => p.Value == entityId).Key
                ?? throw new HttpRequestException($"{service}: Home Assistant answered 400 Bad Request (no entity {entityId})");
            switch (service)
            {
                case "select.select_option":
                    var options = Config(uniqueId)["options"]!.AsArray().Select(o => o!.GetValue<string>());
                    if (!options.Contains(value)) { throw new HttpRequestException($"Option '{value}' is not valid for entity {entityId}"); }
                    break;
                case "button.press":
                    value = Now.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
                    Now = Now.AddSeconds(1);
                    break;
            }
            optimistic[uniqueId] = (value, userId);
            return new HaTrigger(entityId, Derived(entityId)!);
        }
    }

    JsonObject Config(string uniqueId) => JsonNode.Parse(Retained[discovered.First(p => p.Value == uniqueId).Key])!.AsObject();

    // The clock a button press is stamped with.
    public DateTimeOffset Now { get; set; } = new(2026, 10, 29, 20, 0, 0, TimeSpan.Zero);

    // What a person (or an agent) does on the card, and the change Home Assistant reports for it.
    public HaTrigger Press(string entityId, string? userId = "person") => Apply("button.press", entityId, "", userId);
    public HaTrigger Choose(string entityId, string option, string? userId = "person") => Apply("select.select_option", entityId, option, userId);
    public HaTrigger Type(string entityId, string text, string? userId = "person") => Apply("text.set_value", entityId, text, userId);

    // The reply card's Send: a retained publish to the session's reply-payload topic.
    public HaTrigger SendFromCard(string node, string text, string? decisionId = null, string? driver = null, string? at = null,
        JsonArray? images = null, JsonArray? files = null)
    {
        var payload = new JsonObject { ["at"] = at ?? $"{Now:O}", ["text"] = text };
        if (images is not null) { payload["images"] = images; }
        if (files is not null) { payload["files"] = files; }
        if (decisionId is not null) { payload["decision_id"] = decisionId; }
        if (driver is not null) { payload["driver"] = driver; }
        Now = Now.AddSeconds(1);
        PublishAsync(HaContract.Topics(node).ReplyPayload, HaContract.Serialize(payload), true).GetAwaiter().GetResult();
        var entityId = SessionEntities.ReplyPayload(node);
        lock (gate) { return new HaTrigger(entityId, Derived(entityId)!); }
    }

    public async IAsyncEnumerable<HaTrigger> WatchAsync(IReadOnlyCollection<string> entityIds, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        lock (gate) { Watches.Add(entityIds); }
        await foreach (var t in triggers.Reader.ReadAllAsync(cancellationToken))
        {
            if (entityIds.Contains(t.EntityId)) { yield return t; }
        }
    }

    // Home Assistant's image store, as the reply card's uploads fill it.
    public Dictionary<string, byte[]> Images { get; } = new(StringComparer.Ordinal);
    public List<string> DeletedImages { get; } = [];
    public Func<string, bool>? ImageFails { get; set; }

    public string Upload(byte[] bytes, string? id = null)
    {
        id ??= Guid.NewGuid().ToString("N");
        lock (gate) { Images[id] = bytes; }
        return id;
    }

    public Task<byte[]?> DownloadImageAsync(string imageId, long maxBytes, CancellationToken cancellationToken = default)
    {
        if (!HaImageId.IsValid(imageId)) { throw new ArgumentException("not an image id", nameof(imageId)); }
        if (ImageFails?.Invoke(imageId) == true) { throw new HttpRequestException("GET image: Home Assistant answered 500 Internal Server Error"); }
        lock (gate)
        {
            if (!Images.TryGetValue(imageId, out var bytes)) { return Task.FromResult<byte[]?>(null); }
            if (bytes.Length > maxBytes) { throw new InvalidDataException($"the image is larger than {maxBytes} bytes"); }
            return Task.FromResult<byte[]?>(bytes);
        }
    }

    public Task DeleteImageAsync(string imageId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            Images.Remove(imageId);
            DeletedImages.Add(imageId);
        }
        return Task.CompletedTask;
    }

    // Hands a change to whatever is watching, as Home Assistant's trigger subscription would.
    public void Trigger(HaTrigger trigger) => triggers.Writer.TryWrite(trigger);

    public Task<IReadOnlyList<HaMessage>> ReadTopicsAsync(IReadOnlyList<string> filters, TimeSpan window, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            return Task.FromResult<IReadOnlyList<HaMessage>>([.. Retained.Where(r => filters.Any(f => Matches(f, r.Key))).Select(r => new HaMessage(r.Key, r.Value, true))]);
        }
    }

    // MQTT topic filter matching: + is one level, # the rest.
    static bool Matches(string filter, string topic)
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
    // attribute topics its discovery config names, or what an optimistic one was last set to.
    HaState? Derived(string entityId)
    {
        var uniqueId = Registry.FirstOrDefault(p => p.Value == entityId).Key;
        if (uniqueId is null) { return null; }
        var topic = discovered.First(p => p.Value == uniqueId).Key;
        var config = JsonNode.Parse(Retained[topic])!.AsObject();
        var state = config["state_topic"]?.GetValue<string>() is { } st && Retained.TryGetValue(st, out var v) ? v : "unknown";
        // The one value template the contract uses: the reply payload's stamp.
        if (config["value_template"]?.GetValue<string>() == "{{ value_json.at }}" && state != "unknown")
        {
            state = JsonNode.Parse(state)?["at"]?.GetValue<string>() ?? "unknown";
        }
        string? user = null;
        if (optimistic.TryGetValue(uniqueId, out var o)) { (state, user) = o; }
        var attributes = config["json_attributes_topic"]?.GetValue<string>() is { } at && Retained.TryGetValue(at, out var a) ? JsonNode.Parse(a)!.AsObject() : [];
        if (config["options"] is JsonArray options) { attributes["options"] = options.DeepClone(); }
        return new HaState(entityId, state, JsonDocument.Parse(attributes.ToJsonString()).RootElement.Clone(), user);
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
        lock (sessions) { sessions[s.Id] = s; }
        Changed?.Invoke(this, new SessionChange(SessionChangeKind.Upserted, s.Id, s.WithoutHistory(), ++revision));
    }

    public void End(string id)
    {
        lock (sessions) { sessions.Remove(id); }
        Changed?.Invoke(this, new SessionChange(SessionChangeKind.Removed, id, null, ++revision));
    }

    // Runs once, after the next snapshot has been taken: a change landing just after it.
    public Action? AfterSnapshot { get; set; }

    public IReadOnlyList<Session> Snapshot()
    {
        IReadOnlyList<Session> snapshot;
        lock (sessions) { snapshot = [.. sessions.Values.OrderByDescending(s => s.UpdatedAt).Select(s => s.WithoutHistory())]; }
        var after = AfterSnapshot;
        AfterSnapshot = null;
        after?.Invoke();
        return snapshot;
    }
    public Session? Get(string id) { lock (sessions) { return sessions.TryGetValue(id, out var s) ? s : null; } }
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
