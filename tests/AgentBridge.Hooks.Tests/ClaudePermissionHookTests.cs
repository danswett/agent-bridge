using System.Text;
using AgentBridge.Hooks;
using Xunit;

namespace AgentBridge.Hooks.Tests;

// `claude permission` (#1003 PR C): the host's verdict becomes Claude's answer. It stays OFF (AuthenticatedChannel false, nothing registers the hook).
public sealed class ClaudePermissionHookTests
{
    const string Event = """{"session_id":"s-1","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"echo hello"}}""";
    const string Allow = "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"allow\"}}}\n";

    static string Run(string stdin, bool enabled, Func<string, TimeSpan, Task<string?>>? approval, List<string>? asked = null)
    {
        var env = new HookEnvironment
        {
            ExecutableDirectory = null, ProcessId = 4242, Ancestors = () => [20], ElapsedMs = () => 7, ApprovalEnabled = enabled,
            Approval = approval is null ? null : (text, wait) => { asked?.Add(text + "|" + wait.TotalSeconds); return approval(text, wait); },
        };
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(stdin));
        using var output = new MemoryStream();
        Assert.Equal(0, HookRunner.Run(["claude", "permission"], input, output, env));
        return Encoding.UTF8.GetString(output.ToArray());
    }

    [Theory]
    [InlineData("allow", Allow)]
    [InlineData("deny", "{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"deny\",\"message\":\"Denied from a Slipgates device.\"}}}\n")]
    [InlineData(null, "")]
    [InlineData("ALLOW", "")]
    public void The_hosts_verdict_becomes_claudes_answer_and_anything_else_prints_nothing_after_asking_for_less_than_the_hook_timeout(string? verdict, string expected)
    {
        var asked = new List<string>();
        Assert.Equal(expected, Run(Event, true, (_, _) => Task.FromResult(verdict), asked));
        Assert.Equal([Event + "|105"], asked);
        Assert.True(ClaudeApproval.AskWait < ClaudeApproval.HookTimeout);
    }

    [Fact]
    public void While_off_it_asks_nothing_and_prints_nothing_even_when_the_host_would_allow()
    {
        var asked = new List<string>();
        Assert.Equal("", Run(Event, false, (_, _) => Task.FromResult<string?>("allow"), asked));
        Assert.Empty(asked);
        Assert.False(ClaudeApproval.AuthenticatedChannel);
        Assert.False(new HookEnvironment { ExecutableDirectory = null }.ApprovalEnabled);
    }

    [Fact]
    public void A_failing_or_missing_ask_an_unreadable_event_and_an_oversize_event_all_print_nothing()
    {
        Assert.Equal("", Run(Event, true, (_, _) => throw new IOException("host gone")));
        Assert.Equal("", Run(Event, true, (_, _) => Task.FromException<string?>(new InvalidOperationException())));
        Assert.Equal("", Run(Event, true, null));
        var asked = new List<string>();
        Assert.Equal("", Run("not json", true, (_, _) => Task.FromResult<string?>("allow"), asked));
        Assert.Equal("", Run(Event.Replace("echo hello", new string('x', ApprovalClient.MaxEventBytes)), true, (_, _) => Task.FromResult<string?>("allow"), asked));
        Assert.Empty(asked);
    }
}
