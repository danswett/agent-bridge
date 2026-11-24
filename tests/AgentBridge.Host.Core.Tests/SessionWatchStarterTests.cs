using AgentBridge.Host.Core.Watch;
using AgentBridge.Protocol;
using Xunit;

// Session watch S4c (docs/design/session-watch.md, cutover): the watch is OFF by default, and off it changes nothing at all.
public class SessionWatchStarterTests
{
    static readonly DateTimeOffset T = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    static Session Stuck(string id) => new()
    {
        Id = id, Agent = AgentKind.Claude, Status = SessionStatus.Waiting, StatusSince = T.AddMinutes(-6), UpdatedAt = T,
        Activity = new Activity { TurnCount = 1 },
    };

    [Fact]
    public async Task Off_it_reads_no_session_sends_nothing_and_starts_no_timer()
    {
        var touched = new List<string>();
        var handle = SessionWatchStarter.Start(() => false,
            () => { touched.Add("snapshot"); return [Stuck("a")]; }, () => { touched.Add("held"); return false; }, _ => { touched.Add("owner"); return null; },
            _ => { touched.Add("ended"); return false; }, new Dictionary<AgentKind, DateTimeOffset>(), (_, _) => touched.Add("notify"), () => { touched.Add("clock"); return T; }, _ => touched.Add("log"),
            _ => { touched.Add("wait"); return Task.CompletedTask; }, CancellationToken.None);

        Assert.Null(handle);
        await Task.Yield();
        Assert.Empty(touched);
    }

    [Fact]
    public async Task On_it_runs_a_pass_and_sends_one_digest_and_stops_when_cancelled()
    {
        var sent = new List<(string Title, string Body)>();
        using var cts = new CancellationTokenSource();
        var handle = SessionWatchStarter.Start(() => true, () => [Stuck("aaaaaaaa-1")], () => false, _ => null, _ => false, new Dictionary<AgentKind, DateTimeOffset>(),
            (t, b) => sent.Add((t, b)), () => T, _ => { }, _ => { cts.Cancel(); return Task.CompletedTask; }, cts.Token);

        Assert.NotNull(handle);
        await handle.Task;
        Assert.Equal("1 session needs you", Assert.Single(sent).Title);
        Assert.Equal(T, handle.Loop.LastPass);
    }

    // docs/design/session-watch.md, R3 end sources (owner, Nov 23 about 22:42 PT: "A: Alarm unless ended cleanly"): the started watch raises a missing-session
    // notice for an owned session that vanishes, but ONLY for an agent that can say it ended cleanly (Codex today). A Claude vanish never does.
    [Theory]
    [InlineData(AgentKind.Codex, true)]
    [InlineData(AgentKind.Claude, true)]
    [InlineData(AgentKind.Copilot, false)]
    public async Task R3_in_the_started_watch_raises_for_a_vanished_owned_session_only_when_its_agent_has_a_clean_end_signal(AgentKind agent, bool raises)
    {
        var sent = new List<(string Title, string Body)>();
        using var cts = new CancellationTokenSource();
        var passes = 0;
        var handle = SessionWatchStarter.Start(() => true,
            () => ++passes == 1 ? [Working("aaaaaaaa-1") with { Agent = agent, StartedAt = T.AddHours(-1) }] : [], () => false,
            _ => "agent:0123456789abcdef0123456789abcdef/agent_bridge_1111111111111111", _ => false, new Dictionary<AgentKind, DateTimeOffset> { [AgentKind.Claude] = T.AddHours(-5) },
            (t, b) => sent.Add((t, b)), () => T.AddHours(passes), _ => { }, _ => { if (passes >= 4) { cts.Cancel(); } return Task.CompletedTask; }, cts.Token);

        await handle!.Task;
        Assert.Equal(raises, sent.Any(s => s.Body.Contains("1 missing")));
    }

    [Fact]
    public async Task A_vanish_the_source_saw_end_cleanly_raises_nothing_even_for_codex()
    {
        var sent = new List<(string Title, string Body)>();
        using var cts = new CancellationTokenSource();
        var passes = 0;
        var handle = SessionWatchStarter.Start(() => true,
            () => ++passes == 1 ? [Working("aaaaaaaa-1") with { Agent = AgentKind.Codex }] : [], () => false,
            _ => "agent:0123456789abcdef0123456789abcdef/agent_bridge_1111111111111111", id => id == "aaaaaaaa-1", new Dictionary<AgentKind, DateTimeOffset>(),
            (t, b) => sent.Add((t, b)), () => T.AddHours(passes), _ => { }, _ => { if (passes >= 4) { cts.Cancel(); } return Task.CompletedTask; }, cts.Token);

        await handle!.Task;
        Assert.Empty(sent);
    }

    static Session Working(string id) => Stuck(id) with { Status = SessionStatus.Working, StatusSince = T.AddHours(-1) };
}
