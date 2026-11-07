using Xunit;

namespace AgentBridge.Roster.Tests;

public sealed class PinnedDevicesTests
{
    [Fact]
    public void The_query_lists_person_devices_with_their_presence_and_leaves_hosts_out()
    {
        var w = new World();
        w.Genesis();
        w.Add("+B", "G", "B", "G", presence: Presence.None);
        w.Add("+H", "G", "H", "+B", kind: "host", presence: Presence.None);
        var v = w.Verifier();

        var devices = PinnedDevices.Query(v);

        Assert.Equal(["G"], devices.Where(PinnedDevices.CanProvePresence).Select(d => w.NameOf(d.Id)));
        Assert.DoesNotContain(devices, d => d.Id == w.Id("H"));
        var b = Assert.Single(devices, d => d.Id == w.Id("B"));
        Assert.Equal(Presence.None, b.Presence);
        Assert.Equal(devices.OrderBy(d => d.Id, StringComparer.Ordinal), devices);
    }

    [Fact]
    public void A_removed_device_is_no_longer_pinned()
    {
        var w = new World();
        w.Genesis();
        w.Add("+B", "G", "B", "G");
        w.Remove("-B", "G", "+B", "+B");
        var devices = PinnedDevices.Query(w.Verifier());

        Assert.Equal([w.Id("G")], devices.Select(d => d.Id));
    }
}
