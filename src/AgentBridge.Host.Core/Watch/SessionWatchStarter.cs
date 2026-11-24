using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Watch;

public sealed record SessionWatchHandle(SessionWatchLoop Loop, Task Task);

// Starts the host's watch loop, or does nothing at all (#4262, docs/design/session-watch.md "cutover"). Off, it returns null before it reads a
// session, the clock or the owner record, sends nothing and starts no timer: the host behaves exactly as without the watch.
// R3 (missing) is ON, with the end sources the design names (docs/design/session-watch.md, R3 end sources; owner, Nov 23 about 22:42 PT, "A: Alarm unless
// ended cleanly"): a deliberate end the watch saw (a session in Ending), or the agent's own SessionEnd hook (endedByHook, from the session source). Only an agent
// with such a signal (Codex today) can raise it; Claude and Copilot never do. It also needs an owner record, so it is quiet until the owner hookup lands.
public static class SessionWatchStarter
{
    public static SessionWatchHandle? Start(Func<bool> enabled, Func<IReadOnlyList<Session>> snapshot, Func<bool> held, Func<string, string?> ownerOf,
        Func<string, bool> endedByHook, Action<string, string> notify, Func<DateTimeOffset> now, Action<string> log, Func<CancellationToken, Task> wait, CancellationToken ct)
    {
        if (!enabled()) { return null; }
        var loop = new SessionWatchLoop(snapshot, held, new SessionWatch(new WatchSettings(), ownerOf, endedByHook, log), notify, now, log);
        return new SessionWatchHandle(loop, Task.Run(async () =>
        {
            // Nothing may fault this task unseen: a loop that fails says so and ends, and a logger that throws is not allowed to hide it.
            try { await loop.RunAsync(wait, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            { try { log($"session watch: stopped ({e.GetType().Name})"); } catch (Exception) { } }
        }, CancellationToken.None));
    }
}
