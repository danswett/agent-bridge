using Xunit;

namespace AgentBridge.Transport.Tests;

public class RelayRosterReadTests
{
    [Fact]
    [Trait("Category", "Network")]
    public async Task A_device_reads_the_relay_s_copy_of_the_roster_and_an_account_with_none_gets_an_empty_one()
    {
        await using var relay = await TestRelay.StartAsync();
        var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "desk", "host");

        var roster = await host.Relay.GetRosterAsync(CancellationToken.None);

        Assert.NotNull(roster);
        Assert.Equal("observe", roster!.Mode);
        Assert.Empty(roster.Entries);
    }
}
