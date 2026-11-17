namespace AgentBridge.App.Core;

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
