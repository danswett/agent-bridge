using System.Text.Json.Nodes;

namespace AgentBridge.HomeAssistant;

// #3204: agent messages sent while a question is open wait in memory, in order, for it to close.
// A restart or the session's end drops them; reply_queue_count says how many wait. The Send press
// that follows each one is the agent's own, never the owner's confirmation of the form.
public sealed partial class HaConnector
{
    int QueueCount(string sessionId) => ridLogs.TryGetValue(sessionId, out var log) ? log.Queued.Count : 0;

    static bool OwnerPress(RidLog log, string? press) => press is not null && log.Presses.TryGetValue(press, out var agent) && !agent;

    // What reached the card since the question was armed, in order: each agent message is counted
    // (text alone queued), and each new Send press is the agent's while one of its messages still
    // owes one. A press read when the payload cannot be is taken as the agent's: the owner presses again.
    async Task QueueAtQuestionAsync(Published p, Armed armed, IReadOnlyList<(long Seq, HaTrigger Hit)> seen, CancellationToken ct)
    {
        var payloadId = SessionEntities.ReplyPayload(p.Node);
        var submitId = SessionEntities.Submit(p.Node);
        var log = p.Reply.Log;
        // Send first: an agent presses only after its message, so the payload read next shows it.
        var submit = await ObserveAsync(submitId, ct);
        var current = await ObserveAsync(payloadId, ct);
        var events = seen.Where(h => h.Seq > armed.Boundary && (h.Hit.EntityId == payloadId || h.Hit.EntityId == submitId)).Select(h => h.Hit).ToList();
        if (Changed(armed.PayloadBaseline, current) && !events.Any(h => h.EntityId == payloadId && h.State.State == current.Value))
        {
            events.Insert(events.FindLastIndex(h => h.EntityId == payloadId) + 1, new HaTrigger(payloadId, current.Snapshot!));
        }
        if (Changed(armed.SubmitBaseline, submit)) { events.Add(new HaTrigger(submitId, submit.Snapshot!)); }
        foreach (var (entity, state) in events)
        {
            if (!IsStamp(state.State)) { continue; }
            if (entity == submitId)
            {
                if (state.State == armed.SubmitBaseline.Value || !log.Presses.TryAdd(state.State, log.AgentSends > 0 || current.Kind == "unknown")) { continue; }
                log.AgentSends = Math.Max(0, log.AgentSends - 1);
                continue;
            }
            var a = state.Attributes;
            if ((armed.PayloadBaseline.Present && state.State == armed.PayloadBaseline.Value) || log.AgentStamps.Contains(state.State) || p.Reply.IsHandled(state.State)
                || MixedReplyPayload(payloadId, state) || Str(a, "driver") != "agent" || !string.IsNullOrEmpty(Str(a, "decision_id"))) { continue; }
            log.AgentStamps.Add(state.State);
            if (log.AgentStamps.Count > 64) { log.AgentStamps.RemoveAt(0); }
            log.AgentSends++;
            if (!ServesReplies || CleanRid(Str(a, "rid")) is not { } rid || HasAttachments(state) || string.IsNullOrWhiteSpace(Str(a, "text"))) { continue; }
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
