using AgentBridge.Hooks;
using Xunit;

// The Claude Code permission prompt answered from a device (#1003), hook side: what the hook prints for a decision, which decision files it
// accepts, and how long it waits. Sensitive: only a person's decision is taken, nothing is allowed by default, and with no usable decision the
// hook prints nothing, so Claude shows its own prompt at the terminal.
public sealed class ClaudeApprovalTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
    const string Event = """{"session_id":"s-1","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"rm -rf private-folder"}}""";

    static string Decision(string id, string verdict = "allow", string principal = "person", DateTimeOffset? at = null) =>
        $$"""{"request":"{{id}}","verdict":"{{verdict}}","principal":"{{principal}}","at":"{{(at ?? Now):yyyy-MM-dd'T'HH:mm:ss'Z'}}"}""";

    [Fact]
    public void A_request_id_is_stable_for_the_same_prompt_differs_for_another_and_holds_none_of_it()
    {
        var id = ClaudeApproval.RequestId(Event);
        Assert.Equal(id, ClaudeApproval.RequestId(Event));
        Assert.NotEqual(id, ClaudeApproval.RequestId(Event.Replace("private-folder", "other")));
        Assert.Matches("^[0-9a-f]{16}$", id!);
        Assert.Null(ClaudeApproval.RequestId("not json"));
        Assert.Null(ClaudeApproval.RequestId("""{"tool_name":"Bash"}"""));
    }

    [Fact]
    public void The_hook_prints_claudes_decision_for_a_verdict_and_nothing_for_none()
    {
        Assert.Equal("{\"hookSpecificOutput\":{\"hookEventName\":\"PermissionRequest\",\"decision\":{\"behavior\":\"allow\"}}}\n", ClaudeApproval.Reply("allow"));
        var deny = ClaudeApproval.Reply("deny");
        Assert.Contains("\"behavior\":\"deny\"", deny);
        Assert.DoesNotContain("private", deny);
        Assert.Equal("", ClaudeApproval.Reply(null));
        Assert.Equal("", ClaudeApproval.Reply("maybe"));
    }

    [Theory]
    [InlineData("allow", "allow")]
    [InlineData("deny", "deny")]
    public void A_persons_current_decision_for_this_request_is_taken(string verdict, string expected) =>
        Assert.Equal((expected, null), ClaudeApproval.Verdict(Decision("abc", verdict), "abc", Now.AddSeconds(5)));

    [Theory]
    [InlineData("agent", "principal")]
    [InlineData("", "principal")]
    public void An_agents_decision_is_refused_with_its_reason(string principal, string reason) =>
        Assert.Equal((null, reason), ClaudeApproval.Verdict(Decision("abc", principal: principal), "abc", Now));

    [Fact]
    public void A_decision_for_another_request_a_stale_or_future_one_an_unknown_verdict_or_a_malformed_file_is_refused()
    {
        Assert.Equal((null, "request"), ClaudeApproval.Verdict(Decision("zzz"), "abc", Now));
        Assert.Equal((null, "stale"), ClaudeApproval.Verdict(Decision("abc", at: Now.AddMinutes(-10)), "abc", Now));
        Assert.Equal((null, "future"), ClaudeApproval.Verdict(Decision("abc", at: Now.AddMinutes(5)), "abc", Now));
        Assert.Equal((null, "verdict"), ClaudeApproval.Verdict(Decision("abc", "always"), "abc", Now));
        Assert.Equal((null, "unreadable"), ClaudeApproval.Verdict("{not json", "abc", Now));
        Assert.Equal((null, "unreadable"), ClaudeApproval.Verdict("", "abc", Now));
    }

    [Fact]
    public async Task The_hook_waits_for_a_decision_and_takes_it_when_it_appears()
    {
        var clock = Now;
        var polls = 0;
        var verdict = await ClaudeApproval.AwaitAsync(() => ++polls < 3 ? null : Decision("abc"), "abc", () => clock,
            d => { clock += d; return Task.CompletedTask; }, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1), _ => { });
        Assert.Equal("allow", verdict);
        Assert.Equal(3, polls);
    }

    [Fact]
    public async Task With_no_decision_by_the_deadline_the_hook_gives_none_and_never_allows()
    {
        var clock = Now;
        var verdict = await ClaudeApproval.AwaitAsync(() => null, "abc", () => clock, d => { clock += d; return Task.CompletedTask; },
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1), _ => { });
        Assert.Null(verdict);
        Assert.InRange(clock - Now, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(61));
    }

    [Fact]
    public async Task An_agents_decision_is_logged_by_reason_only_and_ignored_so_the_wait_runs_out()
    {
        var clock = Now;
        var log = new List<string>();
        var verdict = await ClaudeApproval.AwaitAsync(() => Decision("abc", principal: "agent", at: clock), "abc", () => clock,
            d => { clock += d; return Task.CompletedTask; }, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), log.Add);
        Assert.Null(verdict);
        Assert.Contains(log, l => l == "claude approval decision refused: principal");
        Assert.DoesNotContain(log, l => l.Contains("private", StringComparison.Ordinal));
    }
}
