using System.Text.Json;
using AgentBridge.Host.Mcp;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

namespace AgentBridge.Host.Tests;

// C3 PR6 (docs/design/beta-messaging.md section 5): the .NET MCP server's end of the host's local pipe. It finds the host the way
// mcp/src/pipe.mjs does, says hello, and with the session's own AGENT_BRIDGE_TOKEN proves which agent it is. It has no other authority.
public sealed class McpHostTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "mcp-host-" + Guid.NewGuid().ToString("N"));
    public McpHostTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    static Func<string, string?> Env(params (string, string)[] values)
    {
        var map = values.ToDictionary(v => v.Item1, v => v.Item2);
        return key => map.GetValueOrDefault(key);
    }

    // --- finding the host ---

    [Fact]
    public void An_explicit_pipe_name_wins_and_a_full_windows_pipe_path_is_reduced_to_its_name()
    {
        Assert.Equal("my-pipe", McpEndpoint.Resolve(Env(("AGENT_BRIDGE_PIPE", "my-pipe"), ("AGENT_BRIDGE_ROOT", root)), windows: true)!.Address);
        Assert.Equal("my-pipe", McpEndpoint.Resolve(Env(("AGENT_BRIDGE_PIPE", @"\\.\pipe\my-pipe")), windows: true)!.Address);
        Assert.Equal("/run/user/1/host.sock", McpEndpoint.Resolve(Env(("AGENT_BRIDGE_PIPE", "/run/user/1/host.sock")), windows: false)!.Address);
    }

    [Fact]
    public void Without_one_it_is_the_host_of_the_root_and_profile_the_host_derives_for_itself()
    {
        var expected = LocalEndpoint.For(Path.Combine(root, "work"));
        Assert.Equal(expected.Address, McpEndpoint.Resolve(Env(("AGENT_BRIDGE_ROOT", root), ("AGENT_BRIDGE_PROFILE", "work")))!.Address);
        var fallback = LocalEndpoint.For(Path.Combine(root, "default"));
        Assert.Equal(fallback.Address, McpEndpoint.Resolve(Env(("AGENT_BRIDGE_ROOT", root)))!.Address);
        Assert.Equal(fallback.Address, McpEndpoint.Resolve(Env(("AGENT_BRIDGE_ROOT", root), ("AGENT_BRIDGE_PROFILE", "  ")))!.Address);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a b")]
    public void A_profile_that_is_not_a_profile_name_finds_no_host(string profile) =>
        Assert.Null(McpEndpoint.Resolve(Env(("AGENT_BRIDGE_ROOT", root), ("AGENT_BRIDGE_PROFILE", profile))));

    [Fact]
    public void The_token_is_the_sessions_own_variable_and_blank_is_none()
    {
        Assert.Equal("t0k", McpHost.TokenFrom(Env(("AGENT_BRIDGE_TOKEN", " t0k "))));
        Assert.Null(McpHost.TokenFrom(Env(("AGENT_BRIDGE_TOKEN", "   "))));
        Assert.Null(McpHost.TokenFrom(Env()));
    }

    // --- talking to it ---

    static object HelloAnswer(params string[] capabilities) => new
    {
        protocol = 1,
        machine = new { id = "00000000000000000000000000000001", name = "test", os = "windows", hostVersion = "0.0.0" },
        capabilities,
    };

    LocalServer Serve(Func<string, JsonElement, object?> answer, List<string>? seen = null) =>
        LocalServer.Start(LocalEndpoint.For(root), (ch, op, data) =>
        {
            seen?.Add(op);
            return Task.FromResult(answer(op, data));
        });

    static object? Normal(string op, JsonElement data) => op switch
    {
        "hello" => HelloAnswer("sessions.read", "sessions.reply"),
        "agent.hello" => new { principal = "agent:m/s", scope = "read+reply" },
        "sessions.list" => new { sessions = Array.Empty<object>() },
        _ => throw new InvalidOperationException("refused: not here"),
    };

    [Fact]
    public async Task With_no_host_listening_it_is_unavailable_and_says_so_without_naming_a_path()
    {
        var e = await Assert.ThrowsAsync<McpToolException>(() => McpHost.OpenAsync(LocalEndpoint.For(root), null, TimeSpan.FromMilliseconds(200)));
        Assert.Equal(McpFailure.Unavailable, e.Kind);
        Assert.DoesNotContain(root, e.Message);
    }

    [Fact]
    public async Task Without_a_token_it_says_hello_only_and_stays_unattributed()
    {
        var seen = new List<string>();
        await using var server = Serve(Normal, seen);
        await using var host = await McpHost.OpenAsync(server.Endpoint, null, TimeSpan.FromSeconds(5));
        Assert.Null(host.Principal);
        Assert.True(host.Has("sessions.read"));
        Assert.False(host.Has("sessions.launch"));
        Assert.Equal(["hello"], seen);
    }

    [Fact]
    public async Task With_a_token_it_proves_which_agent_it_is_and_sends_the_token_to_nobody_else()
    {
        var seen = new List<string>();
        string? sent = null;
        await using var server = Serve((op, data) =>
        {
            if (op == "agent.hello") { sent = data.GetProperty("token").GetString(); }
            return Normal(op, data);
        }, seen);
        await using var host = await McpHost.OpenAsync(server.Endpoint, "secret-token", TimeSpan.FromSeconds(5));
        Assert.Equal("agent:m/s", host.Principal);
        Assert.Equal("secret-token", sent);
        Assert.Equal(["hello", "agent.hello"], seen);
    }

    [Fact]
    public async Task A_host_that_refuses_the_token_fails_the_open_with_its_reason_and_never_echoes_the_token()
    {
        await using var server = Serve((op, data) => op == "agent.hello" ? throw new InvalidOperationException("refused: not an agent of this host") : Normal(op, data));
        var e = await Assert.ThrowsAsync<McpToolException>(() => McpHost.OpenAsync(server.Endpoint, "secret-token", TimeSpan.FromSeconds(5)));
        Assert.Equal(McpFailure.Refused, e.Kind);
        Assert.Contains("not an agent of this host", e.Message);
        Assert.DoesNotContain("secret-token", e.Message);
    }

    [Fact]
    public async Task A_request_the_host_refuses_is_refused_with_its_reason_and_one_it_answers_returns_the_result()
    {
        await using var server = Serve(Normal);
        await using var host = await McpHost.OpenAsync(server.Endpoint, "t", TimeSpan.FromSeconds(5));
        Assert.Equal(0, (await host.CallAsync("sessions.list")).GetProperty("sessions").GetArrayLength());
        var e = await Assert.ThrowsAsync<McpToolException>(() => host.CallAsync("host.stop"));
        Assert.Equal(McpFailure.Refused, e.Kind);
        Assert.Contains("not here", e.Message);
    }

    [Fact]
    public async Task A_host_that_does_not_answer_in_time_is_unavailable_and_so_is_one_that_has_gone()
    {
        var hold = new TaskCompletionSource();
        var server = LocalServer.Start(LocalEndpoint.For(root), async (ch, op, data) =>
        {
            if (op == "slow") { await hold.Task; }
            return Normal(op, data);
        });
        try
        {
            await using var host = await McpHost.OpenAsync(server.Endpoint, null, TimeSpan.FromSeconds(5));
            var slow = await Assert.ThrowsAsync<McpToolException>(() => host.CallAsync("slow", timeout: TimeSpan.FromMilliseconds(200)));
            Assert.Equal(McpFailure.Unavailable, slow.Kind);
            Assert.Contains("slow", slow.Message);
            hold.SetResult();
            await server.DisposeAsync();
            var gone = await Assert.ThrowsAsync<McpToolException>(() => host.CallAsync("sessions.list"));
            Assert.Equal(McpFailure.Unavailable, gone.Kind);
        }
        finally { hold.TrySetResult(); await server.DisposeAsync(); }
    }

    // --- C3 PR6b: pipe client hardening (Q10's read of PR6) ---

    // An explicit name is one pipe name in the local namespace, or a pipe path under \\.\pipe\ that reduces to one: never a path. On Windows
    // "..\UNC\host\pipe\x" normalises to \\.\UNC\host\pipe\x, a pipe on ANOTHER machine.
    [Theory]
    [InlineData("..")]
    [InlineData(@"..\UNC\host\pipe\x")]
    [InlineData(@"a\b")]
    [InlineData("a/b")]
    [InlineData(@"\\host\pipe\x")]
    [InlineData(@"\\.\pipe\..\x")]
    [InlineData(@"\\.\pipe\a\b")]
    [InlineData("name:stream")]
    [InlineData("a..b")]
    [InlineData(".")]
    public void An_explicit_windows_pipe_name_that_could_leave_the_local_pipe_namespace_finds_no_host(string pipe) =>
        Assert.Null(McpEndpoint.Resolve(Env(("AGENT_BRIDGE_PIPE", pipe)), windows: true));

    [Theory]
    [InlineData("host.sock")]
    [InlineData("/run/../etc/host.sock")]
    [InlineData("/run/user/1/")]
    public void An_explicit_unix_socket_must_be_an_absolute_path_without_dot_dot(string path) =>
        Assert.Null(McpEndpoint.Resolve(Env(("AGENT_BRIDGE_PIPE", path)), windows: false));

    [Fact]
    public void The_hosts_own_pipe_name_and_ordinary_names_are_still_accepted()
    {
        Assert.Equal("AgentBridge.Host.0123456789abcdef", McpEndpoint.Resolve(Env(("AGENT_BRIDGE_PIPE", "AgentBridge.Host.0123456789abcdef")), windows: true)!.Address);
        Assert.Equal("AgentBridge.Host.0123456789abcdef", McpEndpoint.Resolve(Env(("AGENT_BRIDGE_PIPE", @"\\.\pipe\AgentBridge.Host.0123456789abcdef")), windows: true)!.Address);
        Assert.Equal("My_pipe-1.v2", McpEndpoint.Resolve(Env(("AGENT_BRIDGE_PIPE", "My_pipe-1.v2")), windows: true)!.Address);
        Assert.Equal("/run/user/1/host.sock", McpEndpoint.Resolve(Env(("AGENT_BRIDGE_PIPE", "/run/user/1/host.sock")), windows: false)!.Address);
    }

    // The token goes in agent.hello and nowhere else: not in the hello, and not in anything the host is sent before agent.hello.
    [Fact]
    public async Task The_hello_carries_no_token_and_the_token_appears_only_in_agent_hello()
    {
        var sent = new List<(string Op, string Data)>();
        await using var server = Serve((op, data) => { sent.Add((op, data.ValueKind == JsonValueKind.Undefined ? "" : data.GetRawText())); return Normal(op, data); });
        await using var host = await McpHost.OpenAsync(server.Endpoint, "secret-token", TimeSpan.FromSeconds(5));
        await host.CallAsync("sessions.list");
        Assert.DoesNotContain(sent.Where(s => s.Op != "agent.hello"), s => s.Data.Contains("secret-token") || s.Data.Contains("token"));
        Assert.Single(sent, s => s.Data.Contains("secret-token"));
    }

    // A call that timed out may have been delivered: it is reported unavailable and never sent again.
    [Fact]
    public async Task A_call_that_times_out_reaches_the_host_once_and_is_not_retried()
    {
        var slow = 0;
        var hold = new TaskCompletionSource();
        await using var server = LocalServer.Start(LocalEndpoint.For(root), async (ch, op, data) =>
        {
            if (op == "slow") { Interlocked.Increment(ref slow); await hold.Task; }
            return Normal(op, data);
        });
        await using var host = await McpHost.OpenAsync(server.Endpoint, null, TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<McpToolException>(() => host.CallAsync("slow", timeout: TimeSpan.FromMilliseconds(150)));
        await Task.Delay(700);
        Assert.Equal(1, slow);
        hold.SetResult();
    }

    // CurrentUserOnly makes the connection fail unless the pipe's owner is this user, so a pipe another user created under the name is never talked to.
    // It cannot be exercised without a second user, so the client's construction is the pinned thing.
    [Fact]
    public void The_pipe_client_opens_with_current_user_only()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "AgentBridge.slnx"))) { root = root.Parent ?? throw new IOException("repository root not found"); }
        var client = File.ReadAllText(Path.Combine(root.FullName, "src", "AgentBridge.Local", "LocalClient.cs"));
        Assert.Matches(@"new NamedPipeClientStream\([^;]*PipeOptions\.CurrentUserOnly", client);
    }

    // On Unix a socket another user can write to is not talked to: neither the socket file nor its folder may be writable by group or others.
    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, true)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupExecute, true)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupWrite, false)]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite, false)]
    public void A_unix_socket_or_folder_writable_by_others_is_not_private(UnixFileMode mode, bool expected) =>
        Assert.Equal(expected, McpHost.IsPrivateMode(mode));

    [Fact]
    public async Task On_unix_an_explicit_socket_others_can_write_is_refused_and_a_private_one_is_served()
    {
        if (OperatingSystem.IsWindows()) { return; }
        await using var server = Serve(Normal);
        var explicitSocket = new LocalEndpoint(string.Empty, server.Endpoint.Address);
        File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.SetUnixFileMode(explicitSocket.Address, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await using (var host = await McpHost.OpenAsync(explicitSocket, null, TimeSpan.FromSeconds(5))) { Assert.True(host.Has("sessions.read")); }
        File.SetUnixFileMode(explicitSocket.Address, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherWrite);
        var e = await Assert.ThrowsAsync<McpToolException>(() => McpHost.OpenAsync(explicitSocket, null, TimeSpan.FromSeconds(5)));
        Assert.Equal(McpFailure.Refused, e.Kind);
    }


    // A hostile host: a frame header claiming 100 MB is refused at once, before any buffer is made, rather than waited on or allocated.
    [Fact]
    public async Task A_frame_longer_than_the_limit_ends_the_open_at_once_as_a_host_failure()
    {
        await using var raw = RawHost.Start(root, async stream =>
        {
            var head = new byte[4];
            await stream.ReadExactlyAsync(head);
            await stream.ReadExactlyAsync(new byte[BitConverter.ToInt32(head)]);
            await stream.WriteAsync(new byte[] { 0x00, 0x00, 0x40, 0x06 });
            await stream.FlushAsync();
            await Task.Delay(Timeout.Infinite);
        });
        var started = Environment.TickCount64;
        var e = await Assert.ThrowsAnyAsync<McpToolException>(() => McpHost.OpenAsync(raw.Endpoint, null, TimeSpan.FromSeconds(10)));
        Assert.True(Environment.TickCount64 - started < 4000, "the oversize header was waited on instead of refused");
        Assert.NotEqual(McpFailure.Refused, e.Kind);
    }

    // --- #4450: the host's own words reach an agent as an exception message, so they are bounded and scrubbed of anything that could be a token ---

    [Fact]
    public async Task A_long_host_refusal_is_bounded()
    {
        await using var server = Serve((op, data) => op == "host.stop" ? throw new InvalidOperationException("refused: " + new string('x', 5000)) : Normal(op, data));
        await using var host = await McpHost.OpenAsync(server.Endpoint, "t", TimeSpan.FromSeconds(5));
        var e = await Assert.ThrowsAsync<McpToolException>(() => host.CallAsync("host.stop"));
        Assert.True(e.Message.Length <= McpHost.MaxHostText + 1, $"{e.Message.Length} characters");
        Assert.StartsWith("refused: xxx", e.Message);
        Assert.EndsWith("…", e.Message);
    }

    [Theory]
    [InlineData("refused: bad credential abat1_AbCdEf0123456789xyz here", "abat1_AbCdEf0123456789xyz")]
    [InlineData("refused: bad credential 0123456789abcdef0123456789abcdef01234567 here", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData("refused: bad credential QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo-_0123456789== here", "QUJDREVGR0hJSktMTU5PUFFSU1RVVldYWVo")]
    public async Task A_token_shaped_string_in_a_host_refusal_is_redacted(string said, string secret)
    {
        await using var server = Serve((op, data) => op == "host.stop" ? throw new InvalidOperationException(said) : Normal(op, data));
        await using var host = await McpHost.OpenAsync(server.Endpoint, "t", TimeSpan.FromSeconds(5));
        var e = await Assert.ThrowsAsync<McpToolException>(() => host.CallAsync("host.stop"));
        Assert.DoesNotContain(secret, e.Message);
        Assert.Contains("[redacted]", e.Message);
        Assert.Contains("bad credential", e.Message);
    }

    [Fact]
    public async Task The_exact_token_this_server_sent_is_redacted_from_the_open_refusal_even_when_it_is_short()
    {
        await using var server = Serve((op, data) => op == "agent.hello" ? throw new InvalidOperationException("refused: no agent holds hunter2 here") : Normal(op, data));
        var e = await Assert.ThrowsAsync<McpToolException>(() => McpHost.OpenAsync(server.Endpoint, "hunter2", TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain("hunter2", e.Message);
        Assert.Contains("no agent holds", e.Message);
    }

    [Fact]
    public void Scrubbing_flattens_control_characters_and_keeps_ordinary_words()
    {
        var text = McpHost.Scrub("refused: not an agent\r\nof this host\0\t(pid 1234, session 5f)", null);
        Assert.Equal("refused: not an agent of this host (pid 1234, session 5f)", text);
        Assert.Equal("", McpHost.Scrub(null, null));
        Assert.Equal("a [redacted] b", McpHost.Scrub("a s3cr3t b", "s3cr3t"));
    }

    // --- #4471 (follow-ups of the #4463 read) ---

    [Fact]
    public void The_cut_never_leaves_half_of_a_surrogate_pair_and_a_token_at_the_cut_is_redacted_whole()
    {
        var emoji = McpHost.Scrub(new string('a', McpHost.MaxHostText - 1) + "😀 and more", null);
        Assert.True(emoji.Length <= McpHost.MaxHostText + 1);
        Assert.DoesNotContain(emoji, c => char.IsSurrogate(c));
        Assert.EndsWith("…", emoji);
        var token = McpHost.Scrub(new string('a', McpHost.MaxHostText - 8) + " abat1_SECRETSECRETSECRET tail", null);
        Assert.DoesNotContain("abat1_", token);
        Assert.DoesNotContain("SECRET", token);
    }

    [Theory]
    [InlineData("a\u2028b\u2029c", "a b c")]
    [InlineData("a\u202Eb", "a b")]
    [InlineData("a\u200Bb\u200Dc\uFEFFd", "a b c d")]
    [InlineData("red \u001B[31mtext\u001B[0m ok", "red text ok")]
    public void Line_separators_bidi_and_zero_width_characters_are_flattened_and_an_escape_sequence_is_removed_whole(string said, string expected) =>
        Assert.Equal(expected, McpHost.Scrub(said, null));

    // After the open the token is not forgotten: a short, unprefixed value this server sent is redacted from a later refusal too.
    [Fact]
    public async Task A_short_token_this_server_sent_is_also_redacted_from_a_later_refusal()
    {
        await using var server = Serve((op, data) => op == "host.stop" ? throw new InvalidOperationException("refused: nobody holds hunter2 here") : Normal(op, data));
        await using var host = await McpHost.OpenAsync(server.Endpoint, "hunter2", TimeSpan.FromSeconds(5));
        var e = await Assert.ThrowsAsync<McpToolException>(() => host.CallAsync("host.stop"));
        Assert.DoesNotContain("hunter2", e.Message);
        Assert.Contains("nobody holds", e.Message);
    }

    [Fact]
    public async Task A_hello_the_host_refuses_with_a_token_in_its_words_is_scrubbed_at_the_open()
    {
        await using var server = Serve((op, data) => op == "hello" ? throw new InvalidOperationException("refused: unknown credential abat1_AbCdEf0123456789xyz for hunter2") : Normal(op, data));
        var e = await Assert.ThrowsAnyAsync<McpToolException>(() => McpHost.OpenAsync(server.Endpoint, "hunter2", TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain("abat1_AbCdEf", e.Message);
        Assert.DoesNotContain("hunter2", e.Message);
    }

    // An explicit name or socket path that the kernel or Windows would read differently from what was typed finds no host.
    [Theory]
    [InlineData("x.")]
    [InlineData("a.")]
    public void A_windows_pipe_name_with_a_trailing_dot_finds_no_host(string pipe) =>
        Assert.Null(McpEndpoint.Resolve(Env(("AGENT_BRIDGE_PIPE", pipe)), windows: true));

    [Theory]
    [InlineData("/run/user/1/host\0.sock")]
    [InlineData("/run/user/1/host\n.sock")]
    public void A_unix_socket_path_with_a_control_character_finds_no_host(string path) =>
        Assert.Null(McpEndpoint.Resolve(Env(("AGENT_BRIDGE_PIPE", path)), windows: false));

    sealed class RawHost : IAsyncDisposable
    {
        readonly IAsyncDisposable listener;
        readonly Task served;
        public LocalEndpoint Endpoint { get; }

        RawHost(LocalEndpoint endpoint, IAsyncDisposable listener, Task served) { Endpoint = endpoint; this.listener = listener; this.served = served; }

        public static RawHost Start(string folder, Func<Stream, Task> script)
        {
            if (OperatingSystem.IsWindows())
            {
                var name = "mcp-raw-" + Guid.NewGuid().ToString("N");
                var pipe = new System.IO.Pipes.NamedPipeServerStream(name, System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte,
                    System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);
                return new(new LocalEndpoint(folder, name), new Disposer(() => pipe.DisposeAsync()),
                    Task.Run(async () => { await pipe.WaitForConnectionAsync(); await script(pipe); }));
            }
            var path = Path.Combine(folder, "raw.sock");
            var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
            socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(path));
            socket.Listen(1);
            return new(new LocalEndpoint(folder, path), new Disposer(() => { socket.Dispose(); return ValueTask.CompletedTask; }),
                Task.Run(async () => { using var peer = await socket.AcceptAsync(); await script(new System.Net.Sockets.NetworkStream(peer)); }));
        }

        public async ValueTask DisposeAsync() { await listener.DisposeAsync(); try { await served.WaitAsync(TimeSpan.FromMilliseconds(200)); } catch (Exception) { } }

        sealed class Disposer(Func<ValueTask> dispose) : IAsyncDisposable { public ValueTask DisposeAsync() => dispose(); }
    }
}
