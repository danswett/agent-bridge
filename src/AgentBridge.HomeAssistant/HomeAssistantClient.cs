using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBridge.HomeAssistant;

// Everything the connector asks of Home Assistant. Like agent-ha-bridge's daemon, it never
// connects to the MQTT broker: messages go through Home Assistant's own mqtt.publish service,
// and the entity registry and dashboard are read and pinned over its WebSocket API. So the
// only credential is a Home Assistant token, and a machine needs no broker account.
public interface IHomeAssistant
{
    // mqtt.publish, at QoS 1. An empty retained payload clears a retained topic.
    Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken = default);

    // GET /api/states/<id>, or null when Home Assistant says there is no such entity.
    Task<HaState?> GetStateAsync(string entityId, CancellationToken cancellationToken = default);

    // GET /api/states: every entity. Used once at start, for the orphan sweep.
    Task<IReadOnlyList<HaState>> GetStatesAsync(CancellationToken cancellationToken = default);

    // config/entity_registry/list.
    Task<IReadOnlyList<HaRegistryEntry>> ListEntityRegistryAsync(CancellationToken cancellationToken = default);

    // config/entity_registry/update with new_entity_id: one entity, by its current id.
    Task RenameEntityAsync(string entityId, string newEntityId, CancellationToken cancellationToken = default);

    // lovelace/config for one dashboard: its JSON as served, or NotFound when there is none.
    Task<HaDashboard> ReadDashboardAsync(string urlPath, CancellationToken cancellationToken = default);

    // One service call naming at most one entity (ServiceTarget): select.select_option and
    // text.set_value on a session's own controls.
    Task CallServiceAsync(string domain, string service, JsonObject data, CancellationToken cancellationToken = default);

    // A server-side state trigger on exactly these entities (subscribe_trigger), as the daemon's
    // Wait-CopilotHaStateChange: one message per change, nothing for the rest of the house.
    // Ends when the connection does; the caller watches again.
    IAsyncEnumerable<HaTrigger> WatchAsync(IReadOnlyCollection<string> entityIds, CancellationToken cancellationToken = default);

    // Every message the broker holds or delivers on these topic filters within the window, read
    // through Home Assistant's mqtt/subscribe. Read-only: used by the shadow comparison.
    Task<IReadOnlyList<HaMessage>> ReadTopicsAsync(IReadOnlyList<string> filters, TimeSpan window, CancellationToken cancellationToken = default);
}

// A state as Home Assistant reports it, with the account behind its last change (context.user_id)
// when there was one: a press from the dashboard carries the person's, one through an agent's
// token the agent's.
public sealed record HaState(string EntityId, string State, JsonElement Attributes, string? UserId = null, string? LastChanged = null);

// One change a watch reported. A removed entity is not a change (Get-BridgeStateTriggerHit).
public sealed record HaTrigger(string EntityId, HaState State);

public sealed record HaRegistryEntry(string EntityId, string? UniqueId);

// A dashboard as read: its config as JSON text, or that Home Assistant has none at that path.
public sealed record HaDashboard(bool Found, string? Json)
{
    public static readonly HaDashboard NotFound = new(false, null);
}

public sealed class HaCommandException(string type, string code, string message) : Exception($"{type} failed: {code}: {message}")
{
    public string Code { get; } = code;
}

// The service-call guard from agent-ha-bridge (Assert-HomeAssistantServiceTarget). On
// 2026-10-02 a call meant to press one session's stop button carried thousands of entity ids
// after a selection collapsed, and Home Assistant pressed 166 buttons across the house. Nothing
// this connector does legitimately names more than one entity, so that is the only shape a
// service call may have: one scalar entity_id, or none. area_id, device_id, label_id and
// floor_id are refused outright, because Home Assistant expands each to every match.
public static class ServiceTarget
{
    static readonly string[] Expanding = ["device_id", "area_id", "label_id", "floor_id"];

    // Null when the call is allowed; otherwise why not.
    public static string? Check(JsonObject data)
    {
        var containers = new List<JsonObject> { data };
        if (data.TryGetPropertyValue("target", out var target))
        {
            if (target is JsonObject t) { containers.Add(t); }
            else if (target is not null) { return "the target is not an object, so what it selects cannot be checked"; }
        }
        var named = 0;
        foreach (var container in containers)
        {
            foreach (var key in Expanding)
            {
                if (container.ContainsKey(key)) { return $"it selects by {key}, which Home Assistant expands to every matching entity; name one entity_id"; }
            }
            if (!container.TryGetPropertyValue("entity_id", out var value)) { continue; }
            named++;
            if (value is JsonArray list)
            {
                if (list.Count != 1) { return $"it names {list.Count} entities; a service call is not a way to fan out"; }
                value = list[0];
            }
            if (value is not JsonValue v || !v.TryGetValue<string>(out var id)) { return "its entity_id is not a single string"; }
            id = id.Trim();
            if (id.Length == 0) { return "its entity_id is empty"; }
            if (id == "all") { return "entity_id 'all' targets every entity in the domain"; }
            if (id.Any(c => c == ',' || char.IsWhiteSpace(c))) { return $"it passes a list as entity_id ('{id}')"; }
            var dot = id.IndexOf('.');
            if (dot <= 0 || dot == id.Length - 1 || id.IndexOf('.', dot + 1) >= 0) { return $"its entity_id is malformed ('{id}')"; }
        }
        return named > 1 ? "it names an entity_id in both the body and the target, which selects both" : null;
    }
}

