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
}
