namespace AgentBridge.Host.Lite;

// Memory-only status of busy-Claude sends (docs/message-delivery.md "Busy sends to Claude", #3828, #3856).
// Ids, a state and times only: never message text, never the target's name. Field names follow the receipt
// journal of the receipt-lookup design (#3875): request, principal, state. A host restart forgets everything.
// One relay message per session claims the slot from TryBegin until it is released (nothing was typed) or settled
// as not held (delivered); queued and unconfirmed sends stay held, because Claude may still take the prompt.
public sealed class BusyQueueStore(TimeProvider time)
{
    public const int MaxRecords = 4096;
    // The receipt journal's window: a record that is not held is kept this long from its first offer.
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(150);
    // How long the host watches an accepted send before it reports unconfirmed (the item stays held).
    public static readonly TimeSpan Watch = TimeSpan.FromSeconds(120);

    public const string InFlight = "in-flight", Queued = "queued", Delivered = "delivered", Unconfirmed = "unconfirmed";

    public sealed record Record(string Request, string Principal, string State, string? Reason, DateTimeOffset At, DateTimeOffset? Until, bool Held);

    public enum Begin { Started, Held, Full }

    readonly object gate = new();
    readonly Dictionary<string, Record> records = new(StringComparer.Ordinal);
    // The slot: session id -> the request that claims it.
    readonly Dictionary<string, string> slots = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> sessionOf = new(StringComparer.Ordinal);
    readonly Dictionary<string, (string? Transcript, long Offset)> tracked = new(StringComparer.Ordinal);

    // Claims the session's slot and creates the in-flight record, before any key goes. Held: another relay message holds the slot, or this
    // request id was already begun. Full: no room for another record.
    public Begin TryBegin(string request, string principal, string session)
    {
        lock (gate)
        {
            Prune();
            if (records.ContainsKey(request) || slots.ContainsKey(session)) { return Begin.Held; }
            if (records.Count >= MaxRecords) { return Begin.Full; }
            records[request] = new Record(request, principal, InFlight, null, time.GetUtcNow(), null, false);
            slots[session] = request;
            sessionOf[request] = session;
            return Begin.Started;
        }
    }

    // The keys went: from now the prompt may sit in Claude's own queue.
    public void KeysWent(string request)
    {
        lock (gate) { if (records.TryGetValue(request, out var r)) { records[request] = r with { Held = true }; } }
    }

    // A later state. held false frees the slot; queued sets until to the watch after now.
    public void Settle(string request, string state, string? reason, bool held)
    {
        lock (gate)
        {
            if (!records.TryGetValue(request, out var r)) { return; }
            records[request] = r with { State = state, Reason = reason, Held = held, Until = state == Queued ? time.GetUtcNow() + Watch : r.Until };
            if (!held) { FreeSlot(request); }
        }
    }

    // Nothing was typed: the record and the slot go.
    public void Release(string request)
    {
        lock (gate) { records.Remove(request); FreeSlot(request); }
    }

    public Record? Get(string request)
    {
        lock (gate) { return records.TryGetValue(request, out var r) ? r : null; }
    }

    public sealed record Holding(string Request, string? Transcript, long Offset);
    // Where a held send's transcript stands: the file and the offset it had when the send began, so a prompt Claude takes later can be found.
    public void Track(string request, string? transcript, long offset)
    {
        lock (gate) { if (records.ContainsKey(request)) { tracked[request] = (transcript, offset); } }
    }

    public (string? Transcript, long Offset) Tracking(string request)
    {
        lock (gate) { return tracked.TryGetValue(request, out var where) ? where : default; }
    }

    public Holding? Holder(string session)
    {
        lock (gate)
        {
            if (!slots.TryGetValue(session, out var request)) { return null; }
            tracked.TryGetValue(request, out var where);
            return new Holding(request, where.Transcript, where.Offset);
        }
    }

    public bool SlotHeld(string session)
    {
        lock (gate) { return slots.ContainsKey(session); }
    }

    public IReadOnlyList<Record> Records()
    {
        lock (gate) { return [.. records.Values]; }
    }

    void FreeSlot(string request)
    {
        tracked.Remove(request);
        if (sessionOf.Remove(request, out var session) && slots.TryGetValue(session, out var holder) && holder == request) { slots.Remove(session); }
    }

    // Callers hold the lock. A record that is not held goes after the window; a held one stays until it is settled or released.
    void Prune()
    {
        var now = time.GetUtcNow();
        foreach (var r in records.Values.Where(r => !r.Held && now - r.At >= Window).ToList())
        {
            records.Remove(r.Request);
            FreeSlot(r.Request);
        }
    }
}
