namespace AgentBridge.HomeAssistant;

// Confirm-DaemonOnlineHeartbeat's judgement, beat by beat: whether the liveness sensor has stopped
// taking the beats it is sent, and so is to be rebuilt.
//
// mqtt.publish answers 200 for a topic Home Assistant no longer listens to, so a beat that was
// published proves nothing; and an entity in that state has no expiry timer running, so the
// dashboard goes on showing the machine online on nothing (agent-ha-bridge #177). The sensor is
// asked instead, through last_reported, which the beat's changing time makes move on every beat
// that lands (#202). Two things must both hold before anything is rebuilt, because either alone
// has been wrong: silent for the window Home Assistant would itself have expired it in (elapsed
// time alone rebuilds a healthy sensor on a host back from hours asleep, whose one read since
// was taken before its first beat arrived), and silent on that many separate beats (a count
// alone fires early on a host whose passes outrun its beat). A rebuild is destructive (the
// entity disappears in between and takes its machine's cards with it), so after Limit in a row
// it stops, says so once, and leaves the sensor alone: still wrong, but no longer flickering.
// Only two beats landing in a row count as recovery; the one a rebuild sends itself always
// lands, and counting it reset the count on every cycle, which made the rebuild repeat for ever.
public sealed class HaLiveness(TimeSpan window, int beats, int limit)
{
    public enum Verdict { None, Rebuild, GiveUp }

    // What one read of the sensor said. Unreadable starts and stops no clock; Absent is the same
    // stall as silence (nothing is there to take a beat); Present with no Reported is a Home
    // Assistant that does not say when it last took a message, which cannot be watched at all.
    public readonly record struct Read(bool Readable, bool Absent, string? Reported)
    {
        public static readonly Read Unreadable = new(false, false, null);
        public static readonly Read Gone = new(true, true, "");
        public static Read Present(string? reported) => new(true, false, reported);
    }

    string seenReported = "";
    DateTimeOffset seenAt = DateTimeOffset.MinValue;
    int unanswered;
    int reporting;
    int repairs;
    bool givenUp;

    // How long the sensor had been silent when the last Rebuild was decided.
    public TimeSpan Stalled { get; private set; }

    public Verdict Observe(DateTimeOffset now, Read read)
    {
        if (!read.Readable) { return Verdict.None; }
        if (!read.Absent && read.Reported is null) { return Verdict.None; }
        var reported = read.Reported!;
        if (seenAt == DateTimeOffset.MinValue || !string.Equals(reported, seenReported, StringComparison.Ordinal))
        {
            reporting++;
            if (reporting >= 2)
            {
                repairs = 0;
                givenUp = false;
            }
            seenReported = reported;
            seenAt = now;
            unanswered = 0;
            return Verdict.None;
        }
        reporting = 0;
        unanswered++;
        var stalled = now - seenAt;
        if (stalled < window || unanswered < beats) { return Verdict.None; }

        // A fresh window whether or not the rebuild works: one that does not take must not repeat
        // on every beat, and one that does is proved by the next read, not by this.
        seenAt = now;
        unanswered = 0;
        if (repairs >= limit)
        {
            if (givenUp) { return Verdict.None; }
            givenUp = true;
            return Verdict.GiveUp;
        }
        repairs++;
        Stalled = stalled;
        return Verdict.Rebuild;
    }
}
