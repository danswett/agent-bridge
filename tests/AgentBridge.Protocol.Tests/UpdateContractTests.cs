using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;
using Xunit;

public sealed class UpdateContractTests
{
    const string OldState = """{"current":"0.1.1","installer":true,"phase":"idle","automatic":false,"canUpdateNow":false,"requestNow":false}""";

    [Fact]
    public void An_old_update_state_round_trips_without_adding_fields() =>
        Assert.Equal(OldState, JsonSerializer.Serialize(JsonSerializer.Deserialize<UpdateState>(OldState, ProtocolJson.Options), ProtocolJson.Options));

    [Theory]
    [InlineData("bytes", "12")]
    [InlineData("total", "9007199254740991")]
    [InlineData("downloaded", "false")]
    [InlineData("downloaded", "true")]
    [InlineData("installing", "\"2026-11-12T05:00:00.000Z\"")]
    [InlineData("leaving", """{"to":"0.1.2","at":"2026-11-12T05:00:00.000Z","backBy":"2026-11-12T05:03:00.000Z"}""")]
    [InlineData("back", """{"from":"0.1.1","to":"0.1.2","request":"79d53b31-44f3-4f97-8d15-ec133618218a","at":"2026-11-12T05:01:00.000Z"}""")]
    [InlineData("mode", "\"restartToUpdate\"")]
    [InlineData("mode", "\"installBetweenTurns\"")]
    [InlineData("mode", "\"asSoonAsReady\"")]
    [InlineData("checksHeldUntil", "\"2026-11-12T06:00:00.000Z\"")]
    [InlineData("policy", """{"state":"ok","at":"2026-11-12T05:00:00.000Z"}""")]
    public void Each_optional_update_fact_survives_a_new_reader_and_is_ignored_by_an_old_one(string field, string value)
    {
        var json = JsonNode.Parse(OldState)!.AsObject();
        json[field] = JsonNode.Parse(value);
        var read = UpdateProtocol.Fit(JsonSerializer.Deserialize<UpdateState>(json, ProtocolJson.Options)!);
        var sent = JsonSerializer.SerializeToNode(read, ProtocolJson.Options)!;
        Assert.True(JsonNode.DeepEquals(json[field], sent[field]), $"{field} was lost or changed");
        Assert.Equal(new OldUpdateState("0.1.1", true, UpdatePhase.Idle, false),
            JsonSerializer.Deserialize<OldUpdateState>(sent, ProtocolJson.Options));
    }

    [Fact]
    public void Retry_and_hold_instants_are_sent_in_UTC_and_the_last_step_is_retained()
    {
        var result = JsonSerializer.Deserialize<UpdateCommandResult>("""
            {"outcome":"refused","code":"rate-limited","retryAt":"2026-11-11T22:00:00-08:00",
             "state":{"checksHeldUntil":"2026-11-11T23:00:00-08:00","last":{"outcome":"failed","step":"installing","at":"2026-11-12T05:00:00Z"}}}
            """, ProtocolJson.Options)!;
        var sent = JsonSerializer.SerializeToNode(result, ProtocolJson.Options)!;
        Assert.Equal("2026-11-12T06:00:00.000Z", sent["retryAt"]?.GetValue<string>());
        Assert.Equal("2026-11-12T07:00:00.000Z", sent["state"]?["checksHeldUntil"]?.GetValue<string>());
        Assert.Equal("installing", sent["state"]?["last"]?["step"]?.GetValue<string>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("builder")]
    public void Hello_distinguishes_a_known_role_or_no_role_from_an_older_hosts_absent_role(string role)
    {
        var json = """{"id":"m","name":"Desk","os":"windows","hostVersion":"0.1.1"}""";
        var old = JsonSerializer.Deserialize<MachineInfo>(json, ProtocolJson.Options)!;
        Assert.Null(JsonSerializer.SerializeToNode(old, ProtocolJson.Options)?["role"]);
        var node = JsonNode.Parse(json)!.AsObject();
        node["role"] = role;
        Assert.Equal(role, JsonSerializer.SerializeToNode(JsonSerializer.Deserialize<MachineInfo>(node, ProtocolJson.Options), ProtocolJson.Options)?["role"]?.GetValue<string>());
    }

    [Fact]
    public void Unknown_update_values_keep_the_least_promising_fallbacks()
    {
        var state = JsonSerializer.Deserialize<UpdateState>("""
            {"phase":"teleporting","mode":"later","policy":{"state":"unknown"},"last":{"outcome":"newer","at":"2026-11-12T05:00:00Z"}}
            """, ProtocolJson.Options)!;
        Assert.Equal(UpdatePhase.Idle, state.Phase);
        Assert.Equal(UpdateOutcome.Failed, state.Last!.Outcome);
        var sent = JsonSerializer.SerializeToNode(state, ProtocolJson.Options)!;
        Assert.Equal("restartToUpdate", sent["mode"]?.GetValue<string>());
        Assert.Equal("unreadable", sent["policy"]?["state"]?.GetValue<string>());
    }

    [Fact]
    public void Fitting_new_update_facts_keeps_existing_version_UUID_and_code_limits()
    {
        var longVersion = new string('v', UpdateProtocol.Version + 1);
        var state = UpdateProtocol.Fit(new UpdateState
        {
            Bytes = -1, Total = -2,
            Leaving = new(longVersion, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            Back = new(longVersion, longVersion, "not-a-uuid", DateTimeOffset.UnixEpoch),
            Last = new() { Outcome = UpdateOutcome.Failed, At = DateTimeOffset.UnixEpoch, Step = "not a code" },
        });
        Assert.Null(state.Bytes);
        Assert.Null(state.Total);
        Assert.Equal(UpdateProtocol.Version, state.Leaving!.To.Length);
        Assert.Equal((UpdateProtocol.Version, UpdateProtocol.Version, (string?)null), (state.Back!.From.Length, state.Back.To.Length, state.Back.Request));
        Assert.Null(state.Last!.Step);
    }

    sealed record OldUpdateState(string Current, bool Installer, UpdatePhase Phase, bool Automatic);
}
