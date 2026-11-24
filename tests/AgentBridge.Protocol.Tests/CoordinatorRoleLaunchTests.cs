using System.Text.Json;
using AgentBridge.Protocol;
using Xunit;

// #4318: a coordinator role a person's launch carries (the app's Role box). Not the worker role, which also narrows MCP servers.
public sealed class CoordinatorRoleLaunchTests
{
    [Theory]
    [InlineData("quality-lead")]
    [InlineData("SRE")]
    [InlineData("a")]
    public void A_plain_role_name_is_taken_and_survives_the_wire(string role)
    {
        var request = new LaunchRequest { Request = Guid.NewGuid().ToString(), CoordinatorRole = role };
        Assert.Null(LaunchProtocol.Check(request));
        var back = JsonSerializer.SerializeToElement(request, ProtocolJson.Options).Deserialize<LaunchRequest>(ProtocolJson.Options)!;
        Assert.Equal(role, back.CoordinatorRole);
    }

    [Theory]
    [InlineData("")]
    [InlineData("two words")]
    [InlineData("../x")]
    [InlineData("tab\there")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890123456789")]
    public void An_unknown_or_malformed_value_is_refused_not_guessed(string role) =>
        Assert.Contains("coordinator role", LaunchProtocol.Check(new LaunchRequest { Request = Guid.NewGuid().ToString(), CoordinatorRole = role }));

    [Fact]
    public void A_coordinator_role_is_for_a_new_session_only()
    {
        Assert.NotNull(LaunchProtocol.Check(new LaunchRequest { Request = Guid.NewGuid().ToString(), CoordinatorRole = "sre", Resume = "11111111-1111-4111-8111-111111111111" }));
        Assert.NotNull(LaunchProtocol.Check(new LaunchRequest { Request = Guid.NewGuid().ToString(), CoordinatorRole = "sre", Transfer = new LaunchTransfer { From = "host", Session = "s" } }));
    }
}
