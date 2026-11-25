using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Fleet;
using AgentBridge.Roster;
using Xunit;

// #2643 C4: what the fleet roster reader does with a missing pin and with a pin it cannot use.
// A host with no pin reads the registry unverified (documented); a pin that exists but cannot supply
// a verified roster must let no machine into the list, and must not keep serving machines it cached before.
public sealed class RelayFleetRosterPinTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "abpin-" + Guid.NewGuid().ToString("N")[..8]);
    const string Node = "agent_bridge_1234567890abcdef";
    static readonly DateTimeOffset Started = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    public RelayFleetRosterPinTests() => Directory.CreateDirectory(root);

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    static JsonElement Frame() => JsonSerializer.SerializeToElement(new
    {
        machine = "a",
        online = true,
        record = new { sessions = new[] { new { node = Node, name = "SG Quality", state = "idle", since = Started } } },
    });

    static InvalidOperationException Refusal() => new("refused: this host's pinned device roster is unreadable, another account's or not yet verified");

    [Fact]
    public void A_host_with_no_pin_reads_the_registry_unverified()
    {
        foreach (var reader in new[] { new RelayFleetRoster(), new RelayFleetRoster(() => null) })
        {
            var machine = Assert.Single(reader.Read([Frame()]));
            Assert.False(machine.Verified);
            Assert.Equal(Node, Assert.Single(machine.Sessions).Node);
        }
    }

    [Fact]
    public void A_pin_that_cannot_supply_a_roster_lets_no_machine_into_the_list()
    {
        var reader = new RelayFleetRoster(() => throw Refusal());
        Assert.Throws<InvalidOperationException>(() => reader.Read([Frame()]));
    }

    [Fact]
    public void A_pin_that_turns_unusable_stops_the_reader_serving_machines_it_cached_before()
    {
        var usable = true;
        var reader = new RelayFleetRoster(() => usable ? null : throw Refusal());
        Assert.Single(reader.Read([Frame()]));
        usable = false;
        Assert.Throws<InvalidOperationException>(() => reader.Read([Frame()]));
        Assert.Throws<InvalidOperationException>(() => reader.Read([]));
    }

    [Fact]
    public void An_unreadable_pin_file_refuses_the_real_membership_while_a_missing_one_reads_unverified()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var pin = Path.Combine(root, "roster-pin.json");
        var trust = new FleetTrust(new HostRosterReader(pin, HostRoster.Now), root, () => "account-a");
        var reader = new RelayFleetRoster(trust.Membership);

        Assert.False(Assert.Single(reader.Read([Frame()])).Verified);

        File.WriteAllText(pin, "{broken");
        Assert.Throws<InvalidOperationException>(() => reader.Read([Frame()]));
    }
}
