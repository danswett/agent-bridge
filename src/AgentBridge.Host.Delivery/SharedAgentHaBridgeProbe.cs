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
    // How old a look may be and still be reported as G5's state. While a device watches hello, the
    // hello watch looks every 10 s (SessionService.HelloCheck); older than this, nothing has looked
    // lately and the state is unknown, never taken as off.
    public static readonly TimeSpan Reported = TimeSpan.FromSeconds(30);

    readonly IAgentHaBridgeProbe probe;
    readonly TimeProvider time;
    // Held while Recent looks, so a burst of hellos past Keep makes one look, not one each.
    readonly object looking = new();
    readonly AsyncLocal<Held?> held = new();
    // The newest look, replaced whole, so it is read without a lock and never torn.
    Look? latest;

    sealed record Look(string? Reason, long At);

    public SharedAgentHaBridgeProbe(IAgentHaBridgeProbe probe, TimeProvider? time = null)
    {
        this.probe = probe;
        this.time = time ?? TimeProvider.System;
    }

    public string? Active()
    {
        // Dated from before the look, so it is never taken for newer than it is.
        var at = time.GetTimestamp();
        var look = new Look(probe.Active(), at);
        var seen = Volatile.Read(ref latest);
        while (seen is null || look.At >= seen.At)
        {
            var was = Interlocked.CompareExchange(ref latest, look, seen);
            if (ReferenceEquals(was, seen)) { break; }
            seen = was;
        }
        return look.Reason;
    }

    public string? Recent()
    {
        if (held.Value is { } h) { return h.Reason; }
        lock (looking)
        {
            if (Volatile.Read(ref latest) is { } last && time.GetElapsedTime(last.At) < Keep) { return last.Reason; }
            return Active();
        }
    }

    // What the last look said, never looking and taking no lock: false when there has been none
    // within maxAge. For what only reports G5 and must not wait on Windows (diagnostics.get, #802).
    public bool TryLast(TimeSpan maxAge, out string? reason)
    {
        var last = Volatile.Read(ref latest);
        reason = last?.Reason;
        return last is not null && time.GetElapsedTime(last.At) < maxAge;
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
