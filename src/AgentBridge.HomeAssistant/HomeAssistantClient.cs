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
}

public sealed record HaState(string EntityId, string State, JsonElement Attributes);

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
                e.TryGetProperty("attributes", out var a) ? a.Clone() : default)
            : null;
}

// Home Assistant's WebSocket API, one command per connection.
internal static class HaWebSocket
{
    const int MaxMessageBytes = 64 * 1024 * 1024;

    public static async Task<JsonElement> RunAsync(WebSocket ws, string token, JsonObject command, CancellationToken cancellationToken)
    {
        using (var hello = await ReceiveAsync(ws, cancellationToken))
        {
            if (Type(hello) != "auth_required") { throw new InvalidDataException($"Home Assistant opened with {Type(hello)} instead of auth_required"); }
        }
        await SendAsync(ws, new JsonObject { ["type"] = "auth", ["access_token"] = token }, cancellationToken);
        using (var auth = await ReceiveAsync(ws, cancellationToken))
        {
            // auth_invalid's message is Home Assistant's and names no secret, but it is not
            // repeated: "the token was refused" is what the log needs.
            if (Type(auth) != "auth_ok") { throw new HaCommandException("auth", Type(auth) ?? "unknown", "Home Assistant refused the token"); }
        }
        command["id"] = 1;
        var type = command["type"]?.GetValue<string>() ?? "?";
        await SendAsync(ws, command, cancellationToken);
        while (true)
        {
            using var message = await ReceiveAsync(ws, cancellationToken);
            var root = message.RootElement;
            if (Type(message) != "result" || !root.TryGetProperty("id", out var id) || id.GetInt32() != 1) { continue; }
            if (root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                return root.TryGetProperty("result", out var result) ? result.Clone() : default;
            }
            var code = root.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var c) ? c.ToString() : "unknown";
            var text = root.TryGetProperty("error", out var error2) && error2.TryGetProperty("message", out var m) ? m.ToString() : "";
            throw new HaCommandException(type, code, text);
        }
    }

    static string? Type(JsonDocument d) => d.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

    static Task SendAsync(WebSocket ws, JsonObject message, CancellationToken cancellationToken) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(HaContract.Serialize(message)), WebSocketMessageType.Text, true, cancellationToken);

    static async Task<JsonDocument> ReceiveAsync(WebSocket ws, CancellationToken cancellationToken)
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
