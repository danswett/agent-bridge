using System.Globalization;
using System.Text.Json.Nodes;

namespace AgentBridge.HomeAssistant;

// #3204: agent messages sent while a question is open wait in memory, in order, for it to close.
// A restart or the session's end drops them; reply_queue_count says how many wait. The Send press
// that follows each one is the agent's own, never the owner's confirmation of the form.
public sealed partial class HaConnector
{
    int QueueCount(string sessionId) => ridLogs.TryGetValue(sessionId, out var log) ? log.Queued.Count : 0;

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
            var full = known is null && queue.Count >= MaxWaiting;
            if (known is null && !full) { queue.Add(state); }
            p.Reply.Ack = MakeAck(rid, CleanSender(Str(a, "sender")), full ? "refused" : known ?? "queued", duplicate: known is not null);
            await TransientAsync(p, full ? "Reply NOT sent" : "Reply " + (known ?? "queued"), full ? new JsonObject { ["error"] = "queue full" } : [], ct);
            p.Reply.Handle(state.State);
        }
    }

    // Queued messages go first once the question has closed, one delivery at a time.
    async Task<bool> DrainQueueAsync(string sessionId, Published p, CancellationToken ct)
    {
        while (p.Reply.Log.Queued.Count > 0)
        {
            var next = p.Reply.Log.Queued[0];
            var read = await CardReplyAsync(sessionId, p, next, ct, queued: true);
            p.Reply.Log.Queued.RemoveAt(0);
            if (read == CardRead.Delivering) { return true; }
        }
        return false;
    }
}
