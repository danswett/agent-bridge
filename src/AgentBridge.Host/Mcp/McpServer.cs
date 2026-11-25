using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Mcp;

// What the server needs of its connection to the host: McpHost is the real one.
public interface IMcpConnection : IMcpCalls
{
    string? Principal { get; }
    bool IsOpen { get; }
    bool Has(string capability);
}

// The MCP server over stdio (docs/design/beta-messaging.md section 5): JSON-RPC 2.0, one message per line, as mcp/src/server.mjs
// speaks it. It lists only the tools the host's capabilities name, and the acting ones only for an accepted agent principal; it sends the
// host only the agent ops of those tools, and the host decides everything else (it classes this process by ancestry as the agent).
public sealed class McpServer(Func<Task<IMcpConnection?>> ensure)
{
    const int MaxText = 16 * 1024;

    static readonly string Instructions = $"{Product.Name} messaging is connected on this machine. When you need to message, read or start another agent session, " +
        "use these tools instead of any other messaging route; they carry your identity and the permissions the owner gave you. " +
        "If a tool answers \"refused\", you may not do that here: say so rather than working around it.";

    // name, capability needed, whether an unattributed connection may use it, description, input schema
    static readonly (string Name, string Needs, bool Unattributed, string Description, string Schema)[] Tools =
    [
        ("list_agent_sessions", "sessions.read", true, "List the agent sessions on this machine: status, title, repository and branch (no working folder). include \"content\" adds the working folder (cwd), latest response and question of each session.",
            """{"type":"object","properties":{"include":{"type":"string","enum":["content"],"description":"Add each session's latest response and question. Default: status only."}}}"""),
        ("read_agent_session", "sessions.read", true, "Read a session's latest answer. Pass the since from reply_to_agent_session to never get a stale answer.",
            """{"type":"object","required":["session_id"],"properties":{"session_id":{"type":"string","description":"Session id from list_agent_sessions."},"since":{"type":"string","description":"The \"since\" reply_to_agent_session returned; the answer then waits for a newer turn."},"wait_seconds":{"type":"number","description":"Wait up to this long (max 600) for the turn to finish."}}}"""),
        ("reply_to_agent_session", "sessions.reply", false, "Send a message to another agent session on this machine.",
            """{"type":"object","required":["session_id","text"],"properties":{"session_id":{"type":"string","description":"Session id."},"text":{"type":"string","description":"The whole message, up to 16 KiB. It arrives as one line."}}}"""),
        ("launch_agent_session", "sessions.launch", false, "Start (or resume) an agent session on this machine.",
            """{"type":"object","properties":{"machine":{"type":"string","description":"Ignored: this server reaches this machine only."},"prompt":{"type":"string","description":"The opening prompt, whole, up to 16 KiB."},"request":{"type":"string","description":"Your own key for this launch (letters, digits, - _ . :), to read its result later."},"agent":{"type":"string","description":"Launcher id from get_launch_options."},"workspace":{"type":"string","description":"Workspace label."},"profile":{"type":"string","description":"Agency profile."},"resume":{"type":"string","description":"Id of a resumable session."},"model":{"type":"string","description":"Model."},"effort":{"type":"string","description":"Effort."},"context":{"type":"string","description":"Context."},"permissions":{"type":"string","enum":["Ask permission","Allow all"]},"role":{"type":"string","description":"Worker role (letters, digits, hyphens). Omitted: default. Cost/context control, not a security boundary. Null or blank roles are rejected."}}}"""),
        ("read_launch_result", "sessions.launch", false, "What a launch made through this server came to.",
            """{"type":"object","required":["request"],"properties":{"machine":{"type":"string","description":"Ignored."},"request":{"type":"string","description":"The \"request\" launch_agent_session returned."}}}"""),
        ("get_launch_options", "sessions.launch", false, "What launch_agent_session can name on this machine.",
            """{"type":"object","properties":{"machine":{"type":"string","description":"Ignored."}}}"""),
        ("read_agent_usage", "usage", false, "Read this host's last usage report, including allowance windows, sources, reading times and errors. Requires an accepted agent principal and the usage capability; no shared or remote reads.",
            """{"type":"object","properties":{},"additionalProperties":false}"""),
        ("end_agent_session", "sessions.launch", false, "End a session on this machine that an agent started through the fleet launch route. The host refuses a person-started session, your own, an unknown or shortened id, one with uncommitted or unpushed work, or one it has no launch record for; outcome \"refused\" with a reason means do not try another way. outcome \"unknown\" means send the SAME request again.",
            """{"type":"object","required":["session_id"],"properties":{"session_id":{"type":"string","description":"Full session id (a UUID) from list_agent_sessions."},"request":{"type":"string","description":"A UUID of your own; resend the same one after an unknown outcome."}}}"""),
    ];

