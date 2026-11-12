using System.Text.Json;
using AgentBridge.Protocol;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class AgentAdoptionCommandTests
{
    [Theory]
    [InlineData("adopt", AgentProtocol.Adopt)]
    [InlineData("revoke", AgentProtocol.Revoke)]
    public async Task Person_commands_use_the_requested_profile_and_session(string verb, string expected)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var calls = 0;
        var code = await AgentAdoptionCommands.RunAsync(["agents", verb, "sess-1", "--profile", "fixture"], _ => false,
            (profile, op, data) =>
            {
                calls++;
                Assert.Equal(("fixture", expected), (profile, op));
                Assert.Equal("sess-1", JsonSerializer.SerializeToElement(data, ProtocolJson.Options).GetProperty("session").GetString());
                return Task.FromResult(JsonSerializer.SerializeToElement(new { session = "sess-1", revoked = true }));
            }, output, error);
        Assert.Equal(0, code);
        Assert.Equal(1, calls);
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public async Task Credential_prints_only_the_claimed_token_and_does_not_invoke_the_person_guard()
    {
        var token = AgentProtocol.TokenPrefix + new string('A', 43);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await AgentAdoptionCommands.RunAsync(["agents", "credential"],
            _ => throw new InvalidOperationException("credential is for the adopted session"), (profile, op, _) =>
            {
                Assert.Equal(AgentProtocol.Credential, op);
                return Task.FromResult(JsonSerializer.SerializeToElement(new AgentCredentialResult(token, "agent:machine1/sess-1", "read", DateTimeOffset.UtcNow), ProtocolJson.Options));
            }, output, error));
        Assert.Equal(token + Environment.NewLine, output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData("adopt", true, 4)]
    [InlineData("revoke", true, 4)]
    [InlineData("invalid", false, 2)]
    [InlineData("credential", false, 2)]
    public async Task Refused_or_invalid_commands_never_open_a_pipe(string verb, bool insideAgent, int expected)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(expected, await AgentAdoptionCommands.RunAsync(["agents", verb, "sess-1"], _ => insideAgent,
            (_, _, _) => throw new InvalidOperationException("must not connect"), output, error));
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public async Task A_host_refusal_exits_nonzero_without_success_output()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await AgentAdoptionCommands.RunAsync(["agents", "credential"], _ => false,
            (_, _, _) => throw new IOException("refused: no adoption window"), output, error));
        Assert.Equal("", output.ToString());
        Assert.Contains("no adoption window", error.ToString());
    }

    [Fact]
    public async Task An_invalid_profile_is_refused_before_any_connection_or_person_check()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await AgentAdoptionCommands.RunAsync(["agents", "adopt", "sess-1", "--profile", "..\\outside"],
            _ => throw new InvalidOperationException("must not check"), (_, _, _) => throw new InvalidOperationException("must not connect"), output, error));
        Assert.Equal("", output.ToString());
        Assert.Contains("usage:", error.ToString());
    }

    [Fact]
    public async Task Revoking_an_absent_adoption_is_not_reported_as_success()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await AgentAdoptionCommands.RunAsync(["agents", "revoke", "sess-1"], _ => false,
            (_, _, _) => Task.FromResult(JsonSerializer.SerializeToElement(new AgentRevokeResult("sess-1", false), ProtocolJson.Options)), output, error));
        Assert.Equal("", output.ToString());
        Assert.Contains("no adoption or credential", error.ToString());
    }
}
