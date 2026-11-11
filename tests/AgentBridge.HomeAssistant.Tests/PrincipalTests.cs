using System.Text;
using AgentBridge.HomeAssistant;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using Xunit;

public class PrincipalTests
{
    [Theory]
    [InlineData("agent-id", "homeassistant:agent-id")]
    [InlineData(" \tAgent-ID\r\n", "homeassistant:Agent-ID")]
    [InlineData("", "homeassistant:")]
    [InlineData(" \t\r\n", "homeassistant:")]
    public void Home_Assistant_principals_keep_the_existing_prefix_and_trim_only_the_id(string id, string expected)
    {
        Assert.Equal(expected, HaContract.HomeAssistantPrincipal(id));
    }

    [Theory]
    [InlineData("x")]
    [InlineData("\u00e9")]
    [InlineData("\U0001f600")]
    public void Home_Assistant_principals_keep_the_start_within_the_driver_principal_byte_limit(string character)
    {
        const string prefix = "homeassistant:";
        var id = string.Concat(Enumerable.Repeat(character, Limits.DriverPrincipal));
        var kept = (Limits.DriverPrincipal - Encoding.UTF8.GetByteCount(prefix)) / Encoding.UTF8.GetByteCount(character);

        var principal = HaContract.HomeAssistantPrincipal(" \t" + id + "\r\n");

        Assert.Equal(prefix + string.Concat(Enumerable.Repeat(character, kept)), principal);
        Assert.InRange(Encoding.UTF8.GetByteCount(principal), 1, Limits.DriverPrincipal);
    }

    [Fact]
    public void Generic_session_drivers_do_not_expose_a_Home_Assistant_member()
    {
        Assert.DoesNotContain(typeof(SessionDrivers).GetMembers(),
            member => member.Name.Contains("HomeAssistant", StringComparison.Ordinal));
    }
}
