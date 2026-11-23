using AgentBridge.Host.Core.Diagnostics;
using AgentBridge.Host.Lite;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

internal sealed class DeliveryLaneNotices : ILaneNoticeStatusSource, IDisposable
{
    readonly LaneComposition lanes;
    readonly IDiagnosticsJournal journal;
    readonly Action<string> log;
    readonly string version;
    readonly object sync = new();
    LaneNoticeStatus? latest;
    bool disposed;
    public event Action? Changed;

    public DeliveryLaneNotices(LaneComposition lanes, IDiagnosticsJournal journal, Action<string> log, string version)
    {
        this.lanes = lanes; this.journal = journal; this.log = log; this.version = version;
        lanes.Notices += Observe;
    }

    public LaneNoticeStatus? Snapshot
    {
        get
        {
            if (Volatile.Read(ref disposed) || !lanes.Enabled()) { return null; }
            var value = Volatile.Read(ref latest);
            return lanes.JournalUncertain
                ? (value ?? new(DeliveryNoticeCause.Unknown, 0, false, true, false, null)) with { JournalUncertain = true }
                : value;
        }
    }

    void Observe(LaneNoticeEvent e)
    {
        var changed = false;
        try
        {
            lock (sync)
            {
                if (disposed || !lanes.Enabled()) { return; }
                var count = latest?.Count ?? 0;
                var at = lanes.Time.GetUtcNow();
                var value = new LaneNoticeStatus(e.Notice.Cause, Math.Min(count + 1, JournalContract.MaxEntries),
                    count >= JournalContract.MaxEntries, e.JournalUncertain, true, at);
                Volatile.Write(ref latest, value); changed = true;
                var refused = e.Notice.Cause == DeliveryNoticeCause.LaneFull;
                var outcome = e.JournalUncertain ? JournalOutcome.Failed : refused ? JournalOutcome.Refused : JournalOutcome.Unavailable;
                if (journal.Record(JournalContract.Draft(JournalKind.DeliveryLaneNotice, outcome, (int)e.Notice.Cause, version)))
                { Volatile.Write(ref latest, value with { DiagnosticsRejected = false }); }
                else { log("lane diagnostics record refused; notice remains visible in status"); }
            }
        }
        finally { if (changed) { Changed?.Invoke(); } }
    }

    public void Dispose()
    {
        lock (sync)
        {
            Volatile.Write(ref disposed, true);
            lanes.Notices -= Observe;
            Changed = null;
        }
    }
}
