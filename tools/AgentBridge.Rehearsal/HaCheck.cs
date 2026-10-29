using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentBridge.Rehearsal;

internal sealed record HaExpectations(
    string? Slug,
    bool Online,
    bool Offline,
    int? Sessions,
    bool NoDuplicates,
    IReadOnlyList<string> Entities,
    IReadOnlyList<string> NoEntities,
    IReadOnlyDictionary<string, string> States);

// Reads a disposable Home Assistant's states and entity registry, and checks what the
// rehearsal expects there. Reads only, and only from this machine: design G6 allows no
// development build near the live instance, so anything but a loopback address is refused
// before a request is made. A disposable instance elsewhere is reached through
// `ssh -L 8123:127.0.0.1:8123`.
internal static partial class HaCheck
{
    public static string? Refusal(Uri url)
    {
        if (url.Scheme is not ("http" or "https")) { return "the Home Assistant URL must be http or https"; }
        var loopback = url.IsLoopback || (IPAddress.TryParse(url.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
        return loopback ? null
            : $"{url.Host} is not this machine: the rehearsal reads only a disposable Home Assistant on 127.0.0.1 (design G6). Forward a remote one with ssh -L 8123:127.0.0.1:8123";
    }

    // `_2`, `_3`… on an id whose base also exists: the entity was registered twice, which is
    // what a publisher that does not keep agent-ha-bridge's unique ids and pinning produces.
    // Further Copilot accounts are numbered on purpose (usage_copilot_2) and are not that.
    [GeneratedRegex(@"^(?<base>.+)_(?<n>[2-9]|[1-9][0-9]+)$")]
    private static partial Regex Numbered();

    public static IReadOnlyList<string> Duplicates(IEnumerable<string> registryIds)
    {
        var ids = registryIds.Where(id => id.Contains(".agent_bridge_", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        return [.. ids.Where(id => Numbered().Match(id) is { Success: true } m && ids.Contains(m.Groups["base"].Value) && !m.Groups["base"].Value.EndsWith("_usage_copilot", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];
    }

    public static Outcome Evaluate(JsonElement states, JsonElement registry, HaExpectations x)
    {
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in states.EnumerateArray())
        {
            if (s.TryGetProperty("entity_id", out var id) && id.GetString() is { } eid) { state[eid] = s.TryGetProperty("state", out var v) ? v.GetString() ?? "" : ""; }
        }
        var registered = registry.ValueKind == JsonValueKind.Array
            ? registry.EnumerateArray().Select(r => r.TryGetProperty("entity_id", out var id) ? id.GetString() : null).OfType<string>().ToList()
            : [];
        var problems = new List<string>();
        var seen = new Dictionary<string, string?>();
        void Want(string entity, Func<string, bool> ok, string expected)
        {
            state.TryGetValue(entity, out var got);
            seen[entity] = got;
            if (got is null || !ok(got)) { problems.Add($"{entity} is {got ?? "absent"}, expected {expected}"); }
        }
        if ((x.Online || x.Offline || x.Sessions is not null) && x.Slug is null) { throw new UsageException("--slug is needed to check the machine's entities"); }
        if (x.Online) { Want($"binary_sensor.agent_bridge_{x.Slug}_online", s => s == "on", "on"); }
        if (x.Offline) { Want($"binary_sensor.agent_bridge_{x.Slug}_online", s => s is "off" or "unavailable", "off or unavailable"); }
        if (x.Sessions is { } n) { Want($"sensor.agent_bridge_{x.Slug}_sessions", s => s == n.ToString(), n.ToString()); }
        foreach (var e in x.Entities) { Want(e, s => s != "unavailable", "present and available"); }
        foreach (var (e, v) in x.States) { Want(e, s => s == v, v); }
        foreach (var e in x.NoEntities)
        {
            if (state.ContainsKey(e) || registered.Contains(e)) { problems.Add($"{e} is still there"); }
        }
        var duplicates = x.NoDuplicates ? Duplicates(registered) : [];
        problems.AddRange(duplicates.Select(d => $"{d} duplicates an entity already registered"));
        var data = new { states = seen, duplicates, registered = registered.Count(r => r.Contains(".agent_bridge_", StringComparison.Ordinal)) };
        return problems.Count == 0 ? Outcome.Pass("Home Assistant shows what the step expects", data) : Outcome.Fail(string.Join("; ", problems), data);
    }

    public static async Task<(JsonElement States, JsonElement Registry)> ReadAsync(Uri url, string token, bool registry, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = url, Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.GetAsync("/api/states", ct);
        if (!response.IsSuccessStatusCode) { throw new IOException($"Home Assistant answered /api/states with {(int)response.StatusCode}"); }
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var states = doc.RootElement.Clone();
        var list = registry ? await RegistryAsync(url, token, ct) : default;
        return (states, list);
    }

    static async Task<JsonElement> RegistryAsync(Uri url, string token, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        var wsUrl = new UriBuilder(url) { Scheme = url.Scheme == "https" ? "wss" : "ws", Path = "/api/websocket" }.Uri;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        await ws.ConnectAsync(wsUrl, cts.Token);
        await ReceiveAsync(ws, cts.Token); // auth_required
        await SendAsync(ws, new { type = "auth", access_token = token }, cts.Token);
        var auth = await ReceiveAsync(ws, cts.Token);
        if (auth.GetProperty("type").GetString() != "auth_ok") { throw new IOException("Home Assistant refused the token over the WebSocket"); }
        await SendAsync(ws, new { id = 1, type = "config/entity_registry/list" }, cts.Token);
        while (true)
        {
            var message = await ReceiveAsync(ws, cts.Token);
            if (message.TryGetProperty("id", out var id) && id.GetInt32() == 1)
            {
                if (!message.TryGetProperty("success", out var ok) || !ok.GetBoolean()) { throw new IOException("Home Assistant failed config/entity_registry/list"); }
                return message.GetProperty("result").Clone();
            }
        }
    }

    static Task SendAsync(ClientWebSocket ws, object message, CancellationToken ct) =>
        ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, ct);

    static async Task<JsonElement> ReceiveAsync(ClientWebSocket ws, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(chunk, ct);
            if (result.MessageType == WebSocketMessageType.Close) { throw new IOException("Home Assistant closed the WebSocket"); }
            buffer.Write(chunk, 0, result.Count);
        }
        while (!result.EndOfMessage);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
        return doc.RootElement.Clone();
    }
}
