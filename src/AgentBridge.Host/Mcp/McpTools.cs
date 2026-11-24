using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBridge.Host.Mcp;

// What the tools need of a host: one request over its pipe. McpHost is the real one; tests have fakes.
public interface IMcpCalls
{
    Task<JsonElement> CallAsync(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default);
}

// The tools, with agent-ha-bridge's names and the results mcp/src/tools.mjs gives, over one host's pipe. Arguments arrive already
// validated (the server does that, PR7b); nothing here widens what the host allows, and no tool reaches another machine.
public static class McpTools
{
    static readonly string[] LaunchSettings = ["agent", "workspace", "profile", "resume", "model", "effort", "context", "permissions", "role"];
    const string DefaultWorkerRole = "default";
    const int Remembered = 200;

    static JsonNode? Node(JsonElement element) => element.ValueKind is JsonValueKind.Undefined ? null : JsonNode.Parse(element.GetRawText());

    // What a reply is answered against: changes whenever the session's visible turn changes (not statusSince, which moves with every
    // status change without a new turn).
    public static string TurnMarker(JsonNode? session)
    {
        var turn = session?["activity"]?["turnCount"];
        var count = turn is JsonValue v && v.TryGetValue<string>(out var s) ? s : turn?.ToJsonString() ?? "";
        var response = session?["activity"]?["response"] is JsonValue r && r.TryGetValue<string>(out var text) ? text : "";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{count}|{response}")), 0, 8).ToLowerInvariant();
    }

    // The default list: status and metadata without the working folder (a host path). The path and the latest answer and question come
    // only with content.
    static JsonObject Typed(JsonNode session, bool content)
    {
        var result = new JsonObject();
        string[] keys = ["id", "agent", "title", "repository", "branch", "model", "status", "statusSince", "startedAt", "updatedAt"];
        foreach (var key in content ? [.. keys, "cwd", "activity", "question"] : keys)
        {
            if (session.AsObject().TryGetPropertyValue(key, out var value)) { result[key] = value?.DeepClone(); }
        }
        return result;
    }

    public static async Task<JsonObject> ListSessionsAsync(IMcpCalls host, bool content, CancellationToken ct = default)
    {
        var sessions = new JsonArray();
        string? cursor = null;
        var held = false;
        do
        {
            var page = Node(await host.CallAsync("sessions.list", cursor is null ? new { } : new { cursor }, ct: ct))?.AsObject() ?? [];
            foreach (var session in page["sessions"]?.AsArray() ?? []) { if (session is not null) { sessions.Add(Typed(session, content)); } }
            held |= page["discoveryHeld"] is JsonValue h && h.TryGetValue<bool>(out var b) && b;
            cursor = page["next"] is JsonValue n && n.TryGetValue<string>(out var next) && next.Length > 0 ? next : null;
        } while (cursor is not null && sessions.Count < 500);
        var result = new JsonObject { ["sessions"] = sessions };
        if (held) { result["discoveryHeld"] = true; }
        return result;
    }

