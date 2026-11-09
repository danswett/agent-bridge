using System.Text.Json.Nodes;
using AgentBridge.Host.Lite;
using AgentBridge.Transport;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class OwnLitePeersTests : IDisposable
{
    const string Account = "12345678";
    readonly List<string> folders = [];
    readonly List<AgentSignKey> keys = [];
    readonly string folder;

    public OwnLitePeersTests() { folder = Temp(); }

    string Temp() { var d = Directory.CreateTempSubdirectory("own-lite-").FullName; folders.Add(d); return d; }

    public void Dispose()
    {
        foreach (var k in keys) { k.Dispose(); }
        foreach (var d in folders) { try { Directory.Delete(d, true); } catch (IOException) { } }
    }

    // Pins a fresh peer (its own key) and returns its device id and signing point.
    (string Id, byte[] Point) Pin(OwnPeers store, byte fill, string account = Account)
    {
        var k = AgentSignKey.OpenOrCreate(Temp(), modeOn: true);
        keys.Add(k);
        var id = Wire.DeviceId(Enumerable.Repeat(fill, 65).ToArray());
        store.Pin(OwnPeerPin.Make(k, id, account), "peer" + fill);
        return (id, k.PublicPoint);
    }

    OwnPeers Store(Func<bool>? on = null, string? account = Account) => new(folder, on ?? (() => true), () => account);

    string FilePath => Path.Combine(folder, OwnPeers.FileName);

    static void AssertClosed(OwnLitePeers peers, params string[] ids)
    {
        foreach (var id in ids) { Assert.False(peers.TryFind(id, out _)); }
        Assert.Empty(peers.All());
    }

    [Fact]
    public void The_receiver_sees_only_pinned_peers_of_the_account_and_nothing_when_the_mode_is_off()
    {
        var on = true;
        var store = Store(() => on);
        var peers = new OwnLitePeers(store);
        Assert.Empty(peers.All());
        var (id, point) = Pin(store, 1);
        Assert.True(peers.TryFind(id, out var found));
        Assert.Equal(point, found.SignPoint);
        Assert.Single(peers.All());
        on = false;
        AssertClosed(peers, id);
    }

    [Fact]
    public void An_edited_key_or_account_a_dropped_signature_or_garbage_in_the_file_trusts_nobody()
    {
        var store = Store();
        var peers = new OwnLitePeers(store);
        var (id, _) = Pin(store, 1);
        var (id2, other) = Pin(store, 2);
        Assert.Equal(2, peers.All().Count);
        var good = File.ReadAllText(FilePath);

        var swapped = JsonNode.Parse(good)!;
        swapped["peers"]![0]!["publicKey"] = BlobFormat.Base64Url(other);
        File.WriteAllText(FilePath, swapped.ToJsonString());
        AssertClosed(peers, id, id2);

        var account = JsonNode.Parse(good)!;
        account["peers"]![0]!["account"] = "87654321";
        File.WriteAllText(FilePath, account.ToJsonString());
        AssertClosed(peers, id, id2);

        var unsigned = JsonNode.Parse(good)!;
        unsigned.AsObject().Remove("sig");
        File.WriteAllText(FilePath, unsigned.ToJsonString());
        AssertClosed(peers, id, id2);

        File.WriteAllText(FilePath, "{ not json");
        AssertClosed(peers, id, id2);

        File.WriteAllText(FilePath, good);
        Assert.Equal(2, peers.All().Count);
    }

    [Fact]
    public void A_peer_pinned_under_another_account_is_excluded()
    {
        var (id, _) = Pin(Store(), 1);
        AssertClosed(new OwnLitePeers(Store(account: "99999999")), id);
        AssertClosed(new OwnLitePeers(Store(account: null)), id);
    }

    [Fact]
    public void A_wrong_or_odd_case_device_id_finds_neither_of_two_pinned_peers()
    {
        var store = Store();
        var peers = new OwnLitePeers(store);
        var (a, _) = Pin(store, 1);
        var (b, _) = Pin(store, 2);
        Assert.Equal(2, peers.All().Count);
        foreach (var bad in new[] { a.ToUpperInvariant(), b.ToUpperInvariant(), "", a[..^1], a + "0", new string('0', 32) })
        {
            Assert.False(peers.TryFind(bad, out var none));
            Assert.Null(none);
        }
        Assert.True(peers.TryFind(a, out var found));
        Assert.Equal(a, found.DeviceId);
    }

    [Fact]
    public void A_failing_mode_or_account_lookup_gives_no_peers_instead_of_throwing()
    {
        var (id, _) = Pin(Store(), 1);
        AssertClosed(new OwnLitePeers(Store(() => throw new IOException("config unreadable"))), id);
        AssertClosed(new OwnLitePeers(new OwnPeers(folder, () => true, () => throw new InvalidDataException("bad"))), id);
    }

    [Fact]
    public void A_damaged_host_key_leaves_a_signed_list_untrusted()
    {
        var store = Store();
        var peers = new OwnLitePeers(store);
        var (id, _) = Pin(store, 1);
        Assert.Single(peers.All());
        File.WriteAllBytes(AgentSignKey.PathOf(folder), [1, 2, 3]);
        AssertClosed(peers, id);
    }
}
