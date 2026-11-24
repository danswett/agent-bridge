using System.Text;
using AgentBridge.Hooks;
using Xunit;

namespace AgentBridge.Hooks.Tests;

// `agent-bridge-host-hook claude permission` (#1003 PR C): turns the host's verdict into Claude's answer. It stays OFF: ClaudeApproval.AuthenticatedChannel is
// still false, the hook is registered nowhere, and an environment has to be told the approval is enabled for anything to be asked.
public sealed class ClaudePermissionHookTests
{
    const string Event = """{"session_id":"s-1","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"echo hello"}}""";

    static string Run(string stdin, HookEnvironment env)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(stdin));
        using var output = new MemoryStream();
        Assert.Equal(0, HookRunner.Run(["claude", "permission"], input, output, env));
        return Encoding.UTF8.GetString(output.ToArray());
    }

    static HookEnvironment Env(bool enabled, Func<string, TimeSpan, Task<string?>>? approval, List<string>? asked = null) => new()
    {
        ExecutableDirectory = null, Now = () => new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero), ProcessId = 4242, Ancestors = () => [20],
        ElapsedMs = () => 7, ApprovalEnabled = enabled,
        Approval = approval is null ? null : (text, wait) => { asked?.Add(text); return approval(text, wait); },
    };

    [Theory]
    [InlineData("allow", "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"allow\"}}}\n")]
    [InlineData("deny", "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"deny\",\"message\":\"Denied from a Slipgates device.\"}}}\n")]
    [InlineData(null, "")]
    [InlineData("none", "")]
    [InlineData("ALLOW", "")]
    [InlineData("always", "")]
    public void The_hosts_verdict_becomes_claudes_answer_and_anything_else_prints_nothing(string? verdict, string expected)
    {
        var asked = new List<string>();
        Assert.Equal(expected, Run(Event, Env(true, (_, _) => Task.FromResult(verdict), asked)));
        Assert.Equal([Event], asked);
    }

    [Fact]
    public void The_wait_it_asks_for_is_below_the_hook_timeout_so_the_hook_ends_itself()
    {
        TimeSpan? asked = null;
        Run(Event, Env(true, (_, wait) => { asked = wait; return Task.FromResult<string?>(null); }));
        Assert.Equal(TimeSpan.FromSeconds(105), asked);
        Assert.True(asked < ClaudeApproval.HookTimeout);
    }

    [Fact]
    public void While_off_it_asks_nothing_and_prints_nothing_even_when_the_host_would_allow()
    {
        var asked = new List<string>();
        Assert.Equal("", Run(Event, Env(false, (_, _) => Task.FromResult<string?>("allow"), asked)));
        Assert.Empty(asked);
        // The default environment is off: the contract's one switch is not on.
        Assert.False(ClaudeApproval.AuthenticatedChannel);
        Assert.False(new HookEnvironment { ExecutableDirectory = null }.ApprovalEnabled);
    }

    [Fact]
    public void A_failing_ask_a_missing_ask_an_unreadable_event_and_an_oversize_event_all_print_nothing()
    {
        Assert.Equal("", Run(Event, Env(true, (_, _) => throw new IOException("host gone"))));
        Assert.Equal("", Run(Event, Env(true, (_, _) => Task.FromException<string?>(new InvalidOperationException()))));
        Assert.Equal("", Run(Event, Env(true, null)));
        var asked = new List<string>();
        Assert.Equal("", Run("not json", Env(true, (_, _) => Task.FromResult<string?>("allow"), asked)));
        Assert.Equal("", Run(Event.Replace("echo hello", new string('x', ApprovalClient.MaxEventBytes)), Env(true, (_, _) => Task.FromResult<string?>("allow"), asked)));
        Assert.Empty(asked);
    }
}
