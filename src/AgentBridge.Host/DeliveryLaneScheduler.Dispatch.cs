using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

internal enum DeliveryOfferState { Pending, Queued, Unconfirmed, Delivered, Dropped, Refused }
internal sealed record DeliveryOfferStatus(DeliveryOfferState State, string? Reason = null);

internal sealed partial class DeliveryLaneScheduler
{
    readonly Dictionary<(string, string), (DeliverySender Sender, Func<bool> Current)> fences = [];
    readonly HashSet<string> dispatching = [];
    public Func<ReplyRequest, CancellationToken, Task<ReplyResult>>? Dispatch { private get; init; }

    public DeliveryOfferStatus Offer(Guid request, string target, DeliverySender sender, string body, Func<bool> current)
    {
        if (request == Guid.Empty || string.IsNullOrWhiteSpace(target) ||
            string.IsNullOrWhiteSpace(sender.Id) || string.IsNullOrWhiteSpace(body))
        { return new(DeliveryOfferState.Refused, $"Target {target}: invalid offer."); }
        var id = request.ToString("D");
        lock (sync)
        {
            if (journal.Find(request) is { } prior)
            {
                if (!SameSender(prior, target, sender) || !journal.MatchesOffer(prior, body))
                { return new(DeliveryOfferState.Refused, $"Target {target}: UUID belongs to another sender, target or body."); }
                if (journal.State(target, id) == DeliveryState.Dropped && prior.State != "dropped")
                { Record(target, prior.Header, body, DeliveryState.Dropped, "restart: pending payload unavailable"); }
                return Query(request, target, sender);
            }
            if (Dispatch is null || !current())
            { return new(DeliveryOfferState.Refused, $"Target {target}: no dispatcher or revoked authority."); }
            var header = new DeliveryHeader(sender.From, id, null, time.GetUtcNow(), "normal", 0);
            fences[(target, id)] = (sender, current);
            if (!Enqueue(target, header, body, sender.Owner ? DeliveryClass.Owner : DeliveryClass.Agent))
            { fences.Remove((target, id)); return new(DeliveryOfferState.Refused, $"Target {target}: lane full."); }
            return Query(request, target, sender);
        }
    }

    public DeliveryOfferStatus Query(Guid request, string target, DeliverySender sender)
    {
        lock (sync)
        {
            if (journal.Find(request) is not { } record || !SameSender(record, target, sender))
            { return new(DeliveryOfferState.Refused, $"Target {target}: unknown UUID or different sender/target."); }
            var state = journal.State(target, record.Header.Id) switch
            {
                DeliveryState.Delivered or DeliveryState.Acked => DeliveryOfferState.Delivered,
                DeliveryState.Handoff or DeliveryState.Typed or DeliveryState.Unconfirmed => DeliveryOfferState.Unconfirmed,
                DeliveryState.Dropped => DeliveryOfferState.Dropped,
                _ => DeliveryOfferState.Pending,
            };
            return new(state, record.Reason ?? (state == DeliveryOfferState.Pending ? "Memory only; not durable acceptance." :
                state == DeliveryOfferState.Dropped ? "Restart: pending payload unavailable." : null));
        }
    }

    static bool SameSender(DeliveryJournalRecord record, string target, DeliverySender sender) =>
        record.Session == target && record.Header.From.Split(" (", StringSplitOptions.None)[0] ==
            new DeliverySender(sender.Id, Owner: sender.Owner).From;

    Item? Head(string session)
    {
        if (!sessions.TryGetValue(session, out var lanes)) { return null; }
        var lane = lanes.Urgent.Count > 0 ? lanes.Urgent : lanes.Queueable;
        return lane.OrderBy(i => lane == lanes.Urgent ? (int)i.Sender : 0).FirstOrDefault();
    }

    async Task<LaneResult> DispatchNextAsync(string session, CancellationToken cancellation)
    {
        using var admission = gate.TryEnterDelivery();
        var attempt = Guid.NewGuid();
        Item item; SubmitKey submit; (DeliverySender Sender, Func<bool> Current) authority;
        lock (sync)
        {
            if (admission is null || uncertain || dispatching.Contains(session) || Head(session) is not { } head ||
                !fences.TryGetValue((session, head.Header.Id), out authority))
            { return LaneResult.Waiting; }
            var box = screen(session);
            if (QueueSubmitPolicy.Select(DeliveryTerminalKind.Unknown, box.Idle, box.Empty, head.Header.Priority == "urgent") is not { } key)
            { return LaneResult.Waiting; }
            item = head; submit = key; dispatching.Add(session);
        }
        DeliveryHeader? handedOff = null;
        bool Current()
        {
            lock (sync)
            {
                var box = screen(session);
                return !uncertain && !cancellation.IsCancellationRequested && authority.Current() &&
                    (handedOff is not null || ReferenceEquals(Head(session), item)) &&
                    QueueSubmitPolicy.Select(DeliveryTerminalKind.Unknown, box.Idle, box.Empty, item.Header.Priority == "urgent") == submit;
            }
        }
        string Handoff()
        {
            lock (sync)
            {
                if (handedOff is not null || !Current()) { throw new InvalidOperationException($"Target {session}: handoff is no longer current."); }
                try { handedOff = journal.ReserveHandoff(session, item.Header, item.Body, item.Header.Priority == "urgent" ? 'u' : 'q'); }
                catch { uncertain = true; gate.MarkJournalUncertain(); throw; }
                var lanes = sessions[session];
                lanes.Urgent.RemoveAll(i => i.Header.Id == item.Header.Id);
                lanes.Queueable.RemoveAll(i => i.Header.Id == item.Header.Id);
                return DeliveryEnvelope.Format(handedOff, item.Body);
            }
        }
        try
        {
            var result = await Dispatch!(new ReplyRequest { Id = session, Request = attempt.ToString("D"), Sender = authority.Sender,
                Text = item.Body, RequireIdle = item.Header.Priority != "urgent" && submit != SubmitKey.Queue, QueueSubmit = submit == SubmitKey.Queue,
                StillCurrent = Current, HandoffText = Handoff }, cancellation);
            if (handedOff is null) { return LaneResult.Waiting; }
            lock (sync)
            {
                var state = result.Outcome == AnswerOutcome.Delivered ? DeliveryState.Delivered : DeliveryState.Unconfirmed;
                var written = journal.Write(session, handedOff, item.Body, state, reason: result.Reason);
                if (journal.Uncertain) { uncertain = true; gate.MarkJournalUncertain(); }
                return written == DeliveryState.Delivered ? LaneResult.HandedOff : LaneResult.Unconfirmed;
            }
        }
        catch
        {
            if (handedOff is not null)
            {
                lock (sync) { uncertain = true; gate.MarkWorkerUncertain(null); journal.Write(session, handedOff, item.Body, DeliveryState.Unconfirmed); }
            }
            throw;
        }
        finally { lock (sync) { dispatching.Remove(session); if (handedOff is not null) fences.Remove((session, item.Header.Id)); } }
    }
}
