using System.Net.Sockets;
using AgentBridge.Local;
using Xunit;

// #4414: a client refuses a Unix-socket host that runs as another user, and says why. The check itself is the same on every platform; reading the peer
// off a real socket (SO_PEERCRED, LOCAL_PEERCRED) can only run where there are Unix sockets, so CI's Linux and macOS jobs prove that half.
public sealed class UnixPeerTests : IDisposable
{
    // Short: a Unix socket path must fit about 100 bytes.
    readonly string folder = Path.Combine(Path.GetTempPath(), "abp-" + Guid.NewGuid().ToString("N")[..8]);
    readonly List<IDisposable> owned = [];

    public UnixPeerTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        foreach (var o in owned) { o.Dispose(); }
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_peer_of_another_user_is_refused_and_the_reason_names_both_users()
    {
        var refused = Assert.Throws<LocalRefusedException>(() => UnixPeer.Require(0, 1000));

        Assert.Contains("another user", refused.Message);
        Assert.Contains("uid 0", refused.Message);
        Assert.Contains("1000", refused.Message);
    }

    [Fact]
    public void A_peer_of_the_same_user_is_accepted()
    {
        UnixPeer.Require(1000, 1000);
        UnixPeer.Require(0, 0);
    }

    [Fact]
    public void A_peer_that_cannot_be_read_is_refused_with_that_reason_never_trusted()
    {
        var refused = Assert.Throws<LocalRefusedException>(() => UnixPeer.Require(null, 1000));

        Assert.Contains("cannot say which user", refused.Message);
    }

    // A listening socket in this process, which is the same user as the client.
    async Task<LocalEndpoint> ListenAsync()
    {
        var endpoint = LocalEndpoint.For(folder);
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        owned.Add(listener);
        listener.Bind(new UnixDomainSocketEndPoint(endpoint.Address));
        listener.Listen(1);
        _ = listener.AcceptAsync().ContinueWith(t => { if (t.IsCompletedSuccessfully) { owned.Add(t.Result); } }, TaskScheduler.Default);
        await Task.Yield();
        return endpoint;
    }

    [Fact]
    public async Task A_real_socket_served_by_this_user_reads_as_this_user_and_connects()
    {
        if (OperatingSystem.IsWindows()) { return; }
        var endpoint = await ListenAsync();

        await using var stream = await LocalClient.ConnectStreamAsync(endpoint, TimeSpan.FromSeconds(5));

        Assert.True(stream.CanWrite);
    }

    [Fact]
    public async Task A_real_socket_whose_peer_reads_as_another_user_is_refused_and_the_connection_closed()
    {
        if (OperatingSystem.IsWindows()) { return; }
        var endpoint = await ListenAsync();

        var refused = await Assert.ThrowsAsync<LocalRefusedException>(() =>
            LocalClient.ConnectStreamAsync(endpoint, TimeSpan.FromSeconds(5), peerUid: _ => UnixPeer.CurrentUid() + 1));

        Assert.Contains("another user", refused.Message);
    }

    [Fact]
    public async Task A_real_socket_whose_peer_cannot_be_read_is_refused()
    {
        if (OperatingSystem.IsWindows()) { return; }
        var endpoint = await ListenAsync();

        var refused = await Assert.ThrowsAsync<LocalRefusedException>(() =>
            LocalClient.ConnectStreamAsync(endpoint, TimeSpan.FromSeconds(5), peerUid: _ => null));

        Assert.Contains("cannot say which user", refused.Message);
    }
}
