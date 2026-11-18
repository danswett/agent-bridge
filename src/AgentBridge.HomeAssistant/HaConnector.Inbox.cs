using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBridge.HomeAssistant;

public sealed partial class HaConnector
{
    readonly DurableInbox? inbox;
    readonly ConcurrentQueue<(InboxMessage Message, bool Duplicate)> inboxReceipts = new();

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
            if (p.Reply.IsHandled(hit.State)) { return true; }
            var message = inbox.Enqueue(node, rid, CleanSender(Str(a, "sender")), JsonSerializer.Serialize(hit.State), time.GetUtcNow(), out var duplicate);
            p.Reply.Handle(hit.State);
            p.Reply.ReadAgain = true;
            inboxReceipts.Enqueue((message, duplicate));
            return true;
        }
    }

    static string InboxOutcome(InboxMessage message) =>
        message.Outcome ?? (message.DeliveredAt is null ? "queued" : "delivered");

    async Task InboxReceiptsAsync(CancellationToken ct)
    {
        while (inboxReceipts.TryPeek(out var receipt))
        {
            var message = receipt.Message;
            Published? p;
            lock (gate) { p = published.Values.FirstOrDefault(p => p.Node == message.Node); }
            if (p is not null)
            {
                var outcome = InboxOutcome(message);
                p.Reply.Ack = MakeAck(message.Rid, message.Sender, outcome, receipt.Duplicate && message.DeliveredAt is not null);
                await TransientAsync(p, outcome == "refused" ? "Reply NOT sent" :
                    message.DeliveredAt is null ? "Reply queued" : "Duplicate reply dropped",
                    outcome == "refused" ? new JsonObject { ["error"] = "This request was refused; check the session and inbox capacity before retrying with a new request id." } : [], ct);
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

}
