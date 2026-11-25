using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Mcp;
using Xunit;

namespace AgentBridge.Host.Tests;

// C3 PR7a: the tools of mcp/src/tools.mjs over a host's pipe, ported with the same outputs. The server (PR7b) validates arguments and
// frames them for MCP; these only turn a host's answers into the tools' results.
public sealed class McpToolsTests
{
    sealed class FakeHost : IMcpCalls
    {
        public readonly List<(string Op, JsonNode? Data)> Calls = [];
        public Func<string, JsonNode?, JsonNode?> Answer = (op, data) => new JsonObject();
        public Task<JsonElement> CallAsync(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            var node = data is null ? null : JsonSerializer.SerializeToNode(data);
            Calls.Add((op, node));
            return Task.FromResult(JsonSerializer.SerializeToElement(Answer(op, node)));
        }
    }

    static JsonObject Session(string id = "s1", string status = "idle", int turns = 1, string response = "hi") => new()
    {
        ["id"] = id, ["agent"] = "claude", ["title"] = "T", ["repository"] = "r", ["branch"] = "b", ["model"] = "m", ["status"] = status,
        ["statusSince"] = "2026-11-23T00:00:00Z", ["startedAt"] = "2026-11-23T00:00:00Z", ["updatedAt"] = "2026-11-23T00:00:01Z",
        ["cwd"] = "C:\\secret\\path", ["activity"] = new JsonObject { ["turnCount"] = turns, ["response"] = response }, ["question"] = "Q?",
    };

    [Fact]
    public async Task The_default_list_has_status_and_metadata_but_no_working_folder_and_content_adds_it()
    {
        var host = new FakeHost { Answer = (op, d) => new JsonObject { ["sessions"] = new JsonArray(Session()) } };
        var plain = (await McpTools.ListSessionsAsync(host, false))["sessions"]![0]!.AsObject();
        Assert.Equal(["id", "agent", "title", "repository", "branch", "model", "status", "statusSince", "startedAt", "updatedAt"], plain.Select(p => p.Key));
        var content = (await McpTools.ListSessionsAsync(host, true))["sessions"]![0]!.AsObject();
        Assert.Equal("C:\\secret\\path", (string?)content["cwd"]);
        Assert.NotNull(content["activity"]);
        Assert.Equal("Q?", (string?)content["question"]);
    }

    [Fact]
    public async Task The_list_follows_the_cursor_to_the_end_and_reports_held_discovery()
    {
        var host = new FakeHost
        {
            Answer = (op, d) => d?["cursor"] is null
                ? new JsonObject { ["sessions"] = new JsonArray(Session("a")), ["next"] = "c2", ["discoveryHeld"] = true }
                : new JsonObject { ["sessions"] = new JsonArray(Session("b")) },
        };
        var result = await McpTools.ListSessionsAsync(host, false);
        Assert.Equal(["a", "b"], result["sessions"]!.AsArray().Select(s => (string?)s!["id"]));
        Assert.True((bool?)result["discoveryHeld"]);
        Assert.Equal("c2", (string?)host.Calls[1].Data!["cursor"]);
    }

    [Fact]
    public void The_turn_marker_moves_with_the_turn_count_and_response_and_is_sixteen_hex_digits()
    {
        var a = McpTools.TurnMarker(Session(turns: 1, response: "x"));
        Assert.Matches("^[0-9a-f]{16}$", a);
        Assert.NotEqual(a, McpTools.TurnMarker(Session(turns: 2, response: "x")));
        Assert.NotEqual(a, McpTools.TurnMarker(Session(turns: 1, response: "y")));
        Assert.Equal(McpTools.TurnMarker(new JsonObject()), McpTools.TurnMarker(new JsonObject { ["activity"] = new JsonObject() }));
    }

    [Fact]
    public async Task A_read_without_since_is_done_when_not_working_and_a_missing_session_is_not_found()
    {
        var host = new FakeHost { Answer = (op, d) => Session() };
        var read = await McpTools.ReadSessionAsync(host, "s1", null, 0);
        Assert.True((bool?)read["done"]);
        Assert.Equal("hi", (string?)read["response"]);
        Assert.Equal(McpTools.TurnMarker(Session()), (string?)read["since"]);
        host.Answer = (op, d) => new JsonObject { ["missing"] = true };
        var gone = await McpTools.ReadSessionAsync(host, "s1", null, 0);
        Assert.False((bool?)gone["found"]);
        Assert.False((bool?)gone["done"]);
    }

