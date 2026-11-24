using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Mcp;
using Xunit;

namespace AgentBridge.Host.Tests;

// C3 PR7b: the MCP stdio server (docs/design/beta-messaging.md section 5): JSON-RPC lines in and out, the tools of PR7a behind
// capability and principal gates, argument validation as mcp/src/server.mjs does it, and nothing sent to the host but its agent ops.
public sealed class McpServerTests
{
    sealed class Conn(string? principal, params string[] caps) : IMcpConnection
    {
        public readonly List<string> Ops = [];
        public bool Open = true;
        public Func<string, JsonNode?> Answer = op => op == "session.get" ? new JsonObject { ["status"] = "idle", ["activity"] = new JsonObject { ["response"] = "r" } } : new JsonObject { ["sessions"] = new JsonArray(), ["outcome"] = "delivered", ["session"] = "s9" };
        public string? Principal => principal;
        public bool IsOpen => Open;
        public bool Has(string capability) => caps.Contains(capability);
        public Task<JsonElement> CallAsync(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            Ops.Add(op);
            return Task.FromResult(JsonSerializer.SerializeToElement(Answer(op)));
        }
    }

    static async Task<List<JsonNode>> RunAsync(Func<Task<IMcpConnection?>> ensure, params string[] lines)
    {
        var input = new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));
        var output = new MemoryStream();
        await new McpServer(ensure).RunAsync(input, output);
        return Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
    }

    static string Call(int id, string name, string args = "{}") =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + name + "\",\"arguments\":" + args + "}}";
    const string List = """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""";
    static Func<Task<IMcpConnection?>> With(Conn? c) => () => Task.FromResult<IMcpConnection?>(c);
    static string[] Names(JsonNode reply) => reply["result"]!["tools"]!.AsArray().Select(t => (string)t!["name"]!).ToArray();

    [Fact]
    public async Task Initialize_names_the_server_and_its_tools_capability_and_tells_agents_to_use_it_and_not_work_around_a_refusal()
    {
        var replies = await RunAsync(With(null), """{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"x","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        var result = replies.Single()["result"]!;
        Assert.Equal("2025-03-26", (string?)result["protocolVersion"]);
        Assert.NotNull(result["capabilities"]!["tools"]);
        Assert.Equal("slipgates", (string?)result["serverInfo"]!["name"]);
        Assert.Contains("refused", (string?)result["instructions"]);
    }

    [Fact]
    public async Task With_no_host_it_lists_no_tools_so_agents_fall_back_and_a_call_says_the_product_is_not_running()
    {
        var replies = await RunAsync(With(null), List, Call(2, "list_agent_sessions"));
        Assert.Empty(Names(replies[0]));
        Assert.True((bool?)replies[1]["result"]!["isError"]);
    }

    [Fact]
    public async Task Tools_are_listed_by_the_capabilities_the_host_names_and_the_acting_ones_need_an_accepted_principal()
    {
        var unattributed = await RunAsync(With(new Conn(null, "sessions.read", "sessions.reply", "sessions.launch", "usage")), List);
        Assert.Equal(["list_agent_sessions", "read_agent_session"], Names(unattributed[0]));
        var agent = await RunAsync(With(new Conn("agent:m/s", "sessions.read", "sessions.reply", "sessions.launch", "usage")), List);
        Assert.Equal(["list_agent_sessions", "read_agent_session", "reply_to_agent_session", "launch_agent_session", "read_launch_result", "get_launch_options", "read_agent_usage"], Names(agent[0]));
        var readOnly = await RunAsync(With(new Conn("agent:m/s", "sessions.read")), List);
        Assert.Equal(["list_agent_sessions", "read_agent_session"], Names(readOnly[0]));
    }

    [Fact]
    public async Task A_call_returns_the_result_as_text_and_structured_content_and_a_launch_is_readable_afterwards()
    {
        var conn = new Conn("agent:m/s", "sessions.read", "sessions.launch");
        var replies = await RunAsync(With(conn), Call(1, "list_agent_sessions"), Call(2, "launch_agent_session", """{"prompt":"go","request":"k1"}"""), Call(3, "read_launch_result", """{"request":"k1"}"""));
        Assert.True(replies[0]["result"]?["structuredContent"]?["sessions"] is not null, replies[0].ToJsonString());
        Assert.Contains("sessions", (string?)replies[0]["result"]!["content"]![0]!["text"]);
        Assert.Equal("s9", (string?)replies[2]["result"]!["structuredContent"]!["session"]);
    }

    [Theory]
    [InlineData("list_agent_sessions", """{"include":"all"}""")]
    [InlineData("read_agent_session", "{}")]
    [InlineData("read_agent_session", """{"session_id":"s","wait_seconds":"soon"}""")]
    [InlineData("reply_to_agent_session", """{"session_id":"s","text":""}""")]
    [InlineData("launch_agent_session", """{"role":"  "}""")]
    [InlineData("launch_agent_session", """{"request":"has space"}""")]
    [InlineData("read_launch_result", "{}")]
    [InlineData("read_agent_usage", """{"x":1}""")]
    public async Task Bad_arguments_are_refused_before_anything_is_sent_to_the_host(string tool, string args)
    {
        var conn = new Conn("agent:m/s", "sessions.read", "sessions.reply", "sessions.launch", "usage");
        var replies = await RunAsync(With(conn), Call(1, tool, args));
        Assert.True((bool?)replies[0]["result"]!["isError"]);
        Assert.Empty(conn.Ops);
    }

    [Fact]
    public async Task A_text_over_sixteen_kib_is_refused_and_an_unknown_or_unlisted_tool_is_an_error_not_a_call()
    {
        var conn = new Conn(null, "sessions.read", "sessions.reply");
        var replies = await RunAsync(With(conn), Call(1, "reply_to_agent_session", $$"""{"session_id":"s","text":"{{new string('x', 16 * 1024 + 1)}}"}"""),
            Call(2, "reply_to_agent_session", """{"session_id":"s","text":"hi"}"""), Call(3, "end_the_world"), Call(4, "read_agent_usage"));
        Assert.All(replies, r => Assert.True((bool?)r["result"]!["isError"]));
        Assert.Empty(conn.Ops);
    }

    [Fact]
    public async Task A_host_that_fails_a_call_gives_an_error_result_with_its_words_and_the_server_goes_on()
    {
        var conn = new Conn("agent:m/s", "sessions.read") { Answer = op => throw new McpToolException("the host said no", McpFailure.Refused) };
        var replies = await RunAsync(With(conn), Call(1, "list_agent_sessions"), """{"jsonrpc":"2.0","id":2,"method":"ping"}""");
        Assert.Equal("the host said no", (string?)replies[0]["result"]!["content"]![0]!["text"]);
        Assert.NotNull(replies[1]["result"]);
    }

    [Fact]
    public async Task A_closed_connection_is_looked_for_again_and_an_unknown_method_or_broken_line_gets_a_json_rpc_error()
    {
        var ensured = 0;
        var first = new Conn(null, "sessions.read") { Open = false };
        var replies = await RunAsync(() => Task.FromResult<IMcpConnection?>(++ensured == 1 ? first : new Conn(null, "sessions.read")), List, List, """{"jsonrpc":"2.0","id":3,"method":"nope"}""", "{not json");
        Assert.Equal(2, ensured);
        Assert.Equal(-32601, (int?)replies[2]["error"]!["code"]);
        Assert.Equal(-32700, (int?)replies[3]["error"]!["code"]);
        Assert.Null(replies[3]["id"]);
    }

    // Whatever it is asked, the server asks the host only for the ops of an agent's tools; the host classifies the caller by ancestry
    // and refuses the app-only ones to it (tests/AgentBridge.Host.Tests LocalApi), so this is the server's half of that.
    [Fact]
    public async Task Whatever_it_is_asked_the_server_sends_the_host_only_the_agent_ops_of_its_tools()
    {
        var conn = new Conn("agent:m/s", "sessions.read", "sessions.reply", "sessions.launch", "usage");
        await RunAsync(With(conn), Call(1, "list_agent_sessions"), Call(2, "read_agent_session", """{"session_id":"s"}"""), Call(3, "reply_to_agent_session", """{"session_id":"s","text":"x"}"""),
            Call(4, "launch_agent_session", "{}"), Call(5, "get_launch_options"), Call(6, "read_agent_usage"), Call(7, "host.stop"), Call(8, "session.stop"), Call(9, "read_agent_session", """{"session_id":"host.stop"}"""));
        string[] allowed = ["sessions.list", "session.get", "session.reply", "session.launch", "launch.options", "usage.get"];
        Assert.All(conn.Ops, op => Assert.Contains(op, allowed));
    }
}
