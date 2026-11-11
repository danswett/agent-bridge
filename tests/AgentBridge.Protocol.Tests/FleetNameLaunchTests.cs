using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;
using Xunit;

public sealed class FleetNameLaunchTests
{
    static JsonObject Data(string name = "SG Builder 1829") => new()
    {
        ["request"] = Guid.NewGuid().ToString(),
        ["fleetName"] = name,
    };

    [Theory]
    [InlineData("SG Chief of Staff")]
    [InlineData("SG Engineering Lead")]
    [InlineData("SG Quality Lead")]
    [InlineData("SG Product Lead")]
    [InlineData("SG Creative Lead")]
    [InlineData("SG Migration Lead")]
    [InlineData("SG SRE Lead")]
    [InlineData("SG Reviewer A")]
    [InlineData("SG Reviewer Z")]
    [InlineData("SG Builder 1")]
    [InlineData("SG Worker 9999")]
    public void A_launch_preserves_the_explicit_fleet_name_instead_of_silently_discarding_it(string name)
    {
        var request = Data(name).Deserialize<LaunchRequest>(ProtocolJson.Options)!;
        var sent = JsonSerializer.SerializeToElement(request, ProtocolJson.Options);
        Assert.True(sent.TryGetProperty("fleetName", out var value), "fleetName was silently discarded");
        Assert.Equal(name, value.GetString());
        Assert.Null(LaunchProtocol.Check(request));
    }

    [Theory]
    [InlineData("resume")]
    [InlineData("transfer")]
    public void A_fleet_name_cannot_be_applied_to_an_existing_or_transferred_session(string field)
    {
        var data = Data();
        if (field == "resume") { data["resume"] = Guid.NewGuid().ToString(); }
        else
        {
            data["workspace"] = "Bridge";
            data["transfer"] = new JsonObject
            {
                ["from"] = new string('a', 32),
                ["session"] = Guid.NewGuid().ToString(),
                ["command"] = new JsonObject { ["body"] = "A", ["by"] = "B", ["mac"] = "C" },
            };
        }
        var request = data.Deserialize<LaunchRequest>(ProtocolJson.Options)!;
        Assert.Contains("fleetName is only for a new session", LaunchProtocol.Check(request));
    }

    [Theory]
    [InlineData("7")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void A_wrong_type_fleet_name_is_refused_instead_of_becoming_an_unnamed_launch(string json)
    {
        var data = Data();
        data["fleetName"] = JsonNode.Parse(json);
        Assert.Throws<JsonException>(() => data.Deserialize<LaunchRequest>(ProtocolJson.Options));
    }
}
