using AgentBridge.Host;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// #1003, the authenticated channel's host side (docs/design/claude-approval-channel.md): a Claude permission prompt is held IN MEMORY by the host, and
// only a person's answer that arrived by the relay-authenticated sessions.answer path can resolve it; once, never from an agent, never after the wait,
// never when the command would be shown cut. Fake clock. Nothing wires it: no pipe op, no hook, no registration, the setting stays off.
public sealed class ClaudeApprovalBrokerTests
{
    const string Person = "person:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero));
    readonly ClaudeApprovalBroker broker;

    public ClaudeApprovalBrokerTests() => broker = new ClaudeApprovalBroker(time, TimeSpan.FromSeconds(100));

    [Theory]
    [InlineData("allow")]
    [InlineData("deny")]
    public async Task A_relay_authenticated_persons_answer_resolves_the_waiting_prompt_with_that_verdict(string verdict)
    {
        var wait = broker.Register("s-1", "r-1", "echo hello");
        Assert.Equal(ClaudeApprovalBroker.Result.Accepted, broker.Resolve(Person, "s-1", "r-1", verdict));
        Assert.Equal(verdict, await wait);
    }

    [Fact]
    public async Task An_answer_is_used_once_so_it_cannot_settle_the_next_identical_prompt()
    {
        var first = broker.Register("s-1", "r-1", "echo hello");
        Assert.Equal(ClaudeApprovalBroker.Result.Accepted, broker.Resolve(Person, "s-1", "r-1", "allow"));
        Assert.Equal("allow", await first);
        Assert.Equal(ClaudeApprovalBroker.Result.Unknown, broker.Resolve(Person, "s-1", "r-1", "allow"));
        // The same prompt asked again is a new registration: it waits, and the earlier answer does not reach it.
        var second = broker.Register("s-1", "r-1", "echo hello");
        Assert.False(second.IsCompleted);
    }

    [Theory]
    [InlineData("agent:abc")]
    [InlineData("person:local")]
    [InlineData("person:")]
    [InlineData("person:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("person:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa ")]
    [InlineData("")]
    public async Task Anything_but_a_relay_authenticated_person_is_refused_and_the_prompt_keeps_waiting(string principal)
    {
        var wait = broker.Register("s-1", "r-1", "echo hello");
        Assert.Equal(ClaudeApprovalBroker.Result.Principal, broker.Resolve(principal, "s-1", "r-1", "allow"));
        Assert.False(wait.IsCompleted);
        Assert.Equal(ClaudeApprovalBroker.Result.Accepted, broker.Resolve(Person, "s-1", "r-1", "deny"));
        Assert.Equal("deny", await wait);
    }

    [Fact]
    public async Task With_no_answer_by_the_deadline_there_is_no_verdict_and_a_late_answer_is_unknown()
    {
        var wait = broker.Register("s-1", "r-1", "echo hello");
        time.Advance(TimeSpan.FromSeconds(99));
        Assert.False(wait.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await wait);
        Assert.Equal(ClaudeApprovalBroker.Result.Unknown, broker.Resolve(Person, "s-1", "r-1", "allow"));
    }

    [Fact]
    public async Task A_command_that_would_be_shown_cut_is_never_answerable_even_by_a_person()
    {
        var wait = broker.Register("s-1", "r-1", new string('x', ClaudeApprovalBroker.PreviewMax + 1));
        Assert.Equal(ClaudeApprovalBroker.Result.Truncated, broker.Resolve(Person, "s-1", "r-1", "allow"));
        time.Advance(TimeSpan.FromSeconds(100));
        Assert.Null(await wait);
        var exact = broker.Register("s-1", "r-2", new string('x', ClaudeApprovalBroker.PreviewMax));
        Assert.Equal(ClaudeApprovalBroker.Result.Accepted, broker.Resolve(Person, "s-1", "r-2", "deny"));
        Assert.Equal("deny", await exact);
    }

    [Fact]
    public void An_unknown_prompt_or_session_or_a_verdict_that_is_neither_allow_nor_deny_changes_nothing()
    {
        var wait = broker.Register("s-1", "r-1", "echo hello");
        Assert.Equal(ClaudeApprovalBroker.Result.Unknown, broker.Resolve(Person, "s-1", "other", "allow"));
        Assert.Equal(ClaudeApprovalBroker.Result.Unknown, broker.Resolve(Person, "s-2", "r-1", "allow"));
        Assert.Equal(ClaudeApprovalBroker.Result.Verdict, broker.Resolve(Person, "s-1", "r-1", "always"));
        Assert.Equal(ClaudeApprovalBroker.Result.Verdict, broker.Resolve(Person, "s-1", "r-1", "ALLOW"));
        Assert.False(wait.IsCompleted);
    }

    [Fact]
    public async Task The_same_prompt_registered_again_ends_the_earlier_wait_with_no_verdict_and_only_the_new_one_can_be_answered()
    {
        var first = broker.Register("s-1", "r-1", "echo hello");
        var second = broker.Register("s-1", "r-1", "echo hello");
        Assert.Null(await first);
        Assert.False(second.IsCompleted);
        Assert.Equal(ClaudeApprovalBroker.Result.Accepted, broker.Resolve(Person, "s-1", "r-1", "allow"));
        Assert.Equal("allow", await second);
    }

    [Fact]
    public async Task A_session_that_ends_withdraws_its_waiting_prompts_with_no_verdict()
    {
        var one = broker.Register("s-1", "r-1", "echo hello");
        var other = broker.Register("s-2", "r-1", "echo hello");
        broker.Withdraw("s-1");
        Assert.Null(await one);
        Assert.False(other.IsCompleted);
        Assert.Equal(ClaudeApprovalBroker.Result.Unknown, broker.Resolve(Person, "s-1", "r-1", "allow"));
    }

    [Fact]
    public void Nothing_outside_the_broker_and_its_tests_refers_to_it_so_the_feature_is_off()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src", "AgentBridge.Host"))) { dir = Path.GetDirectoryName(dir); }
        Assert.NotNull(dir);
        var users = Directory.EnumerateFiles(Path.Combine(dir!, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("ClaudeApprovalBroker", StringComparison.Ordinal))
            .Select(Path.GetFileName).ToList();
        Assert.Equal(["ClaudeApprovalBroker.cs"], users);
    }
}
