namespace AgentBridge.App.Core;

public sealed class LatestHatch
{
    public bool Used { get; private set; }
    public bool Pinned { get; private set; }

    public void Jump() => Used = Pinned = true;
    public void Release() => Pinned = false;
    public bool AtEnd(bool automatic) => Pinned || automatic;
    public double RestoreOffset(double offset, double extent, double? previousExtent) =>
        Math.Clamp(offset + (previousExtent is { } previous ? extent - previous : 0), 0, extent);
}

public readonly record struct LatestInputReceipt(Guid ViewId, long Sequence, double Offset, double Extent);

public readonly record struct LatestInputStamp(Guid ViewId, long Sequence)
{
    public LatestInputReceipt Arrived(double offset, double extent) => new(ViewId, Sequence, offset, extent);
}

public sealed class LatestReceiptGate(Guid viewId)
{
    long sequence;
    public Guid ViewId { get; } = viewId;

    public LatestInputStamp Issue() => new(ViewId, checked(++sequence));

    public bool Accepts(LatestInputReceipt receipt) =>
        sequence > 0 && receipt.ViewId == ViewId && receipt.Sequence == sequence;
}
