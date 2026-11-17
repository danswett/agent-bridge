using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBridge.HomeAssistant;

public sealed partial class HaConnector
{
    readonly DurableInbox? inbox;
    readonly ConcurrentQueue<InboxMessage> inboxReceipts = new();

    bool StoreInbox(HaTrigger hit)
    {
        if (inbox is null || !ServesReplies || NodeOf(hit.EntityId) is not { } node ||
            hit.EntityId != SessionEntities.ReplyPayload(node)) { return false; }
        lock (gate)
        {
            var p = published.Values.FirstOrDefault(p => p.Node == node);
            var a = hit.State.Attributes;
            if (p is null || p.Armed is not null || !p.Reply.Baselined || !IsStamp(hit.State.State) ||
                CleanRid(Str(a, "rid")) is not { } rid || Str(a, "decision_id") is { Length: > 0 } ||
                Str(a, "at") != hit.State.State) { return false; }
            if (string.IsNullOrWhiteSpace(Str(a, "text")) && !HasAttachments(hit.State)) { return false; }
            if (p.Reply.IsHandled(hit.State.State) && inbox.Find(node, rid) is not null) { return true; }
            var message = inbox.Enqueue(node, rid, CleanSender(Str(a, "sender")), JsonSerializer.Serialize(hit.State), time.GetUtcNow());
            p.Reply.Handle(hit.State.State);
            p.Reply.ReadAgain = true;
            inboxReceipts.Enqueue(message);
            return true;
        }
    }

    static string InboxOutcome(InboxMessage message) =>
        message.Outcome ?? (message.DeliveredAt is null ? "queued" : "delivered");

    async Task InboxReceiptsAsync(CancellationToken ct)
    {
        while (inboxReceipts.TryPeek(out var message))
        {
            Published? p;
            lock (gate) { p = published.Values.FirstOrDefault(p => p.Node == message.Node); }
            if (p is not null)
            {
                p.Reply.Ack = MakeAck(message.Rid, message.Sender, InboxOutcome(message), message.DeliveredAt is not null);
                await TransientAsync(p, message.DeliveredAt is null ? "Reply queued" : "Duplicate reply dropped", [], ct);
            }
            inboxReceipts.TryDequeue(out _);
        }
    }

    void CompleteInbox(string node, string rid, string outcome)
    {
        if (inbox?.Peek(node)?.Rid != rid) { return; }
        if (outcome == "refused") { inbox.Forget(node, rid); }
        else { inbox.Deliver(node, rid, time.GetUtcNow(), outcome); }
    }

    async Task<bool> DispatchInboxAsync(string sessionId, Published p, CancellationToken ct)
    {
        if (inbox?.Peek(p.Node) is not { } next) { return false; }
        if (next.StartedAt is not null)
        {
            // A crash cannot tell an unrecorded submission from keys that never went.
            CompleteInbox(p.Node, next.Rid, "unconfirmed");
            p.Reply.Ack = MakeAck(next.Rid, next.Sender, "unconfirmed");
            await TransientAsync(p, "Reply unconfirmed - check the terminal",
                new JsonObject { ["error"] = "Delivery was interrupted; the reply was not automatically typed again." }, ct);
            log($"Home Assistant connector: interrupted reply {next.Rid} was not retyped");
        }
        else
        {
            var state = JsonSerializer.Deserialize<HaState>(next.Payload) ?? throw new InvalidDataException("Invalid inbox reply.");
            var prior = p.Reply.Ack;
            p.Reply.Ack = MakeAck(next.Rid, next.Sender, "queued");
            await TransientAsync(p, "Reply queued", [], ct);
            p.Reply.Ack = prior;
            inbox.Start(p.Node, next.Rid, time.GetUtcNow());
            var read = await CardReplyAsync(sessionId, p, state, ct, fromInbox: true);
            if (read == CardRead.Delivering) { return true; }
            if (read == CardRead.Nothing) { throw new InvalidDataException("Inbox entry is not a reply."); }
            var outcome = p.Reply.Ack?["outcome"]?.GetValue<string>() ?? throw new InvalidDataException("Inbox reply has no outcome.");
            CompleteInbox(p.Node, next.Rid, outcome);
        }
        p.Reply.ReadAgain = true;
        return true;
    }

    async Task RefuseInboxAtQuestionAsync(Published p, CancellationToken ct)
    {
        if (underWay.ContainsKey(p.SessionId)) { return; }
        while (inbox?.Peek(p.Node) is { } next)
        {
            var outcome = next.StartedAt is null ? "refused" : "unconfirmed";
            CompleteInbox(p.Node, next.Rid, outcome);
            p.Reply.Ack = MakeAck(next.Rid, next.Sender, outcome);
            if (outcome == "unconfirmed") { log($"Home Assistant connector: interrupted reply {next.Rid} was not retyped"); }
            await TransientAsync(p, "Reply NOT sent", new JsonObject { ["error"] = outcome == "refused"
                ? "The session is asking a question; retry after it closes." : "Delivery was interrupted; check the terminal before retrying." }, ct);
        }
        p.Reply.ReadAgain = false;
    }
}
