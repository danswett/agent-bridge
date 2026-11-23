using AgentBridge.Host.Core;
using AgentBridge.Host.Lite;
using System.Runtime.CompilerServices;

namespace AgentBridge.Host;

internal sealed class DeliveryDispatchPump : IAsyncDisposable
{
    sealed class Run
    {
        public readonly object Sync = new();
        public readonly HashSet<string> Unconfirmed = new(StringComparer.Ordinal);
        public bool Uncertain;
        public DeliveryDispatchPump? Owner;
    }
    sealed class Slot
    {
        public Task Flight = Task.CompletedTask;
        public ITimer? Timer;
        public bool Dirty, Removed, Running;
        public int Delay = 250, Epoch;
    }
    // Session discovery lives for the host run: replacing a pump or toggling the flag cannot clear its latches.
    static readonly ConditionalWeakTable<ISessionSource, Run> runs = new();
    readonly Run run;
    readonly LaneComposition lane;
    readonly ISessionSource source;
    readonly Action<string> log;
    readonly Dictionary<string, Slot> slots = new(StringComparer.Ordinal);
    bool stopping;

    public DeliveryDispatchPump(LaneComposition lane, ISessionSource source, Action<string> log)
    {
        this.lane = lane; this.source = source; this.log = log;
        run = runs.GetValue(source, _ => new());
        lock (run.Sync)
        {
            if (run.Owner is not null) { throw new InvalidOperationException("The host already owns a lane pump."); }
            run.Owner = this;
        }
        lane.Offered += Wake; lane.Notices += Notice; source.Changed += Changed;
    }

    bool Available()
    {
        if (lane.Uncertain || lane.JournalUncertain) { Stop(true); return false; }
        lock (run.Sync) { return !stopping && !run.Uncertain; }
    }
    bool Current(string session, Slot slot)
    {
        lock (run.Sync) { return !stopping && !run.Uncertain && !slot.Removed && !run.Unconfirmed.Contains(session); }
    }
    public void Wake(string session)
    {
        if (!Available() || !lane.Enabled() || !lane.HasPending(session)) { return; }
        lock (run.Sync)
        {
            if (stopping || run.Uncertain || run.Unconfirmed.Contains(session)) { return; }
            if (!slots.TryGetValue(session, out var slot)) { slots[session] = slot = new(); }
            if (slot.Removed) { return; }
            Cancel(slot); slot.Dirty = true; slot.Delay = 250;
            if (!slot.Running) { Start(session, slot); }
        }
    }
    void Changed(object? sender, SessionChange change)
    {
        lock (run.Sync)
        {
            if (slots.TryGetValue(change.Id, out var slot))
            {
                slot.Removed = change.Kind == SessionChangeKind.Removed;
                if (slot.Removed) { Cancel(slot); slot.Dirty = false; }
            }
        }
        if (change.Kind != SessionChangeKind.Removed) { Wake(change.Id); }
    }
    void Notice(LaneNoticeEvent notice) { if (notice.JournalUncertain) { Stop(true); } }
    void Start(string session, Slot slot)
    {
        slot.Running = true;
        slot.Flight = Task.Run(() => DispatchAsync(session, slot));
    }

    async Task DispatchAsync(string session, Slot slot)
    {
        try
        {
            lock (run.Sync) { slot.Dirty = false; }
            while (Current(session, slot) && Available() && lane.Enabled() && lane.HasPending(session))
            {
                lock (run.Sync) { slot.Dirty = false; }
                if (lane.Resolve() is not { Status: LaneStatus.Ready, Scheduler: { } scheduler }) { return; }
                await lane.Screens.RefreshAsync(session);
                if (!Current(session, slot) || !Available() || !lane.Enabled()) { return; }
                var result = await scheduler.DeliverNextAsync(session);
                if (result == LaneResult.Unconfirmed)
                {
                    lock (run.Sync) { run.Unconfirmed.Add(session); slot.Dirty = false; }
                    _ = Available();
                    return;
                }
                if (!Current(session, slot) || !Available() || !lane.HasPending(session)) { return; }
                lock (run.Sync)
                {
                    if (result == LaneResult.HandedOff) { slot.Delay = 250; }
                    else if (!slot.Dirty && Current(session, slot))
                    {
                        var epoch = ++slot.Epoch;
                        slot.Timer = lane.Time.CreateTimer(_ => Retry(session, slot, epoch), null,
                            TimeSpan.FromMilliseconds(slot.Delay), Timeout.InfiniteTimeSpan);
                        slot.Delay = Math.Min(slot.Delay * 2, 2000);
                        return;
                    }
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Stop(true);
            log($"lane pump failed ({e.GetType().Name}); stopped for this host run, no retry");
        }
        finally
        {
            lock (run.Sync)
            {
                if (slot.Dirty && Current(session, slot)) { Start(session, slot); }
                else
                {
                    slot.Running = false;
                    if (slot.Timer is null) { Cancel(slot); slots.Remove(session); }
                }
            }
        }
    }
    void Retry(string session, Slot slot, int epoch)
    {
        lock (run.Sync)
        {
            if (slot.Epoch != epoch || !Current(session, slot)) { return; }
            Cancel(slot); slot.Dirty = true;
            if (!slot.Running) { Start(session, slot); }
        }
    }
    static void Cancel(Slot slot) { slot.Epoch++; slot.Timer?.Dispose(); slot.Timer = null; }
    void Stop(bool uncertain)
    {
        lock (run.Sync)
        {
            run.Uncertain |= uncertain;
            if (stopping) { return; }
            stopping = true;
            foreach (var slot in slots.Values) { Cancel(slot); slot.Dirty = false; }
        }
        lane.Offered -= Wake; lane.Notices -= Notice; source.Changed -= Changed;
    }
    public void BeginStop() => Stop(false);

    internal async Task WhenIdleAsync()
    {
        while (true)
        {
            Task[] flights;
            lock (run.Sync) { flights = slots.Values.Select(s => s.Flight).Where(t => !t.IsCompleted).ToArray(); }
            if (flights.Length == 0) { return; }
            await Task.WhenAll(flights);
        }
    }
    public async ValueTask DisposeAsync()
    {
        BeginStop(); await WhenIdleAsync();
        lock (run.Sync) { if (ReferenceEquals(run.Owner, this)) { run.Owner = null; } }
    }
}
