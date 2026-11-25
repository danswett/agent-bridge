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
    // Keyed by peer + request, as the receiver's replay store is (#3926 review P2): one peer reusing another's live request id is not refused for it.
    readonly Dictionary<string, Record> records = new(StringComparer.Ordinal);
    // The slot: session id -> the key (peer + request) that claims it.
    readonly Dictionary<string, string> slots = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> sessionOf = new(StringComparer.Ordinal);
    readonly Dictionary<string, (string? Transcript, long Offset, DateTimeOffset? ProcessStart)> tracked = new(StringComparer.Ordinal);

    static string KeyOf(string principal, string request) => principal + '\u001f' + request;

    // The key of the record a caller means. With a peer it is exact; with none it is the one record that has this request id, and a request id
    // two peers both hold is ambiguous, so it names nothing and the caller does nothing rather than touch the wrong peer's record. Callers hold the lock.
    string? Find(string request, string? principal)
    {
        if (principal is not null) { var key = KeyOf(principal, request); return records.ContainsKey(key) ? key : null; }
        string? found = null;
        foreach (var r in records.Values.Where(r => r.Request == request)) { if (found is not null) { return null; } found = KeyOf(r.Principal, r.Request); }
        return found;
    }

    // Claims the session's slot and creates the in-flight record, before any key goes. Held: another relay message holds the slot, or this
    // request id was already begun. Full: no room for another record.
    public Begin TryBegin(string request, string principal, string session)
    {
        lock (gate)
        {
            Prune();
            var key = KeyOf(principal, request);
            if (records.ContainsKey(key) || slots.ContainsKey(session)) { return Begin.Held; }
            if (records.Count >= MaxRecords) { return Begin.Full; }
            records[key] = new Record(request, principal, InFlight, null, time.GetUtcNow(), null, false);
            slots[session] = key;
            sessionOf[key] = session;
            return Begin.Started;
        }
    }

    // The keys went: from now the prompt may sit in Claude's own queue.
    public void KeysWent(string request, string? principal = null)
    {
        lock (gate) { if (Find(request, principal) is { } key) { records[key] = records[key] with { Held = true }; } }
    }

    // A later state. held false frees the slot; queued sets until to the watch after now.
    public void Settle(string request, string state, string? reason, bool held, string? principal = null)
    {
        lock (gate)
        {
            if (Find(request, principal) is not { } key) { return; }
            var r = records[key];
            records[key] = r with { State = state, Reason = reason, Held = held, Until = state == Queued ? time.GetUtcNow() + Watch : r.Until };
            if (!held) { FreeSlot(key); }
        }
    }

    // Settle only while the record is still in the state the caller read (#4129): a send settled delivered or released in between is left as it is,
    // never moved back to queued or held with no slot. The check and the change are one step under the lock.
    public bool SettleIf(string request, Func<Record, bool> still, string state, string? reason, bool held, string? principal = null)
    {
        lock (gate)
        {
            if (Find(request, principal) is not { } key || !still(records[key])) { return false; }
            var r = records[key];
            records[key] = r with { State = state, Reason = reason, Held = held, Until = state == Queued ? time.GetUtcNow() + Watch : r.Until };
            if (!held) { FreeSlot(key); }
            return true;
        }
    }

    // Nothing was typed: the record and the slot go.
    public void Release(string request, string? principal = null)
    {
        lock (gate) { if (Find(request, principal) is { } key) { records.Remove(key); FreeSlot(key); } }
    }

    public Record? Get(string request, string? principal = null)
    {
        lock (gate) { return Find(request, principal) is { } key ? records[key] : null; }
    }

    public sealed record Holding(string Request, string? Transcript, long Offset, DateTimeOffset? ProcessStart = null, string? Principal = null);
    // Where a held send's transcript stands: the file and the offset it had when the send began, so a prompt Claude takes later can be found.
    public void Track(string request, string? transcript, long offset, DateTimeOffset? processStart = null, string? principal = null)
    {
        lock (gate) { if (Find(request, principal) is { } key) { tracked[key] = (transcript, offset, processStart); } }
    }

    public (string? Transcript, long Offset) Tracking(string request, string? principal = null)
    {
        lock (gate) { return Find(request, principal) is { } key && tracked.TryGetValue(key, out var where) ? (where.Transcript, where.Offset) : default; }
    }

    public Holding? Holder(string session)
    {
        lock (gate)
        {
            if (!slots.TryGetValue(session, out var key) || !records.TryGetValue(key, out var held)) { return null; }
            tracked.TryGetValue(key, out var where);
            return new Holding(held.Request, where.Transcript, where.Offset, where.ProcessStart, held.Principal);
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

    void FreeSlot(string key)
    {
        tracked.Remove(key);
        if (sessionOf.Remove(key, out var session) && slots.TryGetValue(session, out var holder) && holder == key) { slots.Remove(session); }
    }

    // Callers hold the lock. A record that is not held goes after the window; a held one stays until it is settled or released.
    void Prune()
    {
        var now = time.GetUtcNow();
        foreach (var r in records.Values.Where(r => !r.Held && now - r.At >= Window).ToList())
        {
            records.Remove(KeyOf(r.Principal, r.Request));
            FreeSlot(KeyOf(r.Principal, r.Request));
        }
    }
}
