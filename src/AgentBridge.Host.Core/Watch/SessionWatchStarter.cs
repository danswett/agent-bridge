using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Watch;

public sealed record SessionWatchHandle(SessionWatchLoop Loop, Task Task);

// Starts the host's watch loop, or does nothing at all (#4262, docs/design/session-watch.md "cutover"). Off, it returns null before it reads a
// session, the clock or the owner record, sends nothing and starts no timer: the host behaves exactly as without the watch.
// R3 (missing) is OFF here: the design says the clean-exit source is named before R3 is built, and none is named yet. With no end source the
// watch is given none (null), so it can never raise a missing-session notice. A named source is passed here once the design names it.
public static class SessionWatchStarter
{
    public static SessionWatchHandle? Start(Func<bool> enabled, Func<IReadOnlyList<Session>> snapshot, Func<bool> held, Func<string, string?> ownerOf,
        Action<string, string> notify, Func<DateTimeOffset> now, Action<string> log, Func<CancellationToken, Task> wait, CancellationToken ct)
    {
        if (!enabled()) { return null; }
        var loop = new SessionWatchLoop(snapshot, held, new SessionWatch(new WatchSettings(), ownerOf, ended: null), notify, now, log);
        return new SessionWatchHandle(loop, Task.Run(async () =>
        {
            // Nothing may fault this task unseen: a loop that fails says so and ends, and a logger that throws is not allowed to hide it.
            try { await loop.RunAsync(wait, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            { try { log($"session watch: stopped ({e.GetType().Name})"); } catch (Exception) { } }
        }, CancellationToken.None));
    }
}
