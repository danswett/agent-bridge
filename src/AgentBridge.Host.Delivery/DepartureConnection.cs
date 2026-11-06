namespace AgentBridge.Host.Delivery;

public readonly record struct DepartureConnectionSnapshot(long Generation, bool Negotiated, long Revision);

// A transport publishes to one stable object. Final fence commitment and publication share
// this library-owned lock; no transport/owner callback is invoked under the delivery lock.
public sealed class DepartureConnection(long generation = 0, bool negotiated = false)
{
    internal Lock Sync { get; } = new();
    long currentGeneration = generation;
    bool currentCapability = negotiated;
    long revision;

    public DepartureConnectionSnapshot Snapshot
    {
        get { lock (Sync) { return new(currentGeneration, currentCapability, revision); } }
    }

    public void Publish(long nextGeneration, bool capability)
    {
        lock (Sync)
        {
            if (nextGeneration < currentGeneration) { throw new ArgumentOutOfRangeException(nameof(nextGeneration)); }
            if (nextGeneration != currentGeneration || capability != currentCapability) { revision = checked(revision + 1); }
            currentGeneration = nextGeneration;
            currentCapability = capability;
        }
    }

    internal bool Matches(long expectedGeneration, bool expectedCapability, long expectedRevision) =>
        currentGeneration == expectedGeneration && currentCapability == expectedCapability && revision == expectedRevision;
}