    // Reads one session. With since (what reply returned) the answer is done only once the turn marker has moved and the session is no
    // longer working, so a stale answer is never handed back as the reply.
    public static async Task<JsonObject> ReadSessionAsync(IMcpCalls host, string id, string? since, double waitSeconds, Func<TimeSpan, Task>? sleep = null, CancellationToken ct = default)
    {
        sleep ??= d => Task.Delay(d, ct);
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(0, waitSeconds));
        while (true)
        {
            var session = Node(await host.CallAsync("session.get", new { id }, ct: ct));
            if (session is not JsonObject s || s["missing"] is not null) { return new JsonObject { ["id"] = id, ["found"] = false, ["done"] = false }; }
            var moved = string.IsNullOrEmpty(since) || TurnMarker(s) != since;
            var status = s["status"] is JsonValue st && st.TryGetValue<string>(out var sv) ? sv : null;
            var done = moved && status != "working";
            if (done || DateTime.UtcNow >= deadline)
            {
                var result = new JsonObject
                {
                    ["id"] = id, ["found"] = true, ["done"] = done, ["status"] = s["status"]?.DeepClone(), ["statusSince"] = s["statusSince"]?.DeepClone(),
                    ["response"] = moved && s["activity"]?["response"] is JsonNode resp ? resp.DeepClone() : "",
                };
                if (s["activity"]?["responseTruncated"] is JsonValue t && t.TryGetValue<bool>(out var tr) && tr) { result["responseTruncated"] = true; }
                if (s["question"] is { } q && !(q is JsonValue qv && qv.TryGetValue<string>(out var qs) && qs.Length == 0)) { result["question"] = q.DeepClone(); }
                result["since"] = TurnMarker(s);
                return result;
            }
            await sleep(TimeSpan.FromMilliseconds(Math.Min(1000, Math.Max(50, (deadline - DateTime.UtcNow).TotalMilliseconds))));
        }
    }

    public static async Task<JsonObject> ReplyAsync(IMcpCalls host, string id, string text, CancellationToken ct = default)
    {
        var before = Node(await host.CallAsync("session.get", new { id }, ct: ct));
        if (before is not JsonObject b || b["missing"] is not null) { return new JsonObject { ["id"] = id, ["outcome"] = "refused", ["reason"] = "No such session." }; }
        var sent = Encoding.UTF8.GetByteCount(text);
        var since = TurnMarker(b);
        JsonObject? result;
        try { result = Node(await host.CallAsync("session.reply", new { id, request = Guid.NewGuid().ToString(), text }, ct: ct))?.AsObject(); }
        catch (McpToolException e)
        {
            // A timeout leaves the reply possibly delivered: say so, with since, rather than throwing.
            return e.Kind == McpFailure.Unavailable
                ? new JsonObject { ["id"] = id, ["outcome"] = "unknown", ["reason"] = e.Message, ["sent"] = sent, ["since"] = since }
                : new JsonObject { ["id"] = id, ["outcome"] = "refused", ["reason"] = e.Message, ["sent"] = sent };
        }
        var outcome = result?["outcome"] is JsonValue o && o.TryGetValue<string>(out var ov) ? ov : null;
        var answer = new JsonObject { ["id"] = id, ["outcome"] = outcome };
        if (result?["reason"] is JsonValue r && r.TryGetValue<string>(out var reason) && reason.Length > 0) { answer["reason"] = reason; }
        answer["sent"] = sent;
        if (outcome != "refused") { answer["since"] = since; }
        return answer;
    }

    // The read-only list of the same person's agent sessions across their machines (owner decision, "Yes, allow the list"): machine id, session
    // id, state, how long, machine online, and nothing a person wrote or a path or a prompt. It asks the host's fleet.list without include, so no
    // names are ever requested; a row without an exact machine id and node id is dropped, and so is every member outside the allowlist.
    public static async Task<JsonObject> FleetListAsync(IMcpCalls host, CancellationToken ct = default)
    {
        var page = Node(await host.CallAsync("fleet.list", new { }, ct: ct))?.AsObject() ?? [];
        var sessions = new JsonArray();
        foreach (var row in page["sessions"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            if (!(Text(row, "machine") is { } machine && DeviceId.IsMatch(machine) && Text(row, "node") is { } node && NodeId.IsMatch(node))) { continue; }
            var kept = new JsonObject();
            foreach (var key in new[] { "machine", "node", "state", "since", "secondsInState", "machineOnline" })
            {
                if (row.TryGetPropertyValue(key, out var value) && value is JsonValue) { kept[key] = value.DeepClone(); }
            }
            sessions.Add(kept);
        }
        var machines = new JsonArray();
        foreach (var row in page["machines"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            if (Text(row, "machine") is not { } id || !DeviceId.IsMatch(id)) { continue; }
            var kept = new JsonObject();
            foreach (var key in new[] { "machine", "state", "complete" })
            {
                if (row.TryGetPropertyValue(key, out var value) && value is JsonValue) { kept[key] = value.DeepClone(); }
            }
            machines.Add(kept);
        }
        return new JsonObject { ["sessions"] = sessions, ["machines"] = machines };
    }

    static string? Text(JsonObject row, string key) => row[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    internal static readonly System.Text.RegularExpressions.Regex DeviceId = new("^[0-9a-f]{32}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    internal static readonly System.Text.RegularExpressions.Regex NodeId = new("^agent_bridge_[0-9a-f]{16}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // A message to a session on another of the same person's machines, through this host's agent.send (docs/design/beta-messaging.md
    // section 4). The host signs it, opens the relay channel and says what came of it; the receiver refuses another account's host. The
    // five outcomes (delivered, refused, undeliverable, unconfirmed, off) come back as the host gave them; none is retried here, and a
    // failure never falls back to a local reply or any other route.
    public static async Task<JsonObject> SendAsync(IMcpCalls host, string machine, string node, string text, CancellationToken ct = default)
    {
        var answer = new JsonObject { ["machine"] = machine, ["id"] = node };
        JsonObject? result;
        try { result = Node(await host.CallAsync("agent.send", new { machine, session = node, text }, TimeSpan.FromSeconds(30), ct))?.AsObject(); }
        catch (McpToolException e)
        {
            // No answer leaves it possibly sent: unknown, and the caller must not send it again unasked.
            answer["outcome"] = e.Kind == McpFailure.Unavailable ? "unknown" : "refused";
            answer["reason"] = e.Message;
            return answer;
        }
        answer["outcome"] = result?["outcome"]?.DeepClone();
        foreach (var key in new[] { "code", "request" }) { if (result?[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0) { answer[key] = s; } }
        return answer;
    }

    const string NoGrant = "00000000-0000-0000-0000-000000000000";

    // Ends a session the host's own launch record shows an agent started (#4286), by the host's fleet.end with no grant. Thin on purpose:
    // every rule (provenance, own session, unpushed work, kill switch) is the host's. A lost answer is "unknown" and keeps its request:
    // send the same request again, never a new one, to learn what happened.
    public static async Task<JsonObject> EndAsync(IMcpCalls host, string id, string? request, CancellationToken ct = default)
    {
        request ??= Guid.NewGuid().ToString();
        JsonObject? receipt;
        try { receipt = Node(await host.CallAsync("fleet.end", new { version = 1, request, grant = NoGrant, session = id }, ct: ct))?.AsObject(); }
        catch (McpToolException e)
        {
            return new JsonObject { ["id"] = id, ["request"] = request, ["outcome"] = e.Kind == McpFailure.Unavailable ? "unknown" : "refused", ["reason"] = e.Message };
        }
        var answer = new JsonObject { ["id"] = id, ["request"] = request, ["outcome"] = receipt?["outcome"]?.DeepClone() };
        if (receipt?["reason"] is JsonValue r && r.TryGetValue<string>(out var reason) && reason.Length > 0) { answer["reason"] = reason; }
        return answer;
    }

    // What launches made through this server came to, newest 200.
    public sealed class LaunchLedger
    {
        readonly OrderedDictionary<string, JsonObject> results = [];

        public void Remember(string request, JsonObject result)
        {
            results[request] = result.DeepClone().AsObject();  // a copy: a JSON node can have one parent, and the caller's goes into a reply
            if (results.Count > Remembered) { results.RemoveAt(0); }
        }

        public JsonObject Read(string request) => results.TryGetValue(request, out var r) ? r.DeepClone().AsObject()
            : new JsonObject { ["status"] = "unknown", ["reason"] = "No launch with that request id was made through this server." };
    }

    public static async Task<JsonObject> LaunchAsync(IMcpCalls host, LaunchLedger ledger, JsonObject options, CancellationToken ct = default)
    {
        var request = options["request"] is JsonValue rq && rq.TryGetValue<string>(out var key) && key.Length > 0 ? key : Guid.NewGuid().ToString();
        var data = new JsonObject { ["request"] = request };
        foreach (var setting in LaunchSettings)
        {
            var value = options[setting];
            if (setting == "role" && !options.ContainsKey("role")) { value = DefaultWorkerRole; }
            if (value is null) { continue; }
            if (setting == "permissions" && value is JsonValue pv && pv.TryGetValue<string>(out var perm)) { value = perm switch { "Ask permission" => "ask", "Allow all" => "allowAll", _ => perm }; }
            data[setting] = value.DeepClone();
        }
        if (options["prompt"] is JsonValue p && p.TryGetValue<string>(out var prompt) && prompt.Length > 0) { data["prompt"] = prompt; }
        JsonNode? result;
        try { result = Node(await host.CallAsync("session.launch", data, ct: ct)); }
        catch (McpToolException e) when (e.Kind == McpFailure.Refused) { result = new JsonObject { ["outcome"] = "refused", ["reason"] = e.Message }; }
        var answer = new JsonObject { ["request"] = request, ["outcome"] = result?["outcome"]?.DeepClone() };
        if (result?["reason"] is JsonValue r && r.TryGetValue<string>(out var reason) && reason.Length > 0) { answer["reason"] = reason; }
        if (result?["session"] is { } session) { answer["session"] = session.DeepClone(); }
        ledger.Remember(request, answer);
        return answer;
    }

    public static async Task<JsonObject> LaunchOptionsAsync(IMcpCalls host, CancellationToken ct = default) =>
        Node(await host.CallAsync("launch.options", new { }, ct: ct))?.AsObject() ?? [];

    public static async Task<JsonObject> UsageAsync(IMcpCalls host, CancellationToken ct = default) =>
        Node(await host.CallAsync("usage.get", new { }, ct: ct))?.AsObject() ?? [];
}
