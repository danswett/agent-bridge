using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

public sealed record TurnTiming(int Ms, long At);

// How long the person waits: from sending a reply to the session settling (#1600). The turn is
// stamped when the reply is sent, goes on to Working, and settles at the first status after that
// which is not Working. Only duration and settlement time are kept (the app name is added on upload);
// no text, session or machine is in a record. It lives in memory with the reply box that sent the
// reply, so a turn in flight when the app closes is lost and nothing else: no state is on a machine.
public sealed class TurnClock
{
    public const string App = "Windows";
    public const int Keep = 100;
    public static readonly TimeSpan Expire = TimeSpan.FromHours(1);

    readonly TimeProvider time;
    readonly List<TurnTiming> done = [];
    long? start;
    bool working;

    public TurnClock(TimeProvider? time = null) => this.time = time ?? TimeProvider.System;

    // A second send before the turn settles keeps the first's start, unless that is stale.
    public void Sent()
    {
        var now = time.GetTimestamp();
        if (start is null || time.GetElapsedTime(start.Value, now) > Expire)
        {
            start = now;
            working = false;
        }
    }

    public void Seen(SessionStatus status)
    {
        if (start is not { } from) { return; }
        var elapsed = time.GetElapsedTime(from);
        if (elapsed > Expire) { Dropped(); return; }
        if (status == SessionStatus.Working) { working = true; return; }
        if (!working) { return; }
        Dropped();
        done.Add(new((int)Math.Max(0, Math.Round(elapsed.TotalMilliseconds)), time.GetUtcNow().ToUnixTimeSeconds()));
        if (done.Count > Keep) { done.RemoveAt(0); }
    }

    // The session is gone: its turn in flight is not reported.
    public void Dropped()
    {
        start = null;
        working = false;
    }

    // Milliseconds of each settled turn, oldest first.
    public IReadOnlyList<int> Recent => done.Select(t => t.Ms).ToArray();

    // Takes the settled turns out, for the call that reports them.
    public IReadOnlyList<TurnTiming> Drain()
    {
        var all = done.ToArray();
        done.Clear();
        return all;
    }
}
