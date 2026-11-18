using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery;

internal enum DeliverySender { Unknown, Agent, System, Owner }

// Inert, per-session selection only. Classification must come from host ingress, never a parsed label.
internal sealed class DeliveryLanes<T> where T : class
{
    readonly Lock gate = new();
    readonly PriorityQueue<(T Item, bool Known, bool Urgent), (int Rank, long Arrival)> pending = new();
    long arrival;
    T? active;

    public void Enqueue(T item, DeliverySender sender, ReplyIntent intent)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!Enum.IsDefined(sender) || !Enum.IsDefined(intent)) { throw new ArgumentException("unknown delivery classification"); }
        var urgent = intent == ReplyIntent.Steer && sender is DeliverySender.Owner or DeliverySender.System;
        var rank = urgent ? sender == DeliverySender.Owner ? 0 : 1 : 2;
        lock (gate) { pending.Enqueue((item, sender != DeliverySender.Unknown && intent != ReplyIntent.Unknown, urgent), (rank, checked(arrival++))); }
    }

    public T? TryTake(bool? idle, bool? inputEmpty)
    {
        lock (gate)
        {
            if (active is not null || idle is null || inputEmpty != true || !pending.TryPeek(out var next, out _)
                || !next.Known || idle != true && !next.Urgent) { return null; }
            pending.Dequeue();
            return active = next.Item;
        }
    }

    public void Complete(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        lock (gate)
        {
            if (!ReferenceEquals(active, item)) { throw new InvalidOperationException("delivery is not the active item"); }
            active = null;
        }
    }
}
