namespace AgentBridge.Relay;

// The registry-get rate (docs/design/fleet-session-registry.md "Limits", #1396). One read builds up to
// 33 frames (32 machines of up to 16 KiB, then `known`), so the cost is bounded here, not per frame:
// a socket may burst, then read at a steady rate, and one account's sockets together stay under a
// ceiling. A refused read is answered `slow-down`; the socket stays open.
public static class RegistryReadLimit
{
    public const int SocketBurst = 20;
    public const double SocketRefillPerSecond = 10;
    public const int AccountBurst = 50;
    public const double AccountRefillPerSecond = 50;
}

// A token bucket on the relay's clock, so tests drive it with a fake one.
sealed class TokenBucket
{
    readonly object gate = new();
    readonly double burst, perSecond;
    readonly TimeProvider time;
    double tokens;
    long last;

    public TokenBucket(double burst, double perSecond, TimeProvider time)
    {
        (this.burst, this.perSecond, this.time) = (burst, perSecond, time);
        tokens = burst;
        last = time.GetTimestamp();
    }

    public bool TryTake()
    {
        lock (gate)
        {
            var now = time.GetTimestamp();
            tokens = Math.Min(burst, tokens + time.GetElapsedTime(last, now).TotalSeconds * perSecond);
            last = now;
            if (tokens < 1) { return false; }
            tokens--;
            return true;
        }
    }
}
