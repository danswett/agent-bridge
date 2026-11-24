using System.Collections.Concurrent;
using AgentBridge.Host.Core.AgentMessaging;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Lite;

internal enum LaneStatus { Off, Refused, Ready }

internal readonly record struct LaneResolution(LaneStatus Status, DeliveryLaneScheduler? Scheduler = null, string? Reason = null);

// A scheduler notice with what the journal showed when it was raised. State is the journal's memory, not proof
// of durable acceptance: a refusal can have no record, and a failed append after a handoff reads Unconfirmed.
// Durable evidence is State not null with JournalUncertain false.
internal sealed record LaneNoticeEvent(LaneNotice Notice, DeliveryState? State, bool JournalUncertain);

// Composes the lane delivery path (docs/message-delivery.md, Phase 1) behind agentMessaging.laneDelivery AND
// liteKill.On. With either off, nothing here is built: no journal, no key, no scheduler. Only Resolve builds them.
// The journal cannot open: the lane refuses for the rest of this host run (a restart is the retry); there is no
// legacy idle-only fallback. Authority stays in memory and the journal is audit only (owner decision, Option A).
internal sealed class LaneComposition : IDisposable
{
    readonly Func<AgentMessagingSettings> settings;
    readonly Func<bool> modeOn;
    readonly DeliveryGate gate;
    readonly ISessionReplier replier;
    readonly Func<DeliveryJournal> open;
    readonly Action<string> log;
    readonly object sync = new();
    readonly ConcurrentQueue<LaneNoticeEvent> pending = new();
    DeliveryJournal? journal;
    DeliveryLaneScheduler? scheduler;
    string? refusal;
    int draining;

    public LaneComposition(Func<AgentMessagingSettings> settings, Func<bool> modeOn, DeliveryGate gate, ISessionReplier replier,
        LaneScreens screens, string folder, Action<string> log, TimeProvider? time = null,
        Func<DeliveryJournal>? open = null)
    {
        this.settings = settings;
        this.modeOn = modeOn;
        this.gate = gate;
        this.replier = replier;
        Screens = screens;
        this.log = log;
        Time = time ?? TimeProvider.System;
        this.open = open ?? (() => new DeliveryJournal(Path.Combine(folder, "diagnostics-journal.jsonl"), log));
    }

    public TimeProvider Time { get; }

    // The screen snapshots the scheduler admits from; a pump awaits Screens.RefreshAsync(target) before it asks for a delivery.
    public LaneScreens Screens { get; }

    // Typed notices, in order, delivered by a drainer outside every scheduler and journal lock; subscribe once, for the host run.
    public event Action<LaneNoticeEvent>? Notices;

    // A new offer was accepted pending, so a pump can wake; raised on the offering thread after the scheduler returned.
    public event Action<string>? Offered;

    public bool Enabled()
    {
        try { return modeOn() && settings().LaneDelivery; }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
    }

    public LaneResolution Resolve()
    {
        if (!Enabled()) { return new(LaneStatus.Off); }
        lock (sync)
        {
            if (refusal is not null) { return new(LaneStatus.Refused, null, refusal); }
            if (scheduler is not null) { return new(LaneStatus.Ready, scheduler); }
            try
            {
                journal = open();
                scheduler = new(journal, gate, Screens.Get, (_, _, _) => Task.FromException(new InvalidOperationException("The lane sends only through its dispatcher.")),
                    Capture, Time)
                { Dispatch = (request, cancellation) => replier.ReplyAsync(request, null, cancellation) };
                return new(LaneStatus.Ready, scheduler);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                journal?.Dispose();
                journal = null;
                scheduler = null;
                refusal = "the delivery journal could not be opened, so lane delivery is refused";
                log($"lane delivery refused for this run: the delivery journal could not be opened ({e.GetType().Name}); there is no fallback");
                return new(LaneStatus.Refused, null, refusal);
            }
        }
    }

    public DeliveryOfferStatus Offer(Guid request, string target, DeliverySender sender, string body, Func<bool> current,
        Func<bool>? revoked = null)
    {
        if (Resolve() is not { Status: LaneStatus.Ready, Scheduler: { } lane })
        { return new(DeliveryOfferState.Refused, $"Target {target}: lane delivery is not available."); }
        // The scheduler keeps this predicate for every later fence, so it must carry the switches too.
        var status = lane.Offer(request, target, sender, body, () => Enabled() && current(), revoked);
        if (status.State is DeliveryOfferState.Pending or DeliveryOfferState.Queued) { Raise(Offered, target); }
        return status;
    }

    // These observe only: they never build the journal or the scheduler.
    DeliveryLaneScheduler? Built { get { lock (sync) { return scheduler; } } }

    public bool HasPending(string session) => Built?.HasPending(session) == true;

    public bool JournalUncertain { get { lock (sync) { return journal?.Uncertain == true; } } }

    public bool Uncertain => JournalUncertain || Built?.Uncertain == true;

    public DeliveryOfferStatus? Status(Guid request, string target, DeliverySender sender) => Built?.Query(request, target, sender);

    // The scheduler can raise a notice while it holds its own lock, so subscribers are never called here.
    void Capture(LaneNotice notice)
    {
        DeliveryState? state = null;
        try { state = journal?.State(notice.Target, notice.Item.Id); }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        pending.Enqueue(new(notice, state, journal?.Uncertain == true));
        if (Interlocked.CompareExchange(ref draining, 1, 0) == 0) { _ = Task.Run(Drain); }
    }

    void Drain()
    {
        do
        {
            while (pending.TryDequeue(out var notice))
            {
                log($"lane notice: target {notice.Notice.Target} id {notice.Notice.Item.Id} state {notice.State?.ToString() ?? "none"}: {notice.Notice.Reason}");
                if (Notices is { } handlers)
                {
                    foreach (var handler in handlers.GetInvocationList().Cast<Action<LaneNoticeEvent>>()) { Call(() => handler(notice)); }
                }
            }
            Volatile.Write(ref draining, 0);
        }
        while (!pending.IsEmpty && Interlocked.CompareExchange(ref draining, 1, 0) == 0);
    }

    void Raise(Action<string>? handlers, string target)
    {
        if (handlers is null) { return; }
        foreach (var handler in handlers.GetInvocationList().Cast<Action<string>>()) { Call(() => handler(target)); }
    }

    void Call(Action action)
    {
        try { action(); }
        catch (Exception e) when (e is not OutOfMemoryException) { log($"lane subscriber failed ({e.GetType().Name}); delivery is unaffected"); }
    }

    public void Dispose() { lock (sync) { journal?.Dispose(); } }
}
