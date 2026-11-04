namespace AgentBridge.Host.Delivery;

// One host's G5 check (design G5: nothing is typed while agent-ha-bridge runs for this user),
// shared by everything that asks it, with its answer kept a moment for what is only offered or
// reported. A hello asked it about six times, uncached (answers, replies, stops, launch twice,
// folder trust). Each look lists the logon session's named objects and asks Task Scheduler over
// COM, about 3 ms and 288 KB, and hellos made it 46% of what the host allocated, measured (#607).
//
// Active looks every time it is asked: what types, presses Enter, stops or launches asks it,
// immediately before acting, so G5's guarantee is the same as before. Recent is for what a hello
// offers and says: at most Keep old, and one look however many ask at once.
public sealed class SharedAgentHaBridgeProbe : IAgentHaBridgeProbe
{
    public static readonly TimeSpan Keep = TimeSpan.FromSeconds(2);

    readonly IAgentHaBridgeProbe probe;
    readonly TimeProvider time;
    readonly object gate = new();
    // Held while Recent looks, so a burst of hellos past Keep makes one look, not one each.
    readonly object looking = new();
    readonly AsyncLocal<Held?> held = new();
    string? last;
    long lastAt;
    bool known;

    public SharedAgentHaBridgeProbe(IAgentHaBridgeProbe probe, TimeProvider? time = null)
    {
        this.probe = probe;
        this.time = time ?? TimeProvider.System;
    }

    public string? Active()
    {
        // Dated from before the look, so it is never taken for newer than it is.
        var at = time.GetTimestamp();
        var reason = probe.Active();
        lock (gate)
        {
            if (!known || at >= lastAt) { (last, lastAt, known) = (reason, at, true); }
        }
        return reason;
    }

    public string? Recent()
    {
        if (held.Value is { } h) { return h.Reason; }
        lock (looking)
        {
            lock (gate)
            {
                if (known && time.GetElapsedTime(lastAt) < Keep) { return last; }
            }
            return Active();
        }
    }

    // One answer for everything one hello says, until disposed: its readOnly and the capabilities
    // G5 takes away come from the same look, never one each side of Keep running out. Only on the
    // caller's own flow (AsyncLocal), and only for Recent.
    public IDisposable Hold()
    {
        var previous = held.Value;
        held.Value = new Held(Recent());
        return new Release(this, previous);
    }

    sealed record Held(string? Reason);

    sealed class Release(SharedAgentHaBridgeProbe owner, Held? previous) : IDisposable
    {
        public void Dispose() => owner.held.Value = previous;
    }
}
