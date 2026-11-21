using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Fleet.Grants;
using AgentBridge.Protocol;
using Xunit;

public sealed class FleetLaunchCommandsTests
{
    static readonly Guid Request = Guid.Parse("3f1c0a52-8d0e-4b8a-9a63-0c1e5f7a2b10");
    static readonly Guid Grant = Guid.Parse("b7e5a0de-4f3c-4a52-8e1f-2d9c6a1b7c33");
    static readonly Guid Session = Guid.Parse("e3d83a44-3d03-4e72-9f61-17d6b1cc897a");
    static FleetLaunchInput Input => new(1, Request, Grant, "SG Builder 266", "repo", "claude");
    static string[] Flags => ["fleet", "launch", "--request", Request.ToString(), "--grant", Grant.ToString(),
        "--name", Input.Name, "--workspace", "repo", "--agent", "claude"];
    static FleetLaunchReceipt Receipt(bool registered) => new(Request, Grant, 1,
        new(LaunchOutcome.Started) { Session = Session.ToString() }, "ask", registered, new(1, 2, 1), new(3, 10, 6));
    static async Task<(int Exit, JsonElement Json, string Error)> Run(string[] args,
        Func<string, object?, Task<JsonElement>> call, string? token = "fixture", string input = "")
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await FleetLaunchCommands.RunAsync(args, "unused", output, error, token, call, new StringReader(input));
        using var parsed = JsonDocument.Parse(output.ToString());
        return (exit, parsed.RootElement.Clone(), error.ToString());
    }

    [Theory]
    [InlineData("--request", "duplicate")]
    [InlineData("--unknown", "ignored")]
    public async Task Bad_flags_emit_one_object_without_contacting_the_host(string flag, string value)
    {
        var result = await Run([.. Flags, flag, value], (_, _) => throw new Exception("must not connect"));
        Assert.Equal(2, result.Exit);
        Assert.Equal("bad-argument", result.Json.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("version")]
    [InlineData("permissions")]
    [InlineData("resume")]
    [InlineData("request")]
    public async Task Strict_json_refuses_quoted_versions_unknown_modes_fields_and_missing_uuid(string field)
    {
        var json = JsonSerializer.SerializeToNode(Input, ProtocolJson.Options)!.AsObject();
        if (field == "request") { json.Remove(field); }
        else { json[field] = field == "version" ? "1" : field == "permissions" ? "bogus" : "unsupported"; }
        var result = await Run(["fleet", "launch", "--json", "-"], (_, _) => throw new Exception("must not connect"),
            input: json.ToJsonString());
        Assert.Equal(2, result.Exit);
        Assert.False(result.Json.GetProperty("uncertain").GetBoolean());
    }

    [Theory]
    [InlineData(false, 7)]
    [InlineData(true, 0)]
    public async Task Only_started_and_registered_confirms_with_full_short_ids_and_durable_remaining(bool registered, int exit)
    {
        var result = await Run(Flags, (op, _) => Task.FromResult(JsonSerializer.SerializeToElement(
            op == LaunchProtocol.Launch ? (object)Receipt(registered) : new { }, ProtocolJson.Options)));
        Assert.Equal(exit, result.Exit);
        Assert.Equal(Session.ToString(), result.Json.GetProperty("sessionId").GetString());
        Assert.Equal(Session.ToString("N")[..16], result.Json.GetProperty("session").GetString());
        Assert.Equal(registered ? "started" : "unconfirmed", result.Json.GetProperty("outcome").GetString());
        Assert.Equal(2, result.Json.GetProperty("remaining").GetProperty("hour").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_or_malformed_launch_response_is_uncertain_sanitized_and_never_retried(bool malformed)
    {
        var launches = 0;
        var result = await Run(Flags, (op, _) =>
        {
            if (op == LaunchProtocol.Launch)
            {
                launches++;
                if (!malformed) { throw new IOException("fixture-secret-do-not-print"); }
            }
            return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        });
        Assert.Equal(7, result.Exit);
        Assert.Equal(1, launches);
        Assert.True(result.Json.GetProperty("uncertain").GetBoolean());
        Assert.DoesNotContain("fixture-secret-do-not-print", result.Error);
    }

    [Fact]
    public async Task Host_and_launch_profiles_stay_distinct_and_prompt_is_sent_whole()
    {
        const string prompt = "first line\nsecond line";
        var result = await Run([.. Flags, "--profile", "receiver", "--launch-profile", "default", "--prompt", prompt],
            (op, data) =>
            {
                if (op == LaunchProtocol.Launch)
                {
                    var sent = Assert.IsType<FleetLaunchInput>(data);
                    Assert.Equal("default", sent.LaunchProfile);
                    Assert.Equal(prompt, sent.Prompt);
                }
                return Task.FromResult(JsonSerializer.SerializeToElement(op == LaunchProtocol.Launch
                    ? (object)Receipt(true) : new { }, ProtocolJson.Options));
            });
        Assert.Equal(0, result.Exit);
    }
}
