using AgentBridge.Host.Core.Watch;
using AgentBridge.Local;

namespace AgentBridge.Host;

// The session watch's heartbeat as host.status shows it (#4262): absent when the watch is off, otherwise the loop's own last pass. Stopped is true
// whenever the loop is no longer running: it failed twice in an hour and stays down until the host restarts, or the host is stopping.
// host.status is read on demand: the heartbeat is not pushed each minute, the app sees the latest time when it reads.
public static class SessionWatchHealth
{
    public static SessionWatchStatus? Of(SessionWatchHandle? watch) =>
        watch is null ? null : new SessionWatchStatus(true, watch.Loop.LastPass, watch.Task.IsCompleted);
}
