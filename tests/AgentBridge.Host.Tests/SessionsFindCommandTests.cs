using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Local;
using Xunit;

public sealed class SessionsFindCommandTests
{
    static JsonElement Json(string json) => JsonSerializer.Deserialize<JsonElement>(json);
    const string Session = """{"node":"agent_bridge_1234567890abcdef","machine":"a","machineName":"VM-A","state":"idle","online":true,"trust":"own-account"}""";

    [Theory]
    [InlineData("""{"result":"found","fleetScope":"account","session":SESSION}""", 0)]
    [InlineData("""{"result":"ambiguous","fleetScope":"account","candidates":[SESSION,SESSION]}""", 4)]
    [InlineData("""{"result":"notFound","fleetScope":"account"}""", 3)]
    [InlineData("""{"result":"offline","fleetScope":"account"}""", 3)]
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
        var code = await SessionsFindCommands.ExecuteAsync(["sessions", "find", "--name", " SG  Lead ", "--machine", "VM-A", "--json"],
            output, error, "fixture-token", (op, data) =>
            {
                calls.Add(op);
                if (op == "fleet.find")
                {
                    var request = JsonSerializer.SerializeToElement(data);
                    Assert.Equal("SG Lead", request.GetProperty("name").GetString());
                    Assert.Equal("VM-A", request.GetProperty("machineName").GetString());
                }
                return Task.FromResult(op == "fleet.find" ? Json(response
                    .Replace("SESSION_WRONG_MACHINE", Session.Replace("VM-A", "VM-B", StringComparison.Ordinal), StringComparison.Ordinal)
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
            Assert.Equal("SG Lead", matches[0].GetProperty("name").GetString());
            Assert.Equal(new[] { "machine", "name", "node", "state" }, matches[0].EnumerateObject().Select(p => p.Name).Order());
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
        var code = await SessionsFindCommands.ExecuteAsync(["sessions", "find", "--name", "SG Lead"],
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
        Assert.Equal(4, await SessionsFindCommands.ExecuteAsync(["sessions", "find", "--name", "SG Lead"], output, error,
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
        var code = await SessionsFindCommands.RunAsync(["sessions", "find", "--name", "SG Lead", "--json"],
            Path.GetTempPath(), output, error, _ => throw new HostNotRunningException("host unavailable", new TimeoutException("pipe deadline")));
        Assert.Equal(6, code);
        Assert.Contains("connect", error.ToString());
        Assert.Contains("pipe deadline", error.ToString());
        Assert.Equal("error", Json(output.ToString()).GetProperty("result").GetString());
    }
}
