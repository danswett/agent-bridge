using AgentBridge.Host;
using AgentBridge.Host.Core.Watch;
using AgentBridge.Protocol;
using Xunit;

// Session watch S4d (#4262): how the running watch becomes the heartbeat in host.status. Absent when it is off; the loop's own last pass otherwise.
public class SessionWatchHealthTests
{
    static readonly DateTimeOffset T = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    static SessionWatchLoop Loop(Func<DateTimeOffset> now) =>
        new(() => [], () => false, new SessionWatch(new WatchSettings(), _ => null), (_, _) => { }, now, _ => { });

    [Fact]
    public void A_watch_that_is_off_has_no_heartbeat_member() => Assert.Null(SessionWatchHealth.Of(null));

    [Fact]
    public void A_running_watch_reports_its_last_pass_and_is_not_stopped()
    {
        var loop = Loop(() => T);
        loop.Pass();
        var status = SessionWatchHealth.Of(new SessionWatchHandle(loop, new TaskCompletionSource().Task))!;
        Assert.Equal((true, T, false), (status.On, status.LastPassAt, status.Stopped));
    }

    [Fact]
    public void Before_its_first_pass_the_last_pass_is_absent_and_a_finished_loop_reads_as_stopped()
    {
        var status = SessionWatchHealth.Of(new SessionWatchHandle(Loop(() => T), Task.CompletedTask))!;
        Assert.Equal((true, null, true), (status.On, status.LastPassAt, status.Stopped));
    }
}
