using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Watch;

// The host's watch loop (docs/design/session-watch.md, "Placement" and "When the watch itself fails"). A pass reads the live sessions, asks
// SessionWatch what to notice, and sends ONE digest. A pass that throws is survived once per hour; a second failure inside the hour sends
// ONE "stopped" notice and ends the loop until the host restarts. LastPass is the heartbeat: it moves on every pass, quiet or held.
public sealed class SessionWatchLoop(Func<IReadOnlyList<Session>> snapshot, Func<bool> held, SessionWatch watch,
    Action<string, string> notify, Func<DateTimeOffset> now, Action<string> log)
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(60);
    static readonly TimeSpan Window = TimeSpan.FromHours(1);
    readonly List<DateTimeOffset> failures = [];

    public DateTimeOffset? LastPass { get; private set; }

    public void Pass()
    {
        var notices = watch.Evaluate(snapshot(), now(), held());
        if (WatchDigest.Compose(notices, Label) is { } digest) { notify(digest.Title, digest.Body); }
        LastPass = now();
    }

    // A short session id only: never a title, which can come from what someone typed.
    static string Label(string id) => id.Length <= 8 ? id : id[..8];

    public async Task RunAsync(Func<CancellationToken, Task> wait, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Pass(); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                var at = now();
                failures.RemoveAll(f => at - f > Window);
                failures.Add(at);
                log($"session watch: a pass failed ({e.GetType().Name})");
                if (failures.Count >= 2)
                {
                    try { notify("Session watch stopped", "The host stopped watching for stuck sessions. Restart the host to start it again."); }
                    catch (Exception n) when (n is not OperationCanceledException) { log($"session watch: the stopped notice failed ({n.GetType().Name})"); }
                    return;
                }
            }
            try { await wait(ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