// Home Assistant over REST (service calls and states) and its WebSocket API (registry and
// dashboard). A WebSocket command opens its own connection, authenticates, runs and closes,
// as the daemon's Invoke-CopilotHaWebSocket does: these are rare (pinning ids, the retirement
// check), and a held connection would be one more thing to keep alive.
public sealed class HomeAssistantClient : IHomeAssistant
{
    readonly Uri baseUrl;
    readonly string token;
    readonly HttpClient http;
    readonly Func<Uri, CancellationToken, Task<WebSocket>> connect;

    public HomeAssistantClient(Uri baseUrl, string token, HttpClient http, Func<Uri, CancellationToken, Task<WebSocket>>? connect = null)
    {
        this.baseUrl = new Uri(baseUrl.ToString().TrimEnd('/') + "/");
        this.token = token;
        this.http = http;
        this.connect = connect ?? (async (uri, ct) =>
        {
            var ws = new ClientWebSocket();
            await ws.ConnectAsync(uri, ct);
            return ws;
        });
    }

    public Uri WebSocketUri => new UriBuilder(new Uri(baseUrl, "api/websocket"))
    {
        Scheme = baseUrl.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
    }.Uri;

    public Task PublishAsync(string topic, string payload, bool retain, CancellationToken cancellationToken = default) =>
        CallServiceAsync("mqtt", "publish", new JsonObject { ["topic"] = topic, ["payload"] = payload, ["retain"] = retain, ["qos"] = 1 }, cancellationToken);