    readonly McpTools.LaunchLedger ledger = new();

    // The `agent-bridge-host mcp` command: the server on stdio, finding its host lazily and again after the host goes. Nothing but
    // protocol lines goes to stdout.
    public static async Task<int> RunStdioAsync(Func<string, string?> env, Stream input, Stream output, CancellationToken ct = default)
    {
        McpHost? current = null;
        async Task<IMcpConnection?> Ensure()
        {
            if (current is { IsOpen: true }) { return current; }
            current = null;
            if (McpEndpoint.Resolve(env) is not { } where) { return null; }
            try { current = await McpHost.OpenAsync(where, McpHost.TokenFrom(env), ct: ct); } catch (McpToolException) { current = null; }
            return current;
        }
        await new McpServer(Ensure).RunAsync(input, output, ct);
        if (current is not null) { await current.DisposeAsync(); }
        return 0;
    }

    static bool Allowed(IMcpConnection conn, (string Name, string Needs, bool Unattributed, string Description, string Schema) tool) =>
        conn.Has(tool.Needs) && (conn.Principal is not null || tool.Unattributed);

    public async Task RunAsync(Stream input, Stream output, CancellationToken ct = default)
    {
        using var reader = new StreamReader(input, new UTF8Encoding(false));
        var writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) { continue; }
            var reply = await HandleAsync(line, ct);
            if (reply is not null) { await writer.WriteLineAsync(reply.ToJsonString()); }
        }
    }

    static JsonObject Error(JsonNode? id, int code, string message) => new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    async Task<JsonObject?> HandleAsync(string line, CancellationToken ct)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(line); }
        catch (JsonException) { return Error(null, -32700, "Parse error"); }
        if (parsed is not JsonObject message || message["method"] is not JsonValue m || !m.TryGetValue<string>(out var method)) { return Error(null, -32600, "Invalid request"); }
        var id = message["id"];
        if (id is null) { return null; }  // a notification (notifications/initialized and the like) gets no answer
        JsonNode? result;
        switch (method)
        {
            case "initialize":
                var version = message["params"]?["protocolVersion"] is JsonValue v && v.TryGetValue<string>(out var pv) ? pv : "2025-03-26";
                result = new JsonObject { ["protocolVersion"] = version, ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "slipgates", ["version"] = "0.1.0" }, ["instructions"] = Instructions };
                break;
            case "ping": result = new JsonObject(); break;
            case "tools/list": result = await ListAsync(); break;
            case "tools/call": result = await CallAsync(message["params"] as JsonObject, ct); break;
            default: return Error(id, -32601, $"Method not found: {method}");
        }
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };
    }

    async Task<JsonObject> ListAsync()
    {
        var tools = new JsonArray();
        // No host: stay quiet so the agent falls back.
        if (await ensure() is { } conn)
        {
            foreach (var tool in Tools.Where(t => Allowed(conn, t)))
            {
                var entry = new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description, ["inputSchema"] = JsonNode.Parse(tool.Schema) };
                if (tool.Name == "read_agent_usage") { entry["annotations"] = new JsonObject { ["readOnlyHint"] = true }; }
                tools.Add(entry);
            }
        }
        return new JsonObject { ["tools"] = tools };
    }

    static JsonObject Failure(string text) => new() { ["isError"] = true, ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };

    async Task<JsonObject> CallAsync(JsonObject? parameters, CancellationToken ct)
    {
        try
        {
            var name = parameters?["name"] is JsonValue n && n.TryGetValue<string>(out var s) ? s : "";
            var args = parameters?["arguments"] ?? new JsonObject();
            var tool = Tools.FirstOrDefault(t => t.Name == name);
            if (tool.Name is null) { throw new McpToolException($"Unknown tool {name}."); }
            if (await ensure() is not { } conn) { throw new McpToolException($"{Product.Name} is not running on this machine.", McpFailure.Unavailable); }
            if (!Allowed(conn, tool)) { throw new McpToolException($"{name} is not available on this connection.", McpFailure.Refused); }
            var a = Validate(name, args);
            JsonObject value = name switch
            {
                "list_agent_sessions" => await McpTools.ListSessionsAsync(conn, (string?)a["include"] == "content", ct),
                "read_agent_session" => await McpTools.ReadSessionAsync(conn, (string)a["session_id"]!, (string?)a["since"], Math.Min(600, a["wait_seconds"] is JsonValue w && w.TryGetValue<double>(out var d) ? d : 0), ct: ct),
                "reply_to_agent_session" => await McpTools.ReplyAsync(conn, (string)a["session_id"]!, (string)a["text"]!, ct),
                "launch_agent_session" => await McpTools.LaunchAsync(conn, ledger, a, ct),
                "read_launch_result" => ledger.Read((string)a["request"]!),
                "read_agent_usage" => await McpTools.UsageAsync(conn, ct),
                "end_agent_session" => await McpTools.EndAsync(conn, (string)a["session_id"]!, (string?)a["request"], ct),
                _ => await McpTools.LaunchOptionsAsync(conn, ct),
            };
            return new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = value.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) }), ["structuredContent"] = value };
        }
        catch (Exception e) when (e is not OperationCanceledException) { return Failure(e.Message); }
    }

    // The same rules mcp/src/server.mjs applies, before anything is sent to the host.
    static JsonObject Validate(string tool, JsonNode args)
    {
        if (args is not JsonObject a) { throw new McpToolException("Arguments must be an object."); }
        void Str(string key, int max = 512, bool optional = false)
        {
            if (!a.ContainsKey(key) && optional) { return; }
            if (a[key] is not JsonValue v || !v.TryGetValue<string>(out var s) || (!optional && s.Length == 0)) { throw new McpToolException($"{key} must be a{(optional ? "" : " non-empty")} string."); }
            if (Encoding.UTF8.GetByteCount(s) > max) { throw new McpToolException($"{key} is too large (max {max} bytes)."); }
        }
        void RequestKey(bool optional = false)
        {
            if (!a.ContainsKey("request") && optional) { return; }
            if (a["request"] is not JsonValue v || !v.TryGetValue<string>(out var s) || !System.Text.RegularExpressions.Regex.IsMatch(s, "^[A-Za-z0-9._:-]{1,128}$"))
            {
                throw new McpToolException("request must be 1 to 128 characters: letters, digits, \"-\", \"_\", \".\" or \":\".");
            }
        }
        void Uuid(string key, bool optional)
        {
            if (!a.ContainsKey(key) && optional) { return; }
            if (a[key] is not JsonValue v || !v.TryGetValue<string>(out var s) || !Guid.TryParseExact(s, "D", out _)) { throw new McpToolException($"{key} must be a full UUID."); }
        }
        switch (tool)
        {
            case "list_agent_sessions" when a.ContainsKey("include") && !(a["include"] is JsonValue iv && iv.TryGetValue<string>(out var inc) && inc == "content"):
                throw new McpToolException("include must be \"content\".");
            case "read_agent_session":
                Str("session_id"); Str("since", optional: true);
                if (a.ContainsKey("wait_seconds") && !(a["wait_seconds"] is JsonValue w && w.TryGetValue<double>(out var d) && double.IsFinite(d))) { throw new McpToolException("wait_seconds must be a number."); }
                break;
            case "reply_to_agent_session": Str("session_id"); Str("text", MaxText); break;
            case "launch_agent_session":
                Str("prompt", MaxText, true); RequestKey(true);
                foreach (var key in new[] { "agent", "workspace", "profile", "resume", "model", "effort", "context", "permissions", "role" }) { Str(key, optional: true); }
                if (a.ContainsKey("role") && string.IsNullOrWhiteSpace((string?)a["role"])) { throw new McpToolException("role must not be blank; omit it to use the default worker role."); }
                break;
            case "read_launch_result": RequestKey(); break;
            case "end_agent_session": Uuid("session_id", false); Uuid("request", true); break;
            case "read_agent_usage" when a.Count > 0: throw new McpToolException("read_agent_usage takes no arguments.");
        }
        return a;
    }
}
