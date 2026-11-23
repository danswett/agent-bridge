namespace AgentBridge.Host.Lite;

// Memory-only status of busy-Claude sends (docs/message-delivery.md "Busy sends to Claude", #3828, #3856).
// Ids, a state and times only: never message text, never the target's name. Field names follow the receipt
// journal of the receipt-lookup design (#3875): request, principal, state. A host restart forgets everything.
public sealed class BusyQueueStore(TimeProvider time)
{
    public const int MaxRecords = 4096;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(150);
    public static readonly TimeSpan Watch = TimeSpan.FromSeconds(120);

    public const string InFlight = "in-flight", Queued = "queued", Delivered = "delivered", Unconfirmed = "unconfirmed";

    public sealed record Record(string Request, string Principal, string State, string? Reason, DateTimeOffset At, DateTimeOffset? Until, bool Held);

    public enum Begin { Started, Held, Full }

    readonly TimeProvider clock = time;

    public Begin TryBegin(string request, string principal, string session) => Begin.Started;
    public void KeysWent(string request) { }
    public void Settle(string request, string state, string? reason, bool held) { }
    public void Release(string request) { }
    public Record? Get(string request) => null;
    public bool SlotHeld(string session) => false;
    public IReadOnlyList<Record> Records() => [];
}