    [Fact]
    public async Task A_read_with_the_since_of_the_unchanged_turn_never_hands_back_a_stale_answer()
    {
        var host = new FakeHost { Answer = (op, d) => Session() };
        var stale = await McpTools.ReadSessionAsync(host, "s1", McpTools.TurnMarker(Session()), 0);
        Assert.False((bool?)stale["done"]);
        Assert.Equal("", (string?)stale["response"]);
    }

    [Fact]
    public async Task A_read_that_waits_polls_until_the_turn_moves_and_stops_working()
    {
        var polls = 0;
        var since = McpTools.TurnMarker(Session(turns: 1));
        var host = new FakeHost { Answer = (op, d) => ++polls < 3 ? Session(status: "working", turns: 1) : Session(status: "idle", turns: 2, response: "new") };
        var read = await McpTools.ReadSessionAsync(host, "s1", since, 60, _ => Task.CompletedTask);
        Assert.True((bool?)read["done"]);
        Assert.Equal("new", (string?)read["response"]);
        Assert.Equal(3, polls);
    }

    [Fact]
    public async Task A_reply_returns_the_outcome_the_size_sent_and_the_since_to_read_against()
    {
        var host = new FakeHost { Answer = (op, d) => op == "session.get" ? Session() : new JsonObject { ["outcome"] = "delivered" } };
        var reply = await McpTools.ReplyAsync(host, "s1", "héllo");
        Assert.Equal("delivered", (string?)reply["outcome"]);
        Assert.Equal(6, (int?)reply["sent"]);
        Assert.Equal(McpTools.TurnMarker(Session()), (string?)reply["since"]);
        var sent = host.Calls.Single(c => c.Op == "session.reply").Data!;
        Assert.Equal("s1", (string?)sent["id"]);
        Assert.True(Guid.TryParse((string?)sent["request"], out _));
    }

    [Fact]
    public async Task A_reply_to_a_missing_session_and_one_the_host_refuses_are_refused_and_carry_no_since()
    {
        var missing = new FakeHost { Answer = (op, d) => new JsonObject { ["missing"] = true } };
        Assert.Equal("refused", (string?)(await McpTools.ReplyAsync(missing, "s1", "x"))["outcome"]);
        var refused = new FakeHost { Answer = (op, d) => op == "session.get" ? Session() : new JsonObject { ["outcome"] = "refused", ["reason"] = "busy" } };
        var reply = await McpTools.ReplyAsync(refused, "s1", "x");
        Assert.Equal("busy", (string?)reply["reason"]);
        Assert.Null(reply["since"]);
    }

    [Fact]
    public async Task A_reply_that_times_out_is_unknown_with_a_since_so_it_is_not_thrown()
    {
        var calls = 0;
        var host = new TimingOutHost(() => ++calls > 1);
        var reply = await McpTools.ReplyAsync(host, "s1", "x");
        Assert.Equal("unknown", (string?)reply["outcome"]);
        Assert.NotNull(reply["since"]);
    }

    sealed class TimingOutHost(Func<bool> failNow) : IMcpCalls
    {
        public Task<JsonElement> CallAsync(string op, object? data = null, TimeSpan? timeout = null, CancellationToken ct = default) =>
            failNow() ? throw new McpToolException("did not answer", McpFailure.Unavailable) : Task.FromResult(JsonSerializer.SerializeToElement(Session()));
    }

    [Fact]
    public async Task A_launch_names_a_default_worker_role_maps_permissions_and_remembers_its_result()
    {
        var host = new FakeHost { Answer = (op, d) => new JsonObject { ["outcome"] = "launched", ["session"] = "s9" } };
        var ledger = new McpTools.LaunchLedger();
        var answer = await McpTools.LaunchAsync(host, ledger, new JsonObject { ["prompt"] = "go", ["permissions"] = "Allow all", ["agent"] = "claude" });
        var data = host.Calls.Single().Data!;
        Assert.Equal("default", (string?)data["role"]);
        Assert.Equal("allowAll", (string?)data["permissions"]);
        Assert.Equal("go", (string?)data["prompt"]);
        Assert.Equal("s9", (string?)answer["session"]);
        Assert.Equal("launched", (string?)ledger.Read((string)answer["request"]!)["outcome"]);
        Assert.Equal("unknown", (string?)ledger.Read("never-made")["status"]);
    }

