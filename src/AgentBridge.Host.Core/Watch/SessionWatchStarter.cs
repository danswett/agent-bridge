using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Watch;

public sealed record SessionWatchHandle(SessionWatchLoop Loop, Task Task);

// Starts the host's watch loop, or does nothing at all (#4262, docs/design/session-watch.md "cutover"). Off, it returns null before it reads a
// session, the clock or the owner record, sends nothing and starts no timer: the host behaves exactly as without the watch.
// R3 is on, explained by what the watch itself saw (a session seen Ending, or one that had delivered, going is not missing); there is no
// other end source yet.
public static class SessionWatchStarter
{
    public static SessionWatchHandle? Start(Func<bool> enabled, Func<IReadOnlyList<Session>> snapshot, Func<bool> held, Func<string, string?> ownerOf,
        Action<string, string> notify, Func<DateTimeOffset> now, Action<string> log, Func<CancellationToken, Task> wait, CancellationToken ct)
    {
        if (!enabled()) { return null; }
        var loop = new SessionWatchLoop(snapshot, held, new SessionWatch(new WatchSettings(), ownerOf, _ => false), notify, now, log);
        return new SessionWatchHandle(loop, Task.Run(() => loop.RunAsync(wait, ct), CancellationToken.None));
    }
}
