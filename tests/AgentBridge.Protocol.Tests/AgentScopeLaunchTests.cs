using System.Text.Json;
using AgentBridge.Protocol;
using Xunit;

// #2777: the app's "May start agents" tick sends agentScope on a person's launch. One value exists; nothing else is a scope a launch can ask for.
public sealed class AgentScopeLaunchTests
{
    [Fact]
    public void The_one_scope_a_launch_can_ask_for_is_taken_and_survives_the_wire()
    {
        var request = new LaunchRequest { Request = Guid.NewGuid().ToString(), AgentScope = AgentProtocol.ScopeReadReplyLaunch };
        Assert.Null(LaunchProtocol.Check(request));
        var back = JsonSerializer.SerializeToElement(request, ProtocolJson.Options).Deserialize<LaunchRequest>(ProtocolJson.Options)!;
        Assert.Equal("read+reply+launch", back.AgentScope);
    }

    [Theory]
    [InlineData("")]
    [InlineData("read")]
    [InlineData("read+reply")]
    [InlineData("admin")]
    [InlineData("read+reply+launch ")]
    public void Any_other_value_is_refused_not_guessed(string scope) =>
        Assert.Contains("agent scope", LaunchProtocol.Check(new LaunchRequest { Request = Guid.NewGuid().ToString(), AgentScope = scope }));

    [Fact]
    public void An_agent_scope_is_for_a_new_session_only()
    {
        Assert.NotNull(LaunchProtocol.Check(new LaunchRequest { Request = Guid.NewGuid().ToString(), AgentScope = AgentProtocol.ScopeReadReplyLaunch, Resume = "11111111-1111-4111-8111-111111111111" }));
        Assert.NotNull(LaunchProtocol.Check(new LaunchRequest { Request = Guid.NewGuid().ToString(), AgentScope = AgentProtocol.ScopeReadReplyLaunch, Transfer = new LaunchTransfer { From = "host", Session = "s" } }));
    }
}