    [Fact]
    public async Task A_launch_the_host_refuses_is_a_refused_outcome_and_an_explicit_request_key_is_kept()
    {
        var host = new FakeHost { Answer = (op, d) => throw new McpToolException("not allowed", McpFailure.Refused) };
        var answer = await McpTools.LaunchAsync(host, new McpTools.LaunchLedger(), new JsonObject { ["request"] = "my-key", ["role"] = "builder" });
        Assert.Equal("refused", (string?)answer["outcome"]);
        Assert.Equal("my-key", (string?)answer["request"]);
        Assert.Equal("builder", (string?)host.Calls.Single().Data!["role"]);
    }

    [Fact]
    public async Task The_launch_ledger_keeps_the_last_two_hundred()
    {
        var ledger = new McpTools.LaunchLedger();
        for (var i = 0; i < 201; i++) { ledger.Remember($"r{i}", new JsonObject { ["outcome"] = "x" }); }
        Assert.Equal("unknown", (string?)ledger.Read("r0")["status"]);
        Assert.Equal("x", (string?)ledger.Read("r200")["outcome"]);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Options_and_usage_are_the_hosts_answers_unchanged()
    {
        var host = new FakeHost { Answer = (op, d) => new JsonObject { ["op"] = op } };
        Assert.Equal("launch.options", (string?)(await McpTools.LaunchOptionsAsync(host))["op"]);
        Assert.Equal("usage.get", (string?)(await McpTools.UsageAsync(host))["op"]);
    }

    const string Session1 = "11111111-1111-4111-8111-111111111111";

    [Fact]
    public async Task End_sends_fleet_end_with_no_grant_and_a_fresh_request_and_returns_the_outcome()
    {
        var host = new FakeHost { Answer = (op, d) => new JsonObject { ["outcome"] = "ended" } };
        var end = await McpTools.EndAsync(host, Session1, null);
        Assert.Equal("ended", (string?)end["outcome"]);
        var sent = host.Calls.Single().Data!;
        Assert.Equal("fleet.end", host.Calls.Single().Op);
        Assert.Equal(1, (int?)sent["version"]);
        Assert.Equal("00000000-0000-0000-0000-000000000000", (string?)sent["grant"]);
        Assert.Equal(Session1, (string?)sent["session"]);
        Assert.True(Guid.TryParse((string?)sent["request"], out _));
        Assert.Equal((string?)sent["request"], (string?)end["request"]);
    }

    [Fact]
    public async Task End_keeps_the_callers_request_a_host_refusal_carries_its_reason_and_a_lost_answer_is_unknown()
    {
        var refused = new FakeHost { Answer = (op, d) => new JsonObject { ["outcome"] = "refused", ["reason"] = "scope" } };
        var r = await McpTools.EndAsync(refused, Session1, "22222222-2222-4222-8222-222222222222");
        Assert.Equal("refused", (string?)r["outcome"]);
        Assert.Equal("scope", (string?)r["reason"]);
        Assert.Equal("22222222-2222-4222-8222-222222222222", (string?)r["request"]);
        var lost = new FakeHost { Answer = (op, d) => throw new McpToolException("did not answer", McpFailure.Unavailable) };
        var u = await McpTools.EndAsync(lost, Session1, "22222222-2222-4222-8222-222222222222");
        Assert.Equal("unknown", (string?)u["outcome"]);
        Assert.Equal("22222222-2222-4222-8222-222222222222", (string?)u["request"]);
        var denied = new FakeHost { Answer = (op, d) => throw new McpToolException("not allowed", McpFailure.Refused) };
        Assert.Equal("refused", (string?)(await McpTools.EndAsync(denied, Session1, null))["outcome"]);
    }
}
