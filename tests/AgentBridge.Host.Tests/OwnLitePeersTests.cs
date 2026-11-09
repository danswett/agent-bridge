using AgentBridge.Host.Lite;
using AgentBridge.Transport;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class OwnLitePeersTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("own-lite-").FullName;
    readonly string peerFolder = Directory.CreateTempSubdirectory("own-lite-peer-").FullName;

    public void Dispose() { foreach (var d in new[] { folder, peerFolder }) { try { Directory.Delete(d, true); } catch (IOException) { } } }

    [Fact]
    public void The_receiver_sees_only_pinned_peers_of_the_account_and_nothing_when_the_mode_is_off()
    {
        var on = true;
        var id = Wire.DeviceId(new byte[65]);
        using var k = AgentSignKey.OpenOrCreate(peerFolder, modeOn: true);
        var store = new OwnPeers(folder, () => on, () => "12345678");
        var peers = new OwnLitePeers(store);
        Assert.False(peers.TryFind(id, out _));
        store.Pin(OwnPeerPin.Make(k, id, "12345678"), "laptop");
        Assert.True(peers.TryFind(id, out var found));
        Assert.Equal(k.PublicPoint, found.SignPoint);
        Assert.Single(peers.All());
        on = false;
        Assert.False(peers.TryFind(id, out _));
        Assert.Empty(peers.All());
    }
}
