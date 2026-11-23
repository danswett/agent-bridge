namespace AgentBridge.Host;

// Observation only, for the dispatch pump and status: neither takes work, writes a record or changes a lane.
internal sealed partial class DeliveryLaneScheduler
{
    internal bool HasPending(string session)
    {
        lock (sync) { return Head(session) is not null; }
    }

    // True once a journal write failed or a handoff is unconfirmed: nothing more is admitted or dispatched.
    internal bool Uncertain
    {
        get { lock (sync) { return uncertain; } }
    }
}
