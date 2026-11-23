using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

internal enum DeliveryClass { Owner, System, Agent }
internal enum LaneResult { Waiting, HandedOff, Unconfirmed }
internal sealed record LaneNotice(string Target, DeliveryHeader Item, string Reason, DeliveryNoticeCause Cause = DeliveryNoticeCause.Unknown) { public string From => "system"; }

// Host-only scheduler; the dispatch adapter lets ReplyService own its claim and typing slot.
internal sealed partial class DeliveryLaneScheduler(DeliveryJournal journal, DeliveryGate gate,
    Func<string, (bool? Idle, bool? Empty)> screen,
    Func<string, DeliveryHeader, string, Task> send, Action<LaneNotice> notice,
    TimeProvider time)
{
    sealed record Item(DeliveryHeader Header, string Body, DeliveryClass Sender);
    sealed class Lanes { public readonly List<Item> Urgent = []; public readonly List<Item> Queueable = []; }
    readonly object sync = new();
    readonly Dictionary<string, Lanes> sessions = new(StringComparer.Ordinal);
    bool uncertain;

    void Record(string session, DeliveryHeader header, string body, DeliveryState state, string? reason = null)
    {
        try { journal.Write(session, header, body, state, reason: reason); }
        catch { uncertain = true; gate.MarkJournalUncertain(); throw; }
    }

    public bool Enqueue(string session, DeliveryHeader header, string body, DeliveryClass sender)
    {
        List<LaneNotice> notices = [];
        try { return EnqueueCore(session, header, body, sender, notices); }
        finally { foreach (var n in notices) { notice(n); } }
    }

    bool EnqueueCore(string session, DeliveryHeader header, string body, DeliveryClass sender, List<LaneNotice> notices)
    {
        lock (sync)
        {
            if (uncertain) { throw new InvalidOperationException($"Target {session}: delivery journal uncertain."); }
            if (!Enum.IsDefined(sender)) { throw new ArgumentException($"Target {session}: unknown sender class."); }
            if (journal.State(session, header.Id) is not null) { return true; }
            var urgent = sender != DeliveryClass.Agent;
            (header, body) = DeliveryEnvelope.Prepare(header with { Seq = null, At = time.GetUtcNow(),
                Priority = urgent ? "urgent" : header.Priority == "fyi" ? "fyi" : "normal" }, body);
            _ = DeliveryEnvelope.Format(header with { Seq = "q1" }, body);
            if (!sessions.TryGetValue(session, out var lanes)) { sessions[session] = lanes = new(); }
            var displaced = urgent && lanes.Urgent.Count == 10 && sender == DeliveryClass.Owner
                ? lanes.Urgent.LastOrDefault(i => i.Sender != DeliveryClass.Owner) : null;
            if (!urgent && header.Priority == "fyi" && lanes.Queueable.Count == 50 &&
                !lanes.Queueable.Any(i => i.Header.Priority == "fyi"))
            {
                Record(session, header, body, DeliveryState.Dropped, "overflow");
                notices.Add(new(session, header, $"Target {session}: dropped overflow.", DeliveryNoticeCause.Overflow));
                return false;
            }
            if (urgent && lanes.Urgent.Count == 10 && displaced is null ||
                !urgent && lanes.Queueable.Count == 50 &&
                    !lanes.Queueable.Any(i => i.Header.Priority == "fyi"))
            {
                notices.Add(new(session, header, $"Target {session}: lane full; refused.", DeliveryNoticeCause.LaneFull));
                return false;
            }
            if ((!urgent || displaced is not null) && lanes.Queueable.Count == 50 &&
                lanes.Queueable.Any(i => i.Header.Priority == "fyi"))
            {
                var drop = lanes.Queueable.First(i => i.Header.Priority == "fyi");
                Record(session, drop.Header, drop.Body, DeliveryState.Dropped, "overflow");
                lanes.Queueable.Remove(drop);
                notices.Add(new(session, drop.Header, $"Target {session}: dropped overflow.", DeliveryNoticeCause.Overflow));
            }
            if (displaced is not null)
            {
                var moved = displaced with { Header = displaced.Header with { Priority = "normal" } };
                Record(session, moved.Header, moved.Body, DeliveryState.Demoted, "owner overflow");
                if (lanes.Queueable.Count == 50)
                {
                    Record(session, moved.Header, moved.Body, DeliveryState.Dropped, "overflow");
                    lanes.Urgent.Remove(displaced);
                    notices.Add(new(session, moved.Header, $"Target {session}: dropped overflow.", DeliveryNoticeCause.Overflow));
                }
                else
                {
                    lanes.Urgent.Remove(displaced);
                    lanes.Queueable.Add(moved);
                    notices.Add(new(session, moved.Header, $"Target {session}: demoted to queueable for owner.", DeliveryNoticeCause.DemotedForOwner));
                }
            }
            Record(session, header, body, DeliveryState.Enqueued);
            (urgent ? lanes.Urgent : lanes.Queueable).Add(new(header, body, sender));
            return true;
        }
    }

    public async Task<LaneResult> DeliverNextAsync(string session, CancellationToken cancellation = default)
    {
        if (Dispatch is not null) { return await DispatchNextAsync(session, cancellation); }
        using var admission = gate.TryEnterDelivery();
        var claim = Guid.NewGuid();
        if (admission is null || !gate.TryClaim(session, claim)) { return LaneResult.Waiting; }
        try
        {
            await gate.WaitToTypeAsync(cancellation);
            try
            {
                Item item;
                lock (sync)
                {
                    if (uncertain || !sessions.TryGetValue(session, out var lanes)) { return LaneResult.Waiting; }
                    var lane = lanes.Urgent.Count > 0 ? lanes.Urgent : lanes.Queueable;
                    if (lane.Count == 0) { return LaneResult.Waiting; }
                    var box = screen(session);
                    if (box.Empty != true || box.Idle is null || lane == lanes.Queueable && box.Idle != true)
                    { return LaneResult.Waiting; }
                    item = lane.OrderBy(i => lane == lanes.Urgent ? (int)i.Sender : 0).First();
                    try { item = item with { Header = journal.ReserveHandoff(session, item.Header, item.Body, lane == lanes.Urgent ? 'u' : 'q') }; }
                    catch { uncertain = true; gate.MarkJournalUncertain(); throw; }
                    lane.RemoveAll(i => i.Header.Id == item.Header.Id);
                }
                try { await send(session, item.Header, DeliveryEnvelope.Format(item.Header, item.Body)); }
                catch
                {
                    lock (sync) { uncertain = true; }
                    gate.MarkWorkerUncertain(null);
                    journal.Write(session, item.Header, item.Body, DeliveryState.Unconfirmed);
                    notice(new(session, item.Header, $"Target {session}: handoff unconfirmed; no retry.", DeliveryNoticeCause.Unconfirmed));
                    throw;
                }
                if (journal.Write(session, item.Header, item.Body, DeliveryState.Typed) == DeliveryState.Unconfirmed)
                {
                    lock (sync) { uncertain = true; }
                    gate.MarkJournalUncertain();
                    notice(new(session, item.Header, $"Target {session}: handoff unconfirmed; no retry.", DeliveryNoticeCause.Unconfirmed));
                    return LaneResult.Unconfirmed;
                }
                return LaneResult.HandedOff;
            }
            finally { gate.DoneTyping(); }
        }
        finally { gate.Release(session, claim); }
    }
}
