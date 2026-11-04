using System.Globalization;

namespace AgentBridge.Transport;

// How long a relay client waits before it tries again (#578). Full jitter: a uniform wait
// between 0 and min(30 s, 1 s * 2^attempt). Without it every host and app retried at the same
// 1, 2, 4... s marks after a deploy closed them all with 1012 at once: a reconnect storm the
// relay brought on itself, which also tripped its per-IP connect limit for devices behind one
// NAT. The PWA keeps the same policy in pwa/js/backoff.js.
public sealed class ReconnectBackoff
{
    public static readonly TimeSpan Base = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan Cap = TimeSpan.FromSeconds(30);
    // After a 1012 the first retry is spread over this window, or over the relay's own hint.
    public static readonly TimeSpan RestartSpread = TimeSpan.FromSeconds(5);
    public const int MinSpreadSeconds = 1;
    public const int MaxSpreadSeconds = 60;

    readonly Func<double> random;
    int attempt;

    // random: uniform in [0, 1); injectable so a test can pin the schedule.
    public ReconnectBackoff(Func<double>? random = null) => this.random = random ?? Random.Shared.NextDouble;

    // Retries since the last successful connection.
    public int Attempt => attempt;

    // Only after a successful connection, never after a failed try.
    public void Reset() => attempt = 0;

    // The wait before the next try. closeStatus and closeReason are how the last connection
    // ended, or null when it never opened.
    public TimeSpan Next(int? closeStatus = null, string? closeReason = null)
    {
        var ceiling = Ceiling(attempt);
        if (closeStatus == CloseCodes.Restarting)
        {
            var spread = SpreadFrom(closeReason) ?? RestartSpread;
            if (spread > ceiling) { ceiling = spread; }
        }
        if (attempt < int.MaxValue) { attempt++; }
        var r = random();
        // A source outside [0, 1) must not stretch the wait past its ceiling or make it negative.
        if (!(r >= 0) || r >= 1) { r = 0; }
        return TimeSpan.FromTicks((long)(ceiling.Ticks * r));
    }

    // min(cap, base * 2^attempt), without overflowing however many attempts there have been.
    public static TimeSpan Ceiling(int attempt) =>
        attempt >= 32 ? Cap : TimeSpan.FromTicks(Math.Min(Cap.Ticks, Base.Ticks << Math.Max(0, attempt)));

    // A 1012's reason "restarting;spread=N" lets the relay widen the spread without a client
    // release. The relay is not trusted, so anything else is ignored and N is clamped to 1-60 s.
    public static TimeSpan? SpreadFrom(string? reason)
    {
        if (string.IsNullOrEmpty(reason) || reason.Length > 123) { return null; }
        var parts = reason.Split(';');
        if (parts.Length != 2 || parts[0] != "restarting" || !parts[1].StartsWith("spread=", StringComparison.Ordinal)) { return null; }
        var n = parts[1]["spread=".Length..];
        if (n.Length is 0 or > 9 || !int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) { return null; }
        return TimeSpan.FromSeconds(Math.Clamp(seconds, MinSpreadSeconds, MaxSpreadSeconds));
    }
}
