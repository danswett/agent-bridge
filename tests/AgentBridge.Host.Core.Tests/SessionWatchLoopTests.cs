using AgentBridge.Host.Core.Watch;
using AgentBridge.Protocol;
using Xunit;

// Session watch S4a (docs/design/session-watch.md, "When the watch itself fails"): one pass sends ONE digest; a loop that throws is
// survived once per hour, and the second failure in an hour raises ONE "stopped" notice and ends. The last-pass time is the heartbeat.
public class SessionWatchLoopTests
{
    static readonly DateTimeOffset T = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    static Session Stuck(string id, string? title = null) => new()
    {
        Id = id, Agent = AgentKind.Claude, Status = SessionStatus.Waiting, StatusSince = T.AddMinutes(-6), UpdatedAt = T,
        Activity = new Activity { TurnCount = 1 }, Title = title,
    };

    sealed class Rig
    {
        public readonly List<(string Title, string Body)> Sent = [];
        public readonly List<string> Logged = [];
        public DateTimeOffset Now = T;
        public Func<IReadOnlyList<Session>> Snapshot = () => [];
        public bool Held;
        SessionWatchLoop? loop;
        public SessionWatchLoop Loop => loop ??= new(() => Snapshot(), () => Held, new SessionWatch(new WatchSettings(), _ => null),
            (t, b) => Sent.Add((t, b)), () => Now, Logged.Add);
    }

    [Fact]
    public void A_pass_over_three_stuck_sessions_sends_one_digest()
    {
        var rig = new Rig { Snapshot = () => [Stuck("aaaaaaaa-1"), Stuck("bbbbbbbb-2"), Stuck("cccccccc-3")] };
        rig.Loop.Pass();
        var (title, body) = Assert.Single(rig.Sent);
        Assert.Equal("3 sessions need you", title);
        Assert.StartsWith("3 waiting at a prompt: ", body);
    }

    [Fact]
    public void A_quiet_pass_sends_nothing_and_still_records_the_heartbeat()
    {
        var rig = new Rig();
        var loop = rig.Loop;
        Assert.Null(loop.LastPass);
        loop.Pass();
        Assert.Empty(rig.Sent);
        Assert.Equal(T, loop.LastPass);
    }

    [Fact]
    public void A_held_discovery_judges_nothing_but_the_pass_still_counts_as_alive()
    {
        var rig = new Rig { Snapshot = () => [Stuck("aaaaaaaa-1")], Held = true };
        var loop = rig.Loop;
        loop.Pass();
        Assert.Empty(rig.Sent);
        Assert.Equal(T, loop.LastPass);
    }

    [Fact]
    public void The_digest_names_a_session_by_a_short_id_never_by_its_title()
    {
        var rig = new Rig { Snapshot = () => [Stuck("aaaaaaaa-1111", title: "my secret prompt text")] };
        rig.Loop.Pass();
        var body = Assert.Single(rig.Sent).Body;
        Assert.DoesNotContain("secret", body);
        Assert.EndsWith("aaaaaaaa", body);
    }

    static async Task Run(SessionWatchLoop loop, int passes)
    {
        using var cts = new CancellationTokenSource();
        var left = passes;
        await loop.RunAsync(_ => { if (--left <= 0) { cts.Cancel(); } return Task.CompletedTask; }, cts.Token);
    }

    [Fact]
    public async Task One_failure_is_survived_and_the_next_pass_runs()
    {
        var rig = new Rig();
        var calls = 0;
        rig.Snapshot = () => ++calls == 1 ? throw new InvalidOperationException("boom") : [Stuck("aaaaaaaa-1")];
        await Run(rig.Loop, 3);
        Assert.Equal("1 session needs you", Assert.Single(rig.Sent).Title);
        Assert.Contains(rig.Logged, l => l.Contains("InvalidOperationException"));
    }

    [Fact]
    public async Task A_second_failure_within_an_hour_sends_one_stopped_notice_and_ends_the_loop()
    {
        var rig = new Rig { Snapshot = () => throw new InvalidOperationException("boom") };
        await Run(rig.Loop, 10);
        var (title, _) = Assert.Single(rig.Sent);
        Assert.Equal("Session watch stopped", title);
    }

    [Fact]
    public async Task Two_failures_more_than_an_hour_apart_are_both_survived()
    {
        var rig = new Rig();
        var calls = 0;
        rig.Snapshot = () => { rig.Now = T.AddHours(calls * 2); return ++calls <= 2 ? throw new InvalidOperationException("boom") : []; };
        await Run(rig.Loop, 4);
        Assert.Empty(rig.Sent);
        Assert.Equal(4, calls);
        Assert.NotNull(rig.Loop.LastPass);
    }

    [Fact]
    public async Task Two_failures_exactly_one_hour_apart_count_as_inside_the_hour()
    {
        var rig = new Rig();
        var calls = 0;
        rig.Snapshot = () => { rig.Now = T.AddHours(calls++); throw new InvalidOperationException("boom"); };
        await Run(rig.Loop, 10);
        Assert.Equal("Session watch stopped", Assert.Single(rig.Sent).Title);
    }

    [Fact]
    public async Task A_failing_pass_does_not_move_the_heartbeat()
    {
        var rig = new Rig();
        var loop = rig.Loop;
        loop.Pass();
        rig.Snapshot = () => throw new InvalidOperationException("boom");
        rig.Now = T.AddMinutes(5);
        await Run(loop, 1);
        Assert.Equal(T, loop.LastPass);
    }

    [Fact]
    public async Task A_wait_that_throws_ends_the_loop_with_a_log_line_and_no_exception()
    {
        var rig = new Rig();
        await rig.Loop.RunAsync(_ => throw new IOException("timer"), CancellationToken.None);
        Assert.Contains(rig.Logged, l => l.Contains("IOException"));
        Assert.Empty(rig.Sent);
    }
}
