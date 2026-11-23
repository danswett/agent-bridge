using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Local;
using Xunit;

public sealed class SessionsFindCommandTests
{
    static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    const string Node = "agent_bridge_1234567890abcdef";
    const string Device = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string Uuid = "12345678-90ab-cdef-1111-111111111111";
    const string Session = """{"node":"agent_bridge_1234567890abcdef","machine":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","machineName":"VM-A","state":"idle","online":true,"trust":"own-account"}""";

    [Theory, InlineData("SG Lead"), InlineData("SG L"), InlineData("agent_bridge_1234567890abcdef")]
    public async Task Name_selectors_are_refused_without_contacting_the_host(string name)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var calls = 0;
        var code = await SessionsFindCommands.ExecuteAsync(["sessions", "find", "--name", name], output, error,
            "fixture-token", (op, _) => { calls++; return Task.FromResult(Json(op == "fleet.find"
                ? "{\"result\":\"found\",\"fleetScope\":\"account\",\"session\":" + Session + "}" : "{}")); });
        Assert.Equal(2, code);
        Assert.Equal(0, calls);
        Assert.Contains("refused:", error.ToString());
        Assert.Contains(name, error.ToString());
    }

    [Theory, InlineData("unknown"), InlineData("stale")]
    public async Task Id_refusals_name_the_id_and_never_retry_or_resolve_a_name(string reason)
    {
        const string id = "agent_bridge_fedcba0987654321";
        using var output = new StringWriter();
        using var error = new StringWriter();
        var finds = 0;
        var code = await SessionsFindCommands.ExecuteAsync(["sessions", "find", "--session-id", id], output, error,
            "fixture-token", (op, _) => { if (op == "fleet.find") { finds++; throw new HostErrorException($"refused: sessionId {id} is {reason}"); }
                return Task.FromResult(Json("{}")); });
        Assert.Equal(5, code);
        Assert.Equal(1, finds);
        Assert.Contains(id, error.ToString());
        Assert.Contains("refused:", error.ToString());
        Assert.Equal("", output.ToString());
    }

    [Theory]
    [InlineData("""{"result":"found","fleetScope":"account","session":SESSION}""", 0)]
    [InlineData("""{"result":"ambiguous","fleetScope":"account","candidates":[SESSION,SESSION]}""", 4)]
    [InlineData("""{"result":"notFound","fleetScope":"account"}""", 5)]
    [InlineData("""{"result":"offline","fleetScope":"account"}""", 5)]
    [InlineData("""{"result":"found","session":{}}""", 6)]
    [InlineData("""{"result":"found","fleetScope":"account","session":{}}""", 6)]
    [InlineData("""{"result":"unknown","fleetScope":"account"}""", 6)]
    [InlineData("""{"result":"ambiguous","fleetScope":"account","candidates":[SESSION]}""", 6)]
    [InlineData("""{"result":"found","fleetScope":"account","session":SESSION_BAD_TRUST}""", 5)]
    [InlineData("""{"result":"found","fleetScope":"account","session":SESSION_DEAD}""", 6)]
    [InlineData("""{"result":"found","fleetScope":"account","session":SESSION_WRONG_MACHINE}""", 6)]
    public async Task Results_have_explicit_exit_codes_and_json_matches(string response, int expected)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var calls = new List<string>();
        var code = await SessionsFindCommands.ExecuteAsync(["sessions", "find", "--session-id", Node, "--machine", Device, "--json"],
            output, error, "fixture-token", (op, data) =>
            {
                calls.Add(op);
                if (op == "fleet.find")
                {
                    var request = JsonSerializer.SerializeToElement(data);
                    Assert.Equal(Node, request.GetProperty("sessionId").GetString());
                    Assert.Equal(Device, request.GetProperty("machine").GetString());
                    Assert.False(request.TryGetProperty("name", out _));
                    Assert.False(request.TryGetProperty("machineName", out _));
                }
                return Task.FromResult(op == "fleet.find" ? Json(response
                    .Replace("SESSION_WRONG_MACHINE", Session.Replace(Device, new string('b', 32), StringComparison.Ordinal), StringComparison.Ordinal)
                    .Replace("SESSION_BAD_TRUST", Session.Replace("own-account", "verified", StringComparison.Ordinal), StringComparison.Ordinal)
                    .Replace("SESSION_DEAD", Session.Replace("idle", "dead", StringComparison.Ordinal), StringComparison.Ordinal)
                    .Replace("SESSION", Session, StringComparison.Ordinal))
                    : Json("""{"machine":"local","protocol":1}"""));
            });
        Assert.Equal(expected, code);
        Assert.Equal(new[] { "hello", "agent.hello", "fleet.find" }, calls);
        var result = Json(output.ToString());
        if (expected is 0 or 4)
        {
            var matches = result.GetProperty("matches");
            Assert.Equal(expected == 0 ? 1 : 2, matches.GetArrayLength());
            Assert.Equal("VM-A", matches[0].GetProperty("machine").GetString());
            Assert.Equal(new[] { "machine", "node", "state" }, matches[0].EnumerateObject().Select(p => p.Name).Order());
        }
        if (expected == 6) { Assert.Contains("fleet.find response", error.ToString()); }
    }

    [Theory]
    [InlineData("fleet.find", "refused: off: killed", 5)]
    [InlineData("fleet.find", "refused: off: disabled", 5)]
    [InlineData("fleet.find", "refused: notSameAccount: other account", 5)]
    [InlineData("fleet.find", "registry-get: HTTP 503 unavailable", 6)]
    [InlineData("agent.hello", "refused: credential outside its session", 5)]
    [InlineData("hello", "HTTP 502", 6)]
    public async Task Refusals_and_transient_host_errors_keep_the_stage_and_reason(string stage, string reason, int expected)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await SessionsFindCommands.ExecuteAsync(["sessions", "find", "--session-id", Node],
            output, error, "fixture-token", (op, _) => op == stage ? throw new HostErrorException(reason)
                : Task.FromResult(Json("{}")));
        Assert.Equal(expected, code);
        Assert.Contains(stage, error.ToString());
        Assert.Contains(reason, error.ToString());
        Assert.DoesNotContain("notFound", error.ToString());
    }

    [Fact]
    public async Task Ambiguous_plain_output_names_every_candidate_machine()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var response = "{\"result\":\"ambiguous\",\"fleetScope\":\"account\",\"candidates\":[" + Session + ","
            + Session.Replace("VM-A", "VM-B", StringComparison.Ordinal) + "]}";
        Assert.Equal(4, await SessionsFindCommands.ExecuteAsync(["sessions", "find", "--session-id", Node], output, error,
            "fixture-token", (op, _) => Task.FromResult(Json(op == "fleet.find" ? response : "{}"))));
        Assert.Contains("VM-A", output.ToString());
        Assert.Contains("VM-B", output.ToString());
        Assert.Contains("ambiguous", error.ToString());
    }

    [Fact]
    public async Task An_unreachable_pipe_reports_connect_and_the_underlying_reason()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await SessionsFindCommands.RunAsync(["sessions", "find", "--session-id", Node, "--json"],
            Path.GetTempPath(), output, error, _ => throw new HostNotRunningException("host unavailable", new TimeoutException("pipe deadline")));
        Assert.Equal(6, code);
        Assert.Contains("connect", error.ToString());
        Assert.Contains("pipe deadline", error.ToString());
        Assert.Equal("error", Json(output.ToString()).GetProperty("result").GetString());
    }

    [Theory, InlineData("Old name", null), InlineData("12345678", null), InlineData(" Copilot: 1234567890abcdef Status ", null), InlineData(Node, "VM-A")]
    public async Task Invalid_id_or_named_machine_selectors_never_open_a_pipe(string id, string? machine)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var args = new List<string> { "sessions", "find", "--session-id", id };
        if (machine is not null) { args.AddRange(["--machine", machine]); }
        Assert.Equal(2, await SessionsFindCommands.RunAsync(args, Path.GetTempPath(), output, error,
            _ => throw new Exception("must not connect")));
        Assert.Contains("refused:", error.ToString());
        Assert.Contains(id, error.ToString());
    }

    [Theory, InlineData(false, false), InlineData(false, true), InlineData(true, false), InlineData(true, true)]
    public async Task Returned_successors_and_unpublished_uuids_cannot_retarget(bool ambiguous, bool full)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var id = full ? Uuid : Node;
        var wrong = full ? Session : Session.Replace(Node, "agent_bridge_fedcba0987654321", StringComparison.Ordinal);
        var response = ambiguous ? "{\"result\":\"ambiguous\",\"fleetScope\":\"account\",\"candidates\":[" + Session + "," + wrong + "]}"
            : "{\"result\":\"found\",\"fleetScope\":\"account\",\"session\":" + wrong + "}";
        Assert.Equal(5, await SessionsFindCommands.ExecuteAsync(["sessions", "find", "--session-id", id], output, error,
            "fixture-token", (op, _) => Task.FromResult(Json(op == "fleet.find" ? response : "{}"))));
        Assert.Equal("", output.ToString());
        Assert.Contains(id, error.ToString());
    }

    [Fact]
    public async Task A_device_pin_survives_a_display_rename_and_uuid_is_explicitly_bound()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var row = Session.Replace("VM-A", "Renamed", StringComparison.Ordinal).Replace("\"node\":", $"\"sessionId\":\"{Uuid}\",\"node\":", StringComparison.Ordinal);
        Assert.Equal(0, await SessionsFindCommands.ExecuteAsync(["sessions", "find", "--session-id", Uuid, "--machine", Device], output, error,
            "fixture-token", (op, _) => Task.FromResult(Json(op == "fleet.find" ? "{\"result\":\"found\",\"fleetScope\":\"account\",\"session\":" + row + "}" : "{}"))));
        Assert.Contains(Node, output.ToString());
        Assert.Contains("Renamed", output.ToString());
    }
}
