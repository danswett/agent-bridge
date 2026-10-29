using System.Buffers.Binary;
using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

// The local pipe itself: framing, a server and a client over a real pipe (a Unix socket off
// Windows), who is let in, and what a broken peer does to a connection. The host's services
// over it are in AgentBridge.Host.Tests (LocalApiTests).
public sealed class LocalPipeTests : IAsyncDisposable
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    // Short: a Unix socket path must fit about 100 bytes.
    readonly string folder = Path.Combine(Path.GetTempPath(), "abl-" + Guid.NewGuid().ToString("N")[..8]);
    readonly List<IAsyncDisposable> owned = [];

    public LocalPipeTests() => Directory.CreateDirectory(folder);

    public async ValueTask DisposeAsync()
    {
        foreach (var o in owned) { await o.DisposeAsync(); }
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    LocalEndpoint Endpoint => LocalEndpoint.For(folder);

    LocalServer Serve(Func<IProtocolChannel, string, JsonElement, Task<object?>> handler)
    {
        var server = LocalServer.Start(Endpoint, handler);
        owned.Add(server);
        return server;
    }

    async Task<LocalChannel> ConnectAsync()
    {
        var ch = await LocalClient.ConnectAsync(Endpoint, Patience);
        owned.Add(ch);
        return ch;
    }

    static Task<object?> Echo(IProtocolChannel ch, string op, JsonElement data) => Task.FromResult<object?>(op switch
    {
        "echo" => new { got = data, peer = ch.PeerKind },
        "boom" => throw new InvalidOperationException("it broke"),
        "big" => new { text = new string('x', 70_000) },
        "subscribe" => new ChannelReply(new { count = 2 }, async c =>
        {
            await c.SendEventAsync("tick", new { n = 1 });
            await c.SendEventAsync("tick", new { n = 2 });
        }),
        Ops.Hello => new HelloResult(1, new MachineInfo("id", "Here", "windows", "0.1.0"), [Capabilities.SessionsRead]),
        _ => null,
    });

    [Fact]
    public async Task A_request_gets_its_response_and_the_host_sees_a_local_peer()
    {
        Serve(Echo);
        var ch = await ConnectAsync();

        var result = await ch.CallAsync("echo", new { text = "héllo 🚀" });

        Assert.Equal("héllo 🚀", result.GetProperty("got").GetProperty("text").GetString());
        Assert.Equal("local", result.GetProperty("peer").GetString());
    }

    [Fact]
    public async Task Unknown_ops_and_failures_come_back_as_errors_and_the_connection_carries_on()
    {
        Serve(Echo);
        var ch = await ConnectAsync();

        var unknown = await ch.RequestAsync("nope", new { });
        Assert.False(unknown.GetProperty("ok").GetBoolean());
        Assert.Equal("unknown op nope", unknown.GetProperty("error").GetString());
        var failed = await Assert.ThrowsAsync<HostErrorException>(() => ch.CallAsync("boom"));
        Assert.Equal("it broke", failed.Message);
        var tooBig = await Assert.ThrowsAsync<HostErrorException>(() => ch.CallAsync("big"));
        Assert.StartsWith("response too large", tooBig.Message);

        Assert.True(ch.IsOpen);
        Assert.Equal(1, (await ch.CallAsync("echo", new { n = 1 })).GetProperty("got").GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task Events_that_follow_a_response_arrive_after_it_numbered_from_one()
    {
        Serve(Echo);
        var ch = await ConnectAsync();

        var result = await ch.CallAsync("subscribe");
        Assert.Equal(2, result.GetProperty("count").GetInt32());

        using var cts = new CancellationTokenSource(Patience);
        var first = await ch.Events.ReadAsync(cts.Token);
        var second = await ch.Events.ReadAsync(cts.Token);
        Assert.Equal(("tick", 1L, 1), (first.Name, first.Seq, first.Data.GetProperty("n").GetInt32()));
        Assert.Equal(("tick", 2L, 2), (second.Name, second.Seq, second.Data.GetProperty("n").GetInt32()));
    }

    [Fact]
    public async Task Many_requests_can_be_outstanding_at_once()
    {
        Serve(async (ch, op, data) =>
        {
            // The first waits longest, so responses come back out of order.
            await Task.Delay(TimeSpan.FromMilliseconds(200 - data.GetProperty("n").GetInt32() * 10));
            return new { n = data.GetProperty("n").GetInt32() };
        });
        var ch = await ConnectAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(n => ch.CallAsync("x", new { n })));

        Assert.Equal(Enumerable.Range(0, 10), results.Select(r => r.GetProperty("n").GetInt32()));
    }

    [Fact]
    public async Task A_message_over_the_limit_is_refused_before_it_is_written_and_the_connection_survives()
    {
        Serve(Echo);
        var ch = await ConnectAsync();

        await Assert.ThrowsAsync<MessageTooLargeException>(() => ch.RequestAsync("echo", new { text = new string('x', Frames.MaxMessageBytes) }));

        Assert.True(ch.IsOpen);
        Assert.Equal("ok", (await ch.CallAsync("echo", new { text = "ok" })).GetProperty("got").GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_frame_claiming_more_than_the_limit_ends_the_connection()
    {
        var accepted = new TaskCompletionSource<LocalChannel>();
        var server = Serve(Echo);
        server.Accepted += c => accepted.TrySetResult(c);
        var raw = await RawAsync();
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Frames.MaxMessageBytes + 1);
        await raw.WriteAsync(header);
        await raw.FlushAsync();

        var atHost = await accepted.Task.WaitAsync(Patience);
        await atHost.Closed.WaitAsync(Patience);
        Assert.IsType<InvalidDataException>(atHost.CloseReason);
    }

    [Fact]
    public async Task A_frame_that_is_not_json_ends_the_connection()
    {
        var accepted = new TaskCompletionSource<LocalChannel>();
        var server = Serve(Echo);
        server.Accepted += c => accepted.TrySetResult(c);
        var raw = await RawAsync();
        await Frames.WriteAsync(raw, "not json"u8.ToArray());

        var atHost = await accepted.Task.WaitAsync(Patience);
        await atHost.Closed.WaitAsync(Patience);
        Assert.False(atHost.IsOpen);
    }

    // A client that speaks frames by hand, through the real client's connection.
    async Task<Stream> RawAsync()
    {
        var ch = await ConnectAsync();
        return (Stream)typeof(LocalChannel).GetField("stream", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(ch)!;
    }

    [Fact]
    public void A_second_server_for_the_same_profile_is_refused()
    {
        Serve(Echo);

        Assert.Throws<EndpointInUseException>(() => LocalServer.Start(Endpoint, Echo));
    }

    [Fact]
    public async Task Connecting_where_no_host_listens_says_the_host_is_not_running()
    {
        await Assert.ThrowsAsync<HostNotRunningException>(() => LocalClient.ConnectAsync(Endpoint, TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public async Task A_refused_process_is_told_why_when_it_says_hello()
    {
        var server = Serve(Echo);
        server.Check = _ => "the host serves only the user it runs as";
        var ch = await ConnectAsync();

        var refused = await Assert.ThrowsAsync<LocalRefusedException>(() => ch.HelloAsync());

        Assert.Equal("the host serves only the user it runs as", refused.Message);
        await ch.Closed.WaitAsync(Patience);
    }

    [Fact]
    public async Task Closing_the_server_ends_its_connections_and_fails_what_is_outstanding()
    {
        var release = new TaskCompletionSource();
        var server = LocalServer.Start(Endpoint, async (_, _, _) => { await release.Task; return new { }; });
        var ch = await ConnectAsync();
        var pending = ch.RequestAsync("wait", new { }, TimeSpan.FromSeconds(30));

        await server.DisposeAsync();

        await Assert.ThrowsAsync<IOException>(() => pending);
        await ch.Closed.WaitAsync(Patience);
        release.TrySetResult();
    }

    [Fact]
    public void The_endpoint_is_the_profiles_own_and_differs_between_profiles()
    {
        var a = LocalEndpoint.For(Path.Combine(folder, "a"));
        var b = LocalEndpoint.For(Path.Combine(folder, "b"));

        Assert.NotEqual(a.Address, b.Address);
        Assert.Equal(a, LocalEndpoint.For(Path.Combine(folder, "a") + Path.DirectorySeparatorChar + "."));
        if (OperatingSystem.IsWindows()) { Assert.StartsWith("AgentBridge.Host.", a.Address); }
        else { Assert.Equal(Path.Combine(folder, "a", "host.sock"), a.Address); }
    }
}

public sealed class PeerPolicyTests
{
    const string Me = "S-1-5-21-1-2-3-1001";

    [Fact]
    public void The_same_user_at_the_same_elevation_is_let_in() =>
        Assert.Null(PeerPolicy.Refusal(new PipePeer(10, Me, false, "AgentBridge"), Me, selfElevated: false));

    [Fact]
    public void Another_user_is_refused() =>
        Assert.Equal("the host serves only the user it runs as", PeerPolicy.Refusal(new PipePeer(10, "S-1-5-21-1-2-3-1002", false, "x"), Me, false));

    [Fact]
    public void An_elevated_app_is_refused_by_an_unelevated_host_and_told_to_run_normally() =>
        Assert.Contains("app is running as administrator", PeerPolicy.Refusal(new PipePeer(10, Me, true, "x"), Me, selfElevated: false));

    [Fact]
    public void An_unelevated_app_is_refused_by_an_elevated_host() =>
        Assert.Contains("host is running as administrator", PeerPolicy.Refusal(new PipePeer(10, Me, false, "x"), Me, selfElevated: true));

    [Fact]
    public void A_process_the_host_cannot_identify_is_refused() =>
        Assert.Equal("the host could not verify the connecting process", PeerPolicy.Refusal(new PipePeer(10, null, null, null), Me, false));
}
