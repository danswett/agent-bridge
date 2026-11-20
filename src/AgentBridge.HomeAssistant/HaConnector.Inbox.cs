using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBridge.HomeAssistant;

// #3204: agent messages sent while a question is open wait, in order, for it to close; the session's
// end drops them; reply_queue_count says how many wait. The Send press that follows each one is the
// agent's own, never the owner's confirmation of the form. #2780: each is also kept in the durable inbox
// beside the state file, so a restart no longer drops them.
public sealed partial class HaConnector
{
    int QueueCount(string sessionId) => ridLogs.TryGetValue(sessionId, out var log) ? log.Queued.Count : 0;

    DurableInbox? inbox;
    DurableInbox? Inbox => ServesReplies && options.StateFile is { } file
        ? inbox ??= new DurableInbox(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, "ha-reply-inbox.json")) : null;

    bool Durably(string what, Action act)
    {
        try { act(); return true; }
        catch (Exception e) { log($"Home Assistant connector could not {what} in the reply inbox: {Describe(e)}"); return false; }
    }

    // The inbox shows only its oldest undelivered entry, so what a restart left there comes back one at
    // a time: the oldest first, and each next one once the one before is marked delivered, all ahead of
    // anything queued since (which the inbox holds after them). Marks owed go first, in the order taken.
    bool Restore(Published p)
    {
        var log = p.Reply.Log;
        if (log.Restored || Inbox is null) { return true; }
        if (!Mark(p)) { return false; }
        log.Since = time.GetUtcNow();
        return log.Restored = true;
    }

    bool Mark(Published p)
    {
        var log = p.Reply.Log;
        InboxMessage? head = null;
        while (true)
        {
            if (!Durably("read", () => head = Inbox!.Peek(p.Node))) { return false; }
            if (log.Marks.Count == 0) { break; }
            // Not the head: already marked (a session's end marks all), since the inbox keeps the queue's order.
            if (head?.Rid == log.Marks[0] && !Durably("mark a reply delivered", () => Inbox!.Deliver(p.Node, head.Rid, time.GetUtcNow()))) { return false; }
            log.Marks.RemoveAt(0);
        }
        if (head is null || log.Queued.Any(q => CleanRid(Str(q.Attributes, "rid")) == head.Rid)) { return true; }
        HaState? state = null;
        if (!Durably("read back a reply", () => state = JsonSerializer.Deserialize<HaState>(head.Payload)) || state is not { State: not null, Attributes.ValueKind: JsonValueKind.Object }) { return false; }
        log.Queued.Insert(0, state);
        return true;
    }

    // Kept before it is acked queued: one the inbox cannot keep is refused, never held in memory alone.
    // A rid already there is a duplicate: queued while it waits; once marked delivered, unconfirmed if
    // that was before this lifetime, else refused (its delivery was refused and the rid forgotten, and
    // the inbox can take it again only after the card closes).
    (string? Known, string? Error) Store(Published p, string rid, HaState state)
    {
        if (Inbox is not { } box) { return (null, null); }
        var at = time.GetUtcNow();
        InboxMessage? kept = null;
        if (!Restore(p) || !Durably("keep a reply", () => kept = box.Enqueue(p.Node, rid, CleanSender(Str(state.Attributes, "sender")), JsonSerializer.Serialize(state), at)))
        {
            return (null, "reply inbox unavailable");
        }
        if (kept!.DeliveredAt is null) { return (kept.QueuedAt == at ? null : "queued", null); }
        return kept.DeliveredAt < p.Reply.Log.Since ? ("unconfirmed", null) : (null, "reply inbox unavailable");
    }

    // A session's end drops its queue, and a session back under the same id starts empty: its entries are
    // marked delivered, the only way the inbox lets them go.
    void Discard(IEnumerable<string> sessionIds)
    {
        foreach (var node in sessionIds.Select(HaContract.NodeId))
        {
            if (Inbox is { } box) { Durably("drop an ended session's replies", () => { while (box.Peek(node) is { } m) { box.Deliver(node, m.Rid, time.GetUtcNow()); } }); }
        }
    }

    // The Send presses remembered for a session. For tests.
    internal int PressesKept(string sessionId) => ridLogs.TryGetValue(sessionId, out var log) ? log.Presses.Count : 0;

    static bool OwnerPress(RidLog log, string? press) => press is not null && log.Presses.TryGetValue(press, out var agent) && !agent;

    static void ClassifyPress(RidLog log, string press, bool uncertain)
    {
        var owed = log.AgentSends.Count > 0;
        if (!log.TakePress(press, owed || uncertain)) { return; }
        if (owed) { log.AgentSends.RemoveAt(0); }
        else if (uncertain)
        {
            log.Unmatched.Add(press);
            if (log.Unmatched.Count > 16) { log.Unmatched.RemoveAt(0); }
        }
    }

    // #3224: an agent payload read only after its Send was taken, unmatched, while no payload could be read owes
    // nothing more, as if it had been read in time. Only a press at or after the payload changed, both on Home
    // Assistant's clock, can be its companion: an owner's press before it still leaves the agent's Send owed.
    static bool SettledByEarlierPress(RidLog log, DateTimeOffset? written)
    {
        if (written is not { } changed) { return false; }
        var i = log.Unmatched.FindIndex(press => DateTimeOffset.TryParse(press, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) && at >= changed);
        if (i < 0) { return false; }
        log.Unmatched.RemoveAt(i);
        return true;
    }

    static bool RememberPayload(RidLog log, string stamp)
    {
        if (log.PayloadStamps.Contains(stamp)) { return false; }
        log.PayloadStamps.Add(stamp);
        if (log.PayloadStamps.Count > 64) { log.PayloadStamps.RemoveAt(0); }
        return true;
    }

    // What reached the card since the question was armed, in order: each agent message is counted
    // (text alone queued), and each new Send press is the agent's while one of its messages still
    // owes one. A press read when the payload cannot be is taken as the agent's: the owner presses again.
    async Task QueueAtQuestionAsync(Published p, Armed armed, IReadOnlyList<(long Seq, HaTrigger Hit)> seen, CancellationToken ct, Observation? current = null, Observation? submit = null)
    {
        var payloadId = SessionEntities.ReplyPayload(p.Node);
        var submitId = SessionEntities.Submit(p.Node);
        var log = p.Reply.Log;
        Restore(p);
        bool Known(string? stamp) => stamp is not null && (stamp == armed.PayloadBaseline.Value || p.Reply.IsHandled(stamp) || log.PayloadStamps.Contains(stamp));
        // Send first: an agent presses only after its message, so the payload read next shows it.
        submit ??= await ObserveAsync(submitId, ct);
        current ??= await ObserveAsync(payloadId, ct);
        var events = seen.Where(h => h.Seq > armed.Boundary && (h.Hit.EntityId == payloadId || h.Hit.EntityId == submitId)).Select(h => h.Hit).ToList();
        // #3224: a watch report of the same stamp with other attributes is a split, not this complete payload.
        if (Changed(armed.PayloadBaseline, current) && !events.Any(h => h.EntityId == payloadId && h.State.State == current.Value && Str(h.State.Attributes, "at") == current.Value))
        {
            events.Insert(events.FindLastIndex(h => h.EntityId == payloadId) + 1, new HaTrigger(payloadId, current.Snapshot!));
        }
        if (Changed(armed.SubmitBaseline, submit)) { events.Add(new HaTrigger(submitId, submit.Snapshot!)); }
        foreach (var (entity, state) in events)
        {
            if (!IsStamp(state.State)) { continue; }
            if (entity == submitId)
            {
                if (state.State != armed.SubmitBaseline.Value) { ClassifyPress(log, state.State, current.Kind == "unknown" || log.UncertainPayload is not null); }
                continue;
            }
            var a = state.Attributes;
            var mixed = MixedReplyPayload(payloadId, state);
            var stamp = state.State;
            if (mixed)
            {
                var at = Str(a, "at");
                if (!IsStamp(at ?? "")) { at = null; }
                if (Known(stamp) && Known(at)) { continue; }
                log.UncertainPayload = (stamp, at, Known(stamp) ? stamp : Known(at) ? at : null, state.LastUpdated);
                if (Known(stamp) && at is not null) { stamp = at; }
            }
            // Either half may arrive first. Only the newer complete payload (or its replacement),
            // never a lagging previous snapshot, discharges the uncertainty: with neither half known, a complete one
            // Home Assistant wrote after the split is the newer (#3224). A split whose attributes held no stamp missed
            // no publish, so its own stamp written complete later ends it too (#3298).
            else if (log.UncertainPayload is { } split && (stamp != split.Before
                ? split.Before is not null || split.At is null || (stamp != split.State && stamp != split.At) || state.LastUpdated > split.Updated
                : split.At is null && state.LastUpdated > split.Updated)) { log.UncertainPayload = null; }
            if (!mixed && Str(a, "driver") != "agent") { log.AgentSends.Remove(stamp); }
            if (!mixed && ((armed.PayloadBaseline.Present && stamp == armed.PayloadBaseline.Value) || p.Reply.IsHandled(stamp))) { continue; }
            var fresh = RememberPayload(log, stamp);
            // #3296: a split of two unseen publishes owes a Send for each, not only its state's: remembering the
            // attributes' stamp without its debt let its complete read clear the split and its agent Send answer.
            var unseen = mixed && log.UncertainPayload is { Before: null, At: { } other } && RememberPayload(log, other) ? other : null;
            var agent = mixed || Str(a, "driver") == "agent";
            // Press accounting is independent of payload acceptance and its fallible acknowledgement.
            // Every agent payload, even one naming the open question, owes its companion Send, unless an unread one
            // already came (#3224), including when a split recorded the debt before this complete read. Each half of a
            // split settles only a press at or after it was written: its stamp's last_changed, its attributes' last_updated.
            if (agent && (fresh || (!mixed && log.AgentSends.Contains(stamp))) && SettledByEarlierPress(log, stamp == state.State ? state.LastChanged : state.LastUpdated)) { log.AgentSends.Remove(stamp); }
            else if (fresh && agent) { log.AgentSends.Add(stamp); }
            if (unseen is not null && !SettledByEarlierPress(log, state.LastUpdated)) { log.AgentSends.Add(unseen); }
            if (mixed || Str(a, "driver") != "agent" || !string.IsNullOrEmpty(Str(a, "decision_id")) || !ServesReplies || CleanRid(Str(a, "rid")) is not { } rid
                || HasAttachments(state) || string.IsNullOrWhiteSpace(Str(a, "text"))) { continue; }
            var queue = log.Queued;
            var known = queue.Any(q => CleanRid(Str(q.Attributes, "rid")) == rid) ? "queued"
                : p.Reply.Rids.TryGetValue(rid, out var prior) ? prior ?? "unconfirmed" : null;
            var error = known is null && queue.Count >= MaxWaiting ? "queue full" : null;
            if (known is null && error is null && ((known, error) = Store(p, rid, state)) is (null, null)) { queue.Add(state); }
            p.Reply.Ack = MakeAck(rid, CleanSender(Str(a, "sender")), error is not null ? "refused" : known ?? "queued", duplicate: known is not null);
            await TransientAsync(p, error is not null ? "Reply NOT sent" : "Reply " + (known ?? "queued"), error is not null ? new JsonObject { ["error"] = error } : [], ct);
            p.Reply.Handle(state.State);
        }
    }

    // Queued messages go first once the question has closed, one delivery at a time; each taken is marked
    // delivered in the inbox.
    async Task<bool> DrainQueueAsync(string sessionId, Published p, CancellationToken ct)
    {
        Restore(p);
        while (p.Reply.Log.Queued.Count > 0)
        {
            var next = p.Reply.Log.Queued[0];
            var read = await CardReplyAsync(sessionId, p, next, ct, queued: true);
            p.Reply.Log.Queued.RemoveAt(0);
            if (Inbox is not null && CleanRid(Str(next.Attributes, "rid")) is { } rid) { p.Reply.Log.Marks.Add(rid); }
            Mark(p);
            if (read == CardRead.Delivering) { return true; }
        }
        return false;
    }
}
