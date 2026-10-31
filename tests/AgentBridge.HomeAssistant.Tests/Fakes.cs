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
            // Home Assistant writes a message only when it changes something, and last_reported
            // records when it last did; an entity it has stopped listening to records nothing.
            if (!lastPayload.TryGetValue(topic, out var previous) || previous != payload)
            {
                lastPayload[topic] = payload;
                if (!Deaf.Any(u => Topics(u).Contains(topic))) { reportedAt[topic] = (++reports).ToString(System.Globalization.CultureInfo.InvariantCulture); }
            }
            if (retain)
            {
                // When a state topic's value last changed, as an entity's last_changed, on this
                // Home Assistant's clock.
                if (!Retained.TryGetValue(topic, out var was) || was != payload) { changedAt[topic] = Now; }
                if (payload.Length == 0) { Retained.Remove(topic); }
                else { Retained[topic] = payload; }
            }
            if (topic.StartsWith(HaContract.DiscoveryPrefix + "/", StringComparison.Ordinal) && topic.EndsWith("/config", StringComparison.Ordinal))
            {
                if (payload.Length == 0)
                {
                    if (discovered.Remove(topic, out var gone))
                    {
                        Registry.Remove(gone);
                        optimistic.Remove(gone);
                        // Built anew when its config comes again: listening, unless it never will.
                        if (!StaysDeaf) { Deaf.Remove(gone); }
                    }
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

    readonly Dictionary<string, string> lastPayload = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> reportedAt = new(StringComparer.Ordinal);
    long reports;
    // Entities (by unique_id) Home Assistant has stopped applying messages to (agent-ha-bridge #177).
    public HashSet<string> Deaf { get; } = new(StringComparer.Ordinal);
    // Whether one stays deaf when it is built again.
    public bool StaysDeaf { get; set; }

    // The state and attribute topics an entity's config names.
    IEnumerable<string> Topics(string uniqueId)
    {
        if (discovered.FirstOrDefault(p => p.Value == uniqueId).Key is not { } topic || !Retained.TryGetValue(topic, out var json)) { yield break; }
        var config = JsonNode.Parse(json)!.AsObject();
        if (config["state_topic"]?.GetValue<string>() is { } st) { yield return st; }
        if (config["json_attributes_topic"]?.GetValue<string>() is { } at) { yield return at; }
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

    // Notifications sent (any call naming no entity), in order, with the account that sent them.
    public List<(string Service, JsonObject Data)> Notified { get; } = [];
    public List<(string Service, JsonObject Data)> NotifiedNow() { lock (gate) { return [.. Notified]; } }
    public Func<string, bool>? NotifyFailsFor { get; set; }
    // What a notify call throws instead, when not null (a timeout, say).
    public Func<string, Exception?>? NotifyThrows { get; set; }
    readonly System.Threading.Channels.Channel<HaEvent> events = System.Threading.Channels.Channel.CreateUnbounded<HaEvent>();
    public int EventSubscriptions;

    public Task CallServiceAsync(string domain, string service, JsonObject data, CancellationToken cancellationToken = default)
    {
        if (ServiceTarget.Check(data) is { } refused) { throw new InvalidOperationException(refused); }
        if (data["entity_id"] is null)
        {
            if (NotifyThrows?.Invoke($"{domain}.{service}") is { } thrown) { throw thrown; }
            if (NotifyFailsFor?.Invoke($"{domain}.{service}") == true) { throw new HttpRequestException($"{domain}.{service}: Home Assistant answered 400 Bad Request"); }
            lock (gate) { Notified.Add(($"{domain}.{service}", data.DeepClone().AsObject())); }
            return Task.CompletedTask;
        }
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
    readonly Dictionary<string, DateTimeOffset> changedAt = new(StringComparer.Ordinal);

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

    // A phone's tap, as Home Assistant fires it.
    public void Fire(string eventType, JsonObject data, string? userId = "person", string? id = null) =>
        events.Writer.TryWrite(new HaEvent(eventType, JsonSerializer.SerializeToElement(data), userId, id ?? Guid.NewGuid().ToString("N")));

    // Holds a subscription unconfirmed until it is let go (a Home Assistant slow to answer).
    public TaskCompletionSource SubscriptionGate { get; set; } = CompletedGate();
    static TaskCompletionSource CompletedGate() { var t = new TaskCompletionSource(); t.SetResult(); return t; }

    public async IAsyncEnumerable<HaEvent> EventsAsync(string eventType, Action? subscribed = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await SubscriptionGate.Task.WaitAsync(cancellationToken);
        Interlocked.Increment(ref EventSubscriptions);
        subscribed?.Invoke();
        await foreach (var e in events.Reader.ReadAllAsync(cancellationToken))
        {
            if (e.EventType == eventType) { yield return e; }
        }
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
    // What a failing fetch throws: a 500 unless said otherwise (a timeout, say).
    public Exception? ImageError { get; set; }
    public int ImageFetches { get; private set; }

    public string Upload(byte[] bytes, string? id = null)
    {
        id ??= Guid.NewGuid().ToString("N");
        lock (gate) { Images[id] = bytes; }
        return id;
    }

    public Task<byte[]?> DownloadImageAsync(string imageId, long maxBytes, CancellationToken cancellationToken = default)
    {
        if (!HaImageId.IsValid(imageId)) { throw new ArgumentException("not an image id", nameof(imageId)); }
        lock (gate) { ImageFetches++; }
        if (ImageFails?.Invoke(imageId) == true) { throw ImageError ?? new HttpRequestException("GET image: Home Assistant answered 500 Internal Server Error"); }
        lock (gate)
        {
            if (!Images.TryGetValue(imageId, out var bytes)) { return Task.FromResult<byte[]?>(null); }
            if (bytes.Length > maxBytes) { throw new InvalidDataException($"the image is larger than {maxBytes} bytes"); }
            return Task.FromResult<byte[]?>(bytes);
        }
    }

    // A delete that fails, with what it throws (a command timing out on its own, say).
    public Func<string, Exception?>? DeleteFails { get; set; }

    // A delete Home Assistant is slow to answer: it waits for this.
    public Task? DeleteWaits { get; set; }

    public async Task DeleteImageAsync(string imageId, CancellationToken cancellationToken = default)
    {
        if (DeleteWaits is { } wait) { await wait.WaitAsync(cancellationToken); }
        if (DeleteFails?.Invoke(imageId) is { } failed) { throw failed; }
        lock (gate)
        {
            Images.Remove(imageId);
            DeletedImages.Add(imageId);
        }
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
        var stateTopic = config["state_topic"]?.GetValue<string>();
        var state = stateTopic is { } st && Retained.TryGetValue(st, out var v) ? v : "unknown";
        DateTimeOffset? changed = stateTopic is not null && changedAt.TryGetValue(stateTopic, out var at0) ? at0 : null;
        DateTimeOffset? updated = changed;
        if (config["json_attributes_topic"]?.GetValue<string>() is { } attrTopic && changedAt.TryGetValue(attrTopic, out var at1) && (updated is null || at1 > updated)) { updated = at1; }
        // The one value template the contract uses: the reply payload's stamp.
        if (config["value_template"]?.GetValue<string>() == "{{ value_json.at }}" && state != "unknown")
        {
            state = JsonNode.Parse(state)?["at"]?.GetValue<string>() ?? "unknown";
        }
        string? user = null;
        if (optimistic.TryGetValue(uniqueId, out var o)) { (state, user) = o; }
        var attributes = config["json_attributes_topic"]?.GetValue<string>() is { } at && Retained.TryGetValue(at, out var a) ? JsonNode.Parse(a)!.AsObject() : [];
        if (config["options"] is JsonArray options) { attributes["options"] = options.DeepClone(); }
        var reported = Topics(uniqueId).Select(t => reportedAt.TryGetValue(t, out var r) ? long.Parse(r, System.Globalization.CultureInfo.InvariantCulture) : 0).DefaultIfEmpty(0).Max();
        return new HaState(entityId, state, JsonDocument.Parse(attributes.ToJsonString()).RootElement.Clone(), user, changed, updated,
            reported == 0 ? null : reported.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public Task<IReadOnlyList<HaRegistryEntry>> ListEntityRegistryAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            RegistryLists++;
            return Task.FromResult<IReadOnlyList<HaRegistryEntry>>([.. Registry.Select(p => new HaRegistryEntry(p.Value, p.Key)),
                .. Helpers.Keys.Select(id => new HaRegistryEntry("input_boolean." + id, id, HelperNames.GetValueOrDefault("input_boolean." + id)))]);
        }
    }

    // Home Assistant's input_boolean helpers in storage (id -> name), what was asked of them, and
    // the names the registry gives entities.
    public Dictionary<string, string> Helpers { get; } = new(StringComparer.Ordinal);
    public List<string> HelperCalls { get; } = [];
    public Dictionary<string, string> HelperNames { get; } = new(StringComparer.Ordinal);
    public Exception? HelperListFails { get; set; }
    // A list that answers empty although helpers are stored (Home Assistant still starting).
    public bool HelperListEmpty { get; set; }
    // Whether a helper created has a state at once.
    public bool HelperStateless { get; set; }
    // The id Home Assistant gives a helper it creates, when not the one its name makes.
    public Func<string, string>? CreateAs { get; set; }

    // A Home Assistant restart: the optimistic entities (no state topic) come back with no value.
    public void ForgetOptimistic() { lock (gate) { optimistic.Clear(); } }

    public Task<IReadOnlyList<string>> ListInputBooleansAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            HelperCalls.Add("list");
            if (HelperListFails is { } e) { throw e; }
            return Task.FromResult<IReadOnlyList<string>>(HelperListEmpty ? [] : [.. Helpers.Keys]);
        }
    }

    public Task<string> CreateInputBooleanAsync(string name, string icon, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            HelperCalls.Add($"create {name} {icon}");
            var id = CreateAs?.Invoke(name) ?? HaContract.HelperIdOf(name);
            if (Helpers.ContainsKey(id)) { for (var i = 2; ; i++) { if (!Helpers.ContainsKey($"{id}_{i}")) { id = $"{id}_{i}"; break; } } }
            Helpers[id] = name;
            if (!HelperStateless) { States["input_boolean." + id] = new HaState("input_boolean." + id, "off", JsonDocument.Parse("{}").RootElement.Clone()); }
            return Task.FromResult(id);
        }
    }

    public Task DeleteInputBooleanAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            HelperCalls.Add($"delete {id}");
            Helpers.Remove(id);
            States.Remove("input_boolean." + id);
        }
        return Task.CompletedTask;
    }

    public Task SetEntityNameAsync(string entityId, string name, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            HelperCalls.Add($"name {entityId} {name}");
            HelperNames[entityId] = name;
        }
        return Task.CompletedTask;
    }

    // A helper as Home Assistant already holds it, with its value.
    public void Helper(string id, string name, string state = "off")
    {
        lock (gate)
        {
            Helpers[id] = name;
            States["input_boolean." + id] = new HaState("input_boolean." + id, state, JsonDocument.Parse("{}").RootElement.Clone());
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
