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
            (_, _) => touched.Add("notify"), () => { touched.Add("clock"); return T; }, _ => touched.Add("log"),
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
        var handle = SessionWatchStarter.Start(() => true, () => [Stuck("aaaaaaaa-1")], () => false, _ => null,
            (t, b) => sent.Add((t, b)), () => T, _ => { }, _ => { cts.Cancel(); return Task.CompletedTask; }, cts.Token);

        Assert.NotNull(handle);
        await handle.Task;
        Assert.Equal("1 session needs you", Assert.Single(sent).Title);
        Assert.Equal(T, handle.Loop.LastPass);
    }
}
