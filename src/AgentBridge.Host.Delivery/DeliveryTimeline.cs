using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery;

// What a caller hears of one answer or reply as it goes, and how it tells the host's log when
// the result went out. A repeat of a request still being delivered gets its own watch on the
// first one's delivery: the stages reached so far at once, the rest as they come.
public sealed class DeliveryWatch(Action<bool>? typed = null, bool sendsResult = false)
{
    // Raised once every key has gone, with whether the session was working then (it takes the
    // prompt at its next step). Raised on the delivery's own path: it must not block.
    internal Action<bool>? Typed => typed;

    // The caller sends the result itself and will say when (ResultSent), so the delivery's timing
    // line can say when it went. Only the request that started the delivery is waited for.
    internal bool SendsResult => sendsResult;

    internal DeliveryTimeline? Timeline { get; set; }

    // When this copy of the request reached the delivery, in ms after the first copy did: 0 but
    // for a repeat.
    internal long Arrived { get; set; }

    // The delivery's timings so far, from this copy's own arrival, or null for a request refused
    // before it began.
    public DeliveryTimings? Timings => Timeline?.Timings(this);

    public void ResultSent(bool sent) => Timeline?.Sent(this, sent);
}

// When each stage of one delivery came, in milliseconds after the request was received, by the
// delivery's own clock (options.Time), for the result's timings and one log line:
//   reply 0b7e3c1d timings (ms after 2026-11-04T06:56:28.441Z): checked 2, turn 3, inject 4-25, typed 25, recorded 1190, confirmed 1361, settled 1361, sent 1362
// The log's own timestamps are to the second, too coarse to say where a reply's second went (#597).
// The receipt time is UTC, in ISO 8601 with a Z, the owner's rule for diagnostic timestamps (#611):
// it reads the same on every machine and against agents' transcripts, which are in UTC too, where
// the log's own zone-less local time does not. Pacific is for what people are shown.
internal sealed class DeliveryTimeline
{
    readonly TimeProvider time;
    readonly string kind;
    readonly Guid key;
    readonly Action<string> log;
    readonly long start;
    readonly DateTimeOffset startUtc;
    readonly Lock gate = new();
    readonly List<DeliveryWatch> watches = [];
    DeliveryWatch? sender;
    long? checkedAt, turn, injectStart, injectEnd, typed, recorded, confirmed, settled, sent;
    bool? working;
    bool unsent, written;

    public DeliveryTimeline(TimeProvider time, string kind, Guid key, Action<string> log)
    {
        this.time = time;
        this.kind = kind;
        this.key = key;
        this.log = log;
        start = time.GetTimestamp();
        // To the millisecond, as agents write their timestamps: a prompt recorded within the
        // request's own millisecond is 0, not before it.
        var now = time.GetUtcNow();
        startUtc = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMillisecond));
    }

    long Now() => (long)time.GetElapsedTime(start).TotalMilliseconds;

    public void Watch(DeliveryWatch? watch, bool first)
    {
        if (watch is null) { return; }
        bool? reached;
        lock (gate)
        {
            watch.Timeline = this;
            watch.Arrived = first ? 0 : Now();
            if (first && watch.SendsResult) { sender = watch; }
            reached = typed is null ? null : working ?? false;
            if (reached is null && settled is null) { watches.Add(watch); }
        }
        if (reached is { } w) { Raise(watch, w); }
    }

    // The checks made on arrival have passed, and the delivery waits for its turn to type.
    public void Checked() { lock (gate) { checkedAt ??= Now(); } }

    // It holds the one typing slot.
    public void Turn() { lock (gate) { turn ??= Now(); } }

    public void InjectStart() { lock (gate) { injectStart ??= Now(); } }

    public void InjectEnd() { lock (gate) { injectEnd = Now(); } }

    public void Typed(bool wasWorking)
    {
        DeliveryWatch[] tell;
        lock (gate)
        {
            if (typed is not null) { return; }
            typed = Now();
            working = wasWorking;
            tell = [.. watches];
        }
        foreach (var w in tell) { Raise(w, wasWorking); }
    }

    // The agent's own time for the prompt or answer it recorded, where its record has one.
    public void Recorded(DateTimeOffset? at)
    {
        if (at is not { } when) { return; }
        var ms = (long)(when - startUtc).TotalMilliseconds;
        // An agent's clock reads to the millisecond and is the same machine's: earlier than the
        // request means the record is not this one's, or the clock moved, and says nothing.
        if (ms < 0) { return; }
        lock (gate) { recorded ??= ms; }
    }

    public void Confirmed() { lock (gate) { confirmed ??= Now(); } }

    public void Settled()
    {
        lock (gate)
        {
            settled ??= Now();
            watches.Clear();
            if (sender is not null && sent is null && !unsent) { return; }
        }
        Write();
    }

    public void Sent(DeliveryWatch watch, bool wasSent)
    {
        lock (gate)
        {
            if (watch != sender || sent is not null || unsent) { return; }
            if (wasSent) { sent = Now(); } else { unsent = true; }
            if (settled is null) { return; }
        }
        Write();
    }

    // A repeat counts from its own arrival, as its client sent it; a stage reached before then
    // reads 0, already there when it came.
    public DeliveryTimings? Timings(DeliveryWatch watch)
    {
        lock (gate)
        {
            long? From(long? ms) => ms is { } v ? Math.Max(0, v - watch.Arrived) : null;
            return typed is null && recorded is null && confirmed is null ? null
                : new DeliveryTimings { Typed = From(typed), Recorded = From(recorded), Confirmed = From(confirmed) };
        }
    }

    void Write()
    {
        string line;
        lock (gate)
        {
            if (written) { return; }
            written = true;
            var parts = new List<string>();
            void Add(string name, long? ms) { if (ms is { } v) { parts.Add(FormattableString.Invariant($"{name} {v}")); } }
            Add("checked", checkedAt);
            Add("turn", turn);
            if (injectStart is { } a) { parts.Add(FormattableString.Invariant($"inject {a}-{(injectEnd is { } e ? e.ToString(System.Globalization.CultureInfo.InvariantCulture) : "?")}")); }
            Add("typed", typed);
            if (working == true) { parts.Add("working"); }
            Add("recorded", recorded);
            Add("confirmed", confirmed);
            Add("settled", settled);
            Add("sent", sent);
            if (unsent) { parts.Add("not sent"); }
            line = FormattableString.Invariant($"{kind} {key.ToString()[..8]} timings (ms after {startUtc.UtcDateTime:yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff'Z'}): {string.Join(", ", parts)}");
        }
        log(line);
    }

    static void Raise(DeliveryWatch watch, bool working)
    {
        try { watch.Typed?.Invoke(working); }
        catch (Exception) { }
    }
}
