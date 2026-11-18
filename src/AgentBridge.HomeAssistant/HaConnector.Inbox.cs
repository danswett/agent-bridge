using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBridge.HomeAssistant;

public sealed partial class HaConnector
{
    DurableInbox? queuedInbox;
    IReadOnlyList<InboxMessage> inbox = [];
    int QueueCount(string sessionId) => inbox.Count(i => i.Node == sessionId && i.DeliveredAt is null);
    static string InboxStatus(InboxMessage item) => item.Outcome ?? (item.DeliveredAt is null ? "queued" : "delivered");

    void LoadInbox()
    {
        if (options.StateFile is not { } path) { return; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var store = new DurableInbox(path + ".inbox");
            inbox = store.Read();
            queuedInbox = store;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            log($"Home Assistant connector inbox unavailable: {Describe(e)}; queued replies are disabled");
        }
    }

    async Task QueueAtQuestionAsync(string sessionId, Published p, IReadOnlyList<(long Seq, HaTrigger Hit)> seen, CancellationToken ct, Observation? current = null)
    {
        if (!ServesReplies) { return; }
        current ??= await ObserveAsync(SessionEntities.ReplyPayload(p.Node), ct);
        if (current.Snapshot is { } sent && Str(sent.Attributes, "driver") == "agent"
            && string.IsNullOrEmpty(Str(sent.Attributes, "decision_id")))
        {
            var submit = await ObserveAsync(SessionEntities.Submit(p.Node), ct);
            // reply-session's companion press sends this payload, not the owner's form.
            if (DriverOf(submit.Snapshot?.UserId) == "agent") { p.Armed!.SpentPress = submit.Value; }
        }
        var states = seen.Where(h => h.Seq > p.Armed!.Boundary && h.Hit.EntityId == SessionEntities.ReplyPayload(p.Node)).Select(h => h.Hit.State);
        if (Changed(p.Armed!.PayloadBaseline, current)) { states = states.Append(current.Snapshot!); }
        foreach (var state in states)
        {
            var a = state.Attributes;
            if (!IsStamp(state.State) || p.Reply.IsHandled(state.State) || MixedReplyPayload(SessionEntities.ReplyPayload(p.Node), state)
                || Str(a, "driver") != "agent" || !string.IsNullOrEmpty(Str(a, "decision_id"))
                || CleanRid(Str(a, "rid")) is not { } rid) { continue; }
            var prior = inbox.FirstOrDefault(i => i.Node == sessionId && i.Rid == rid);
            var remembered = p.Reply.Rids.TryGetValue(rid, out var known);
            string? error = null;
            var outcome = prior is not null ? InboxStatus(prior) : remembered ? known ?? "unconfirmed" : "queued";
            if (prior is null && !remembered)
            {
                var payload = JsonSerializer.Serialize(state);
                if (QueueCount(sessionId) >= MaxWaiting) { error = "queue full"; }
                else if (Encoding.UTF8.GetByteCount(payload) > 65536) { error = "queued reply exceeds 64 KiB"; }
                else if (queuedInbox is null) { error = "durable reply queue unavailable"; }
                else
                {
                    try
                    {
                        queuedInbox.Enqueue(sessionId, rid, CleanSender(Str(a, "sender")), payload, time.GetUtcNow(), out var saved);
                        inbox = saved;
                    }
                    catch (InvalidOperationException) { error = "queue full"; }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                    {
                        log($"Home Assistant connector could not persist its inbox: {Describe(e)}");
                        error = "durable reply queue unavailable";
                    }
                }
            }
            if (error is not null) { outcome = "refused"; }
            p.Reply.Ack = MakeAck(rid, CleanSender(Str(a, "sender")), outcome, duplicate: prior is not null || remembered);
            await TransientAsync(p, error is null ? "Reply " + outcome : "Reply NOT sent",
                error is null ? [] : new JsonObject { ["error"] = error }, ct);
            p.Reply.Handle(state.State);
        }
    }

    void InboxOutcome(string sessionId, string rid, string outcome)
    {
        if (!inbox.Any(i => i.Node == sessionId && i.Rid == rid)) { return; }
        queuedInbox!.SetOutcome(sessionId, rid, outcome);
        inbox = [.. inbox.Where(i => outcome != "refused" || i.Node != sessionId || i.Rid != rid)
            .Select(i => i.Node == sessionId && i.Rid == rid ? i with { Outcome = outcome } : i)];
    }

    async Task<bool> DrainInboxAsync(string sessionId, Published p, CancellationToken ct)
    {
        var next = inbox.FirstOrDefault(i => i.Node == sessionId && i.DeliveredAt is null);
        if (next is null) { return false; }
        // Persist uncertainty BEFORE touching the terminal. A crash in that gap cannot safely
        // distinguish typed from not typed: never replay it automatically after restart.
        var dispatched = queuedInbox!.Deliver(sessionId, next.Rid, time.GetUtcNow(), "unconfirmed");
        inbox = [.. inbox.Select(i => i.Node == sessionId && i.Rid == next.Rid ? dispatched : i)];
        var state = JsonSerializer.Deserialize<HaState>(next.Payload) ?? throw new JsonException("missing queued reply");
        if (await CardReplyAsync(sessionId, p, state, ct, queued: true) != CardRead.Delivering)
        {
            InboxOutcome(sessionId, next.Rid, "refused");
        }
        return true;
    }
}