    // Every service call goes through the guard, before anything reaches the wire.
    public async Task CallServiceAsync(string domain, string service, JsonObject data, CancellationToken cancellationToken = default)
    {
        if (ServiceTarget.Check(data) is { } refused) { throw new InvalidOperationException($"refused a {domain}.{service} call: {refused}"); }
        using var request = Request(HttpMethod.Post, $"api/services/{Uri.EscapeDataString(domain)}/{Uri.EscapeDataString(service)}");
        request.Content = new StringContent(HaContract.Serialize(data), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureAsync(response, $"{domain}.{service}", cancellationToken);
    }

    public async Task<HaState?> GetStateAsync(string entityId, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, "api/states/" + Uri.EscapeDataString(entityId));
        using var response = await http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) { return null; }
        await EnsureAsync(response, "GET " + entityId, cancellationToken);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return State(doc.RootElement);
    }

    public async Task<IReadOnlyList<HaState>> GetStatesAsync(CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, "api/states");
        using var response = await http.SendAsync(request, cancellationToken);
        await EnsureAsync(response, "GET /api/states", cancellationToken);
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) { throw new InvalidDataException("/api/states is not a list"); }
        return [.. doc.RootElement.EnumerateArray().Select(State).OfType<HaState>()];
    }

    public async Task<IReadOnlyList<HaRegistryEntry>> ListEntityRegistryAsync(CancellationToken cancellationToken = default)
    {
        var result = await CommandAsync(new JsonObject { ["type"] = "config/entity_registry/list" }, cancellationToken);
        if (result.ValueKind != JsonValueKind.Array) { throw new InvalidDataException("the entity registry is not a list"); }
        var entries = new List<HaRegistryEntry>();
        foreach (var e in result.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("entity_id", out var id) || id.ValueKind != JsonValueKind.String) { continue; }
            entries.Add(new HaRegistryEntry(id.GetString()!, e.TryGetProperty("unique_id", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null));
        }
        return entries;
    }

    public Task RenameEntityAsync(string entityId, string newEntityId, CancellationToken cancellationToken = default) =>
        CommandAsync(new JsonObject { ["type"] = "config/entity_registry/update", ["entity_id"] = entityId, ["new_entity_id"] = newEntityId }, cancellationToken);

    public async Task<HaDashboard> ReadDashboardAsync(string urlPath, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await CommandAsync(new JsonObject { ["type"] = "lovelace/config", ["url_path"] = urlPath }, cancellationToken);
            if (result.ValueKind != JsonValueKind.Object) { throw new InvalidDataException("the dashboard config is not an object"); }
            return new HaDashboard(true, result.GetRawText());
        }
        // Home Assistant's answer for a path with no dashboard, or one with no saved config.
        catch (HaCommandException e) when (e.Code == "config_not_found") { return HaDashboard.NotFound; }
    }

    // One WebSocket command: auth_required, auth, auth_ok, then the command and its result.
    async Task<JsonElement> CommandAsync(JsonObject command, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var ws = await connect(WebSocketUri, timeout.Token);
        try
        {
            return await HaWebSocket.RunAsync(ws, token, command, timeout.Token);
        }
        finally
        {
            if (ws.State == WebSocketState.Open)
            {
                // The close frame is sent, not waited on: the command is done, and a Home
                // Assistant that never answers a close must not hold the connector's pass.
                try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
                catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException) { }
            }
        }
    }

    HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(baseUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    // The status only: a response body could echo what was sent, and that is not for the log.
    static Task EnsureAsync(HttpResponseMessage response, string what, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return response.IsSuccessStatusCode ? Task.CompletedTask
            : throw new HttpRequestException($"{what}: Home Assistant answered {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
    }

    static HaState? State(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("entity_id", out var id) && id.ValueKind == JsonValueKind.String
            ? new HaState(id.GetString()!, e.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "",
                e.TryGetProperty("attributes", out var a) ? a.Clone() : default, UserId(e), LastChanged(e))
            : null;

    // Get-BridgeStateUserId: context.user_id, read step by step because any of it may be absent.
    static string? UserId(JsonElement state) =>
        state.TryGetProperty("context", out var c) && c.ValueKind == JsonValueKind.Object
        && c.TryGetProperty("user_id", out var u) && u.ValueKind == JsonValueKind.String && u.GetString() is { Length: > 0 } user
            ? user : null;

    // When the state last changed: what tells a second tap of one option from a re-read of the first.
    static string? LastChanged(JsonElement state) =>
        state.TryGetProperty("last_changed", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;

    public async IAsyncEnumerable<HaTrigger> WatchAsync(IReadOnlyCollection<string> entityIds, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (entityIds.Count == 0) { yield break; }
        using var ws = await OpenAsync(cancellationToken);
        try
        {
            await HaWebSocket.SubscribeAsync(ws, new JsonObject
            {
                ["type"] = "subscribe_trigger",
                ["trigger"] = new JsonObject { ["platform"] = "state", ["entity_id"] = new JsonArray([.. entityIds.Select(e => (JsonNode)e)]) },
            }, cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                using var message = await HaWebSocket.ReceiveAsync(ws, cancellationToken);
                if (Hit(message.RootElement) is { } hit) { yield return hit; }
            }
        }
        finally { await CloseAsync(ws); }
    }

    // Get-BridgeStateTriggerHit: the change one subscribe_trigger message describes, or null. An
    // entity being removed arrives as a hit with no to_state, and is never a press.
    internal static HaTrigger? Hit(JsonElement message)
    {
        if (!message.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "event") { return null; }
        if (!message.TryGetProperty("event", out var e) || e.ValueKind != JsonValueKind.Object
            || !e.TryGetProperty("variables", out var v) || v.ValueKind != JsonValueKind.Object
            || !v.TryGetProperty("trigger", out var t) || t.ValueKind != JsonValueKind.Object
            || !t.TryGetProperty("entity_id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())
            || !t.TryGetProperty("to_state", out var to) || to.ValueKind != JsonValueKind.Object
            || !to.TryGetProperty("state", out var s) || s.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        var state = s.GetString()!;
        if (state is "" or "unknown" or "unavailable") { return null; }
        return new HaTrigger(id.GetString()!, new HaState(id.GetString()!, state, to.TryGetProperty("attributes", out var a) ? a.Clone() : default, UserId(to), LastChanged(to)));
    }

    public async Task<IReadOnlyList<HaMessage>> ReadTopicsAsync(IReadOnlyList<string> filters, TimeSpan window, CancellationToken cancellationToken = default)
    {
        var messages = new List<HaMessage>();
        if (filters.Count == 0) { return messages; }
        // A retained message for an early filter can arrive while a later one is still being
        // subscribed; it is collected there too, or the shadow would call the topic missing.
        void Collect(JsonElement root)
        {
            if (root.TryGetProperty("type", out var type) && type.GetString() == "event"
                && root.TryGetProperty("event", out var e) && e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("topic", out var topic) && topic.ValueKind == JsonValueKind.String)
            {
                var payload = e.TryGetProperty("payload", out var p) ? p.ValueKind == JsonValueKind.String ? p.GetString()! : p.GetRawText() : "";
                var retain = e.TryGetProperty("retain", out var r) && r.ValueKind == JsonValueKind.True;
                messages.Add(new HaMessage(topic.GetString()!, payload, retain));
            }
        }
        using var ws = await OpenAsync(cancellationToken);
        try
        {
            for (var i = 0; i < filters.Count; i++)
            {
                await HaWebSocket.SubscribeAsync(ws, new JsonObject { ["type"] = "mqtt/subscribe", ["topic"] = filters[i] }, cancellationToken, id: i + 1, other: Collect);
            }
            using var until = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            until.CancelAfter(window);
            while (true)
            {
                JsonDocument message;
                try { message = await HaWebSocket.ReceiveAsync(ws, until.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { break; }
                using (message) { Collect(message.RootElement); }
            }
        }
        finally { await CloseAsync(ws); }
        return messages;
    }

    async Task<WebSocket> OpenAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var ws = await connect(WebSocketUri, timeout.Token);
        try
        {
            await HaWebSocket.AuthenticateAsync(ws, token, timeout.Token);
            return ws;
        }
        catch
        {
            ws.Dispose();
            throw;
        }
    }

    static async Task CloseAsync(WebSocket ws)
    {
        if (ws.State != WebSocketState.Open) { return; }
        try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); }
        catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException) { }
    }
}

// Home Assistant's WebSocket API: one command per connection, or one long subscription.
internal static class HaWebSocket
{
    const int MaxMessageBytes = 64 * 1024 * 1024;

    public static async Task AuthenticateAsync(WebSocket ws, string token, CancellationToken cancellationToken)
    {
        using (var hello = await ReceiveAsync(ws, cancellationToken))
        {
            if (Type(hello) != "auth_required") { throw new InvalidDataException($"Home Assistant opened with {Type(hello)} instead of auth_required"); }
        }
        await SendAsync(ws, new JsonObject { ["type"] = "auth", ["access_token"] = token }, cancellationToken);
        using var auth = await ReceiveAsync(ws, cancellationToken);
        // auth_invalid's message is Home Assistant's and names no secret, but it is not
        // repeated: "the token was refused" is what the log needs.
        if (Type(auth) != "auth_ok") { throw new HaCommandException("auth", Type(auth) ?? "unknown", "Home Assistant refused the token"); }
    }

    // Sends a subscription and waits for Home Assistant to accept it. Anything else that arrives
    // meanwhile (an earlier subscription's events) goes to other, not away.
    public static async Task SubscribeAsync(WebSocket ws, JsonObject command, CancellationToken cancellationToken, int id = 1, Action<JsonElement>? other = null)
    {
        command["id"] = id;
        var type = command["type"]?.GetValue<string>() ?? "?";
        await SendAsync(ws, command, cancellationToken);
        while (true)
        {
            using var message = await ReceiveAsync(ws, cancellationToken);
            if (Result(message.RootElement, id, type) is not null) { return; }
            other?.Invoke(message.RootElement);
        }
    }

    public static async Task<JsonElement> RunAsync(WebSocket ws, string token, JsonObject command, CancellationToken cancellationToken)
    {
        await AuthenticateAsync(ws, token, cancellationToken);
        command["id"] = 1;
        var type = command["type"]?.GetValue<string>() ?? "?";
        await SendAsync(ws, command, cancellationToken);
        while (true)
        {
            using var message = await ReceiveAsync(ws, cancellationToken);
            if (Result(message.RootElement, 1, type) is { } result) { return result.ValueKind == JsonValueKind.Undefined ? default : result.Clone(); }
        }
    }

    // The result for command id, null for any other message; a failure throws.
    static JsonElement? Result(JsonElement root, int id, string type)
    {
        if (!root.TryGetProperty("type", out var t) || t.GetString() != "result" || !root.TryGetProperty("id", out var i) || i.GetInt32() != id) { return null; }
        if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True)
        {
            return root.TryGetProperty("result", out var result) ? result : default(JsonElement);
        }
        var code = root.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var c) ? c.ToString() : "unknown";
        var text = root.TryGetProperty("error", out var error2) && error2.TryGetProperty("message", out var m) ? m.ToString() : "";
        throw new HaCommandException(type, code, text);
    }

    static string? Type(JsonDocument d) => d.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

    static Task SendAsync(WebSocket ws, JsonObject message, CancellationToken cancellationToken) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(HaContract.Serialize(message)), WebSocketMessageType.Text, true, cancellationToken);

    internal static async Task<JsonDocument> ReceiveAsync(WebSocket ws, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, cancellationToken);
            if (r.MessageType == WebSocketMessageType.Close) { throw new WebSocketException("Home Assistant closed the connection"); }
            stream.Write(buffer, 0, r.Count);
            if (stream.Length > MaxMessageBytes) { throw new InvalidDataException("a Home Assistant message was too large"); }
            if (r.EndOfMessage) { break; }
        }
        stream.Position = 0;
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }
}
