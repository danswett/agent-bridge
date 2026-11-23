namespace AgentBridge.Host.Lite;

// S5: the one subscriber of LaneComposition.Notices. When the lane drops an item, or cannot confirm a handoff, it keeps one
// line about it so the sender can read it through the pull path (Query and Status); it types into no session. The record is
// memory for the host run, bounded, and holds ids and closed causes only: never the reason text and never message text.
// The journal keeps the drop itself.
internal sealed class LaneNoticeSink : IDisposable
{
    readonly LaneComposition? lane;
    readonly int capacity;
    readonly object sync = new();
    readonly Dictionary<Guid, string> described = [];
    readonly HashSet<(Guid, string)> seen = [];
    readonly Queue<(Guid Id, string Text)> order = new();

    public LaneNoticeSink(LaneComposition? lane, int capacity = 256)
    {
        this.lane = lane;
        this.capacity = Math.Max(1, capacity);
        if (lane is not null) { lane.Notices += Accept; }
    }

    public int Count { get { lock (sync) { return order.Count; } } }

    // S2b: told once per notice newly kept (a drop, a revoked-authority drop, an unconfirmed handoff), never with its id, cause or text:
    // the host's "Delivery failures" push only counts them.
    event Action? Counted;

    public IDisposable Forward(Action counted)
    {
        Counted += counted;
        return new Listener(() => Counted -= counted);
    }

    sealed class Listener(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }

    public string? Describe(Guid request)
    {
        lock (sync) { return described.GetValueOrDefault(request); }
    }

    internal void Accept(LaneNoticeEvent notice)
    {
        if (!Guid.TryParseExact(notice.Notice.Item.Id, "D", out var id) || Words(id, notice.Notice.Cause) is not { } text) { return; }
        lock (sync)
        {
            if (!seen.Add((id, text))) { return; }
            order.Enqueue((id, text));
            described[id] = text;
            while (order.Count > capacity)
            {
                var old = order.Dequeue();
                seen.Remove(old);
                if (described.GetValueOrDefault(old.Id) == old.Text) { described.Remove(old.Id); }
            }
        }
        // Outside the lock. A listener (the push) that fails must never break the lane that is telling the sender.
        try { Counted?.Invoke(); }
        catch (Exception) { /* nothing the lane can do about a listener */ }
    }

    // Only a drop or an unconfirmed handoff is told; a demotion or a full lane is not a lost item.
    static string? Words(Guid id, DeliveryNoticeCause cause) => cause switch
    {
        DeliveryNoticeCause.Overflow => $"dropped id={id:D} reason=overflow",
        DeliveryNoticeCause.RevokedAuthority => $"dropped id={id:D} reason=revoked authority",
        DeliveryNoticeCause.Unconfirmed => $"unconfirmed id={id:D}: may have been delivered; not retried",
        _ => null,
    };

    public void Dispose()
    {
        if (lane is not null) { lane.Notices -= Accept; }
    }
}
