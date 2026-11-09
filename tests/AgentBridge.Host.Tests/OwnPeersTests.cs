using System.Security.Cryptography;
using AgentBridge.Transport;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class OwnPeersTests : IDisposable
{
    static readonly string Own = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    readonly string folder = Directory.CreateTempSubdirectory("own-peers-").FullName;
    readonly string peerFolder = Directory.CreateTempSubdirectory("own-peer-key-").FullName;
    bool on = true;

    public void Dispose() { foreach (var d in new[] { folder, peerFolder }) { try { Directory.Delete(d, true); } catch (IOException) { } } }

    static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    (string Id, string Code, byte[] Key) Peer(string? account = null)
    {
        using var k = AgentSignKey.OpenOrCreate(peerFolder, modeOn: true);
        var id = NewId();
        return (id, OwnPeerPin.Make(k, id, account ?? Own), k.PublicPoint);
    }

    OwnPeers Store(string? account = null) => new(folder, () => on, () => account ?? Own);

    (int Code, string Out, string Err) Run(string yes, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var code = OwnPeerCommands.Run(["peers", .. args], folder, () => on, new OwnPeerCommands.Local(NewId(), Own), () => yes, o, e);
        return (code, o.ToString(), e.ToString());
    }

    [Fact]
    public void An_unknown_peer_is_not_trusted_and_an_empty_list_trusts_nobody()
    {
        Assert.Empty(Store().List());
        Assert.Null(Store().TrustedKey(Peer().Id));
    }

    [Fact]
    public void A_confirmed_pin_shows_id_and_fingerprint_is_trusted_and_clear_removes_it_without_restart()
    {
        var (id, code, key) = Peer();
        var r = Run("yes", "pin", code, "--name", "laptop");
        Assert.Equal(0, r.Code);
        Assert.Contains(id, r.Out);
        Assert.Contains(OwnPeers.Fingerprint(key), r.Out);
        var live = Store();
        Assert.Equal(key, live.TrustedKey(id));
        Assert.Contains(id, Run("", "list").Out);
        Run("", "clear", id);
        Assert.Null(live.TrustedKey(id));
        Assert.Equal(1, Run("y", "pin", code).Code);
        Assert.Null(Store().TrustedKey(id));
    }

    [Fact]
    public void A_peer_of_another_account_or_with_a_bad_signature_is_refused()
    {
        var (_, other, _) = Peer(NewId());
        Assert.NotEqual(0, Run("yes", "pin", other).Code);
        Assert.Empty(Store().List());

        var (_, code, _) = Peer();
        var parts = code.Split('.');
        parts[0] = NewId();
        Assert.NotEqual(0, Run("yes", "pin", string.Join('.', parts)).Code);
        Assert.Empty(Store().List());
    }

    [Fact]
    public void Mode_off_reads_nothing_and_refuses_the_commands()
    {
        var (id, code, _) = Peer();
        Store().Pin(code, "p");
        on = false;
        Assert.Null(Store().TrustedKey(id));
        Assert.Empty(Store().List());
        Assert.Equal(3, Run("yes", "pin", code).Code);
        Assert.Equal(3, Run("", "show").Code);
        Assert.Contains("off", Run("", "list").Out);
    }

    [Fact]
    public void A_hand_edited_or_unsigned_entry_is_not_trusted()
    {
        var (id, code, _) = Peer();
        Store().Pin(code, "p");
        var path = Path.Combine(folder, OwnPeers.FileName);
        var text = File.ReadAllText(path);
        File.WriteAllText(path, text.Replace("\"name\": \"p\"", "\"name\": \"q\""));
        Assert.Null(Store().TrustedKey(id));
        File.WriteAllText(path, System.Text.RegularExpressions.Regex.Replace(text, "\"sig\": \"[^\"]*\"", "\"sig\": null"));
        Assert.Null(Store().TrustedKey(id));
        Assert.True(Store().Damaged);
    }

    [Fact]
    public void Mode_off_creates_no_key_and_writes_nothing()
    {
        on = false;
        Run("yes", "pin", Peer().Code);
        Run("", "show");
        Assert.False(File.Exists(AgentSignKey.PathOf(folder)));
        Assert.False(File.Exists(Path.Combine(folder, OwnPeers.FileName)));
    }

    [Fact]
    public void A_tampered_file_fails_closed_and_says_so_until_cleared()
    {
        var (id, code, _) = Peer();
        Store().Pin(code, "p");
        var path = Path.Combine(folder, OwnPeers.FileName);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"version\": 1", "\"version\": 2"));
        Assert.Null(Store().TrustedKey(id));
        Assert.Contains("damaged", Run("", "list").Out);
        Assert.NotEqual(0, Run("yes", "pin", code).Code);
        Run("", "clear");
        Assert.Contains("No peers", Run("", "list").Out);
    }

    [Theory]
    [InlineData("peers", "pin", true)]
    [InlineData("peers", "clear", true)]
    [InlineData("peers", "show", true)]
    [InlineData("peers", "list", false)]
    [InlineData("config", "pin", false)]
    public void Only_peers_writes_are_refused_inside_an_agent_session(string a, string b, bool writes) =>
        Assert.Equal(writes, OwnPeerCommands.Writes([a, b]));
}
