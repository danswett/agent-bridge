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
    public async Task A_profile_too_deep_for_a_socket_address_listens_in_the_users_runtime_folder()
    {
        // Pipe names have no such limit.
        if (OperatingSystem.IsWindows()) { return; }
        var before = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", folder);
        try
        {
            var deep = Path.Combine(folder, new string('d', 90), new string('e', 90));
            var endpoint = LocalEndpoint.For(deep);

            Assert.StartsWith(folder, endpoint.Address);
            Assert.True(LocalEndpoint.Fits(endpoint.Address));
            Assert.Equal(endpoint.Address, LocalEndpoint.For(deep).Address);
            Assert.NotEqual(endpoint.Address, LocalEndpoint.For(deep + "x").Address);
            Assert.Equal(Path.Combine(folder, "host.sock"), Endpoint.Address);

            var server = LocalServer.Start(endpoint, Echo);
            owned.Add(server);
            var ch = await LocalClient.ConnectAsync(endpoint, Patience);
            owned.Add(ch);
            Assert.Equal("Here", (await ch.HelloAsync()).Machine.Name);
        }
        finally { Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", before); }
    }

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

    // A client that speaks frames by hand, over the real client's connection. Not a started
    // channel's stream: its reader disposed the stream when the host hung up on a bad frame,
    // sometimes before the test's flush of that frame had returned, which then threw
    // ObjectDisposedException.
    async Task<Stream> RawAsync()
    {
        var stream = await LocalClient.ConnectStreamAsync(Endpoint, Patience);
        owned.Add(stream);
        return stream;
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
    public async Task A_client_told_to_wait_without_limit_connects_to_a_host_that_starts_later()
    {
        // A pipe client waits for its pipe; a Unix socket client finds no socket and is told at once.
        if (!OperatingSystem.IsWindows()) { return; }
        using var cts = new CancellationTokenSource(Patience);
        var connecting = LocalClient.ConnectAsync(Endpoint, Timeout.InfiniteTimeSpan, cts.Token);
        await Task.Delay(200);
        Assert.False(connecting.IsCompleted);

        Serve(Echo);
        var ch = await connecting;
        owned.Add(ch);
        Assert.True(ch.IsOpen);
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

    [Fact]
    public async Task The_host_sees_the_connecting_process_and_what_it_runs_inside_nearest_first()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        PipePeer? seen = null;
        Serve((ch, op, data) =>
        {
            seen = (ch as LocalChannel)?.Peer;
            return Task.FromResult<object?>(new { ok = true });
        });
        var ch = await ConnectAsync();

        await ch.CallAsync("who");

        var chain = seen!.Chain;
        Assert.Equal(Environment.ProcessId, chain[0].Id);
        Assert.Equal(Environment.ProcessPath, chain[0].Image, ignoreCase: true);
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        Assert.True(Math.Abs((chain[0].StartedAt!.Value - self.StartTime.ToUniversalTime()).TotalSeconds) < 1);
        // Something started this test; no parent started after its child.
        Assert.True(chain.Count >= 2);
        for (var i = 1; i < chain.Count; i++)
        {
            if (chain[i].StartedAt is { } parent && chain[i - 1].StartedAt is { } child) { Assert.True(parent <= child.AddSeconds(1)); }
        }
    }

    [Fact]
    public void A_process_that_has_gone_has_no_chain()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) { return; }
        int id;
        using (var gone = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "true", OperatingSystem.IsWindows() ? "/c exit" : "") { UseShellExecute = false, CreateNoWindow = true })!)
        {
            gone.WaitForExit();
            id = gone.Id;
        }
        // Having exited is not yet having left the process table, which is what ProcessChain
        // reads: a CI runner's snapshot still listed the cmd.exe just waited for, under this
        // testhost, though it could no longer be opened. Gone is when the table says so.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (Listed(id) && DateTime.UtcNow < deadline) { Thread.Sleep(20); }

        Assert.Empty(ProcessChain.Of(id));
    }

    static bool Listed(int id) => System.Diagnostics.Process.GetProcesses().Count(p => { using (p) { return p.Id == id; } }) > 0;
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

// Ancestry from a whole-process table, as macOS's ps gives it (and Windows' snapshot feeds the
// same walk), without depending on what really runs the tests.
public sealed class ProcessAncestryTests
{
    [Fact]
    public void Macs_ps_listing_is_read_with_paths_that_hold_spaces_and_local_start_times()
    {
        var table = ProcessChain.ParsePs("""
                1     0 Mon Oct 26 08:00:01 2026     /sbin/launchd
              612     1 Thu Oct 29 09:15:00 2026     /System/Applications/Utilities/Terminal.app/Contents/MacOS/Terminal
              640   612 Thu Oct 29 09:15:02 2026     -zsh
              700   640 Thu Oct 29 10:01:44 2026     /Users/me/.local/bin/copilot
              733   700 Thu Oct 29 10:02:10 2026     /Applications/Visual Studio Code.app/Contents/MacOS/Electron
            garbage line
            """);

        Assert.Equal(5, table.Count);
        Assert.Equal("/Applications/Visual Studio Code.app/Contents/MacOS/Electron", table[733].Image);
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 10, 29, 10, 1, 44, DateTimeKind.Local)).ToUniversalTime(), table[700].StartedAt);
        var chain = ProcessChain.Walk(733, table);
        Assert.True(chain.Complete);
        Assert.Equal([733, 700, 640, 612, 1], chain.Processes.Select(p => p.Id));
    }

    [Fact]
    public void A_walk_ends_at_a_parent_that_has_gone_or_started_after_its_child_and_knows_nothing_of_a_process_not_listed()
    {
        var t = new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);
        var table = new Dictionary<int, ProcessChain.Listed>
        {
            [30] = new(25, t.AddMinutes(5), "host"),
            [25] = new(20, t.AddMinutes(9), "reused"),
            [20] = new(1, t, "copilot"),
            [40] = new(39, t, "orphan"),
        };

        Assert.Equal([30], ProcessChain.Walk(30, table).Processes.Select(p => p.Id));
        Assert.True(ProcessChain.Walk(40, table).Complete);
        Assert.Equal(ProcessAncestry.Unknown, ProcessChain.Walk(99, table));
    }

    [Fact]
    public void A_chain_cut_off_at_the_depth_limit_is_incomplete_but_one_that_ends_there_is_not()
    {
        var t = new DateTimeOffset(2026, 10, 29, 10, 0, 0, TimeSpan.Zero);
        // 1 is the oldest; each started a minute after its parent.
        static Dictionary<int, ProcessChain.Listed> Line(int length, DateTimeOffset t) =>
            Enumerable.Range(1, length).ToDictionary(i => i, i => new ProcessChain.Listed(i - 1, t.AddMinutes(i), $"p{i}"));

        var cut = ProcessChain.Walk(40, Line(40, t));
        Assert.Equal(32, cut.Processes.Count);
        Assert.False(cut.Complete);

        var whole = ProcessChain.Walk(32, Line(32, t));
        Assert.Equal(32, whole.Processes.Count);
        Assert.True(whole.Complete);
    }
}

